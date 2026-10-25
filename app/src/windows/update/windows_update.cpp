#include "windows_update.h"
#include "update_package.h"
#include "update_download.h"
#include "update_handoff.h"
#include "update_install_guard.h"
#include <wchar.h>
#ifndef VEX_VERSION
#error VEX_VERSION must come from app/VERSION
#endif

namespace {
enum class UpdateState { idle, checking, current, downloading, verifying, ready, portable, failed, preparing, confirmation };
struct UpdateSnapshot {
    UpdateState state = UpdateState::idle;
    UpdateState failedFrom = UpdateState::checking;
    vex::UpdateManifest manifest;
    unsigned percent = 0;
    DWORD error = 0;
};
SRWLOCK updateLock = SRWLOCK_INIT;
UpdateSnapshot sharedSnapshot, snapshot;
HWND updateWindow = nullptr;
HANDLE worker = nullptr, workerStop = nullptr;
// Final snapshots precede thread exit; the next worker joins its predecessor without blocking the window.
HANDLE preparationPredecessor = nullptr;
vex::UpdateDownload download;
vex::UpdateHandoff handoff;
bool stopping = false, disabled = true, dismissed = false, manualCheck = false, installing = false;
wchar_t stage[MAX_PATH] = {}, executable[MAX_PATH] = {};
vex::UpdateInstallation installation;
NotificationSurface updateSurface;
ULONGLONG currentNoticeDeadline = 0;
void resetCurrentNoticeTimer() {
    KillTimer(updateWindow, windowsUpdateNoticeTimer);
    currentNoticeDeadline = 0;
    if (snapshot.state == UpdateState::current && manualCheck && !dismissed) {
        constexpr UINT lifetime = 5000;
        currentNoticeDeadline = GetTickCount64() + lifetime;
        SetTimer(updateWindow, windowsUpdateNoticeTimer, lifetime, nullptr);
    }
}
void publish(UpdateSnapshot value) {
    AcquireSRWLockExclusive(&updateLock);
    sharedSnapshot = value;
    if (!stopping && updateWindow) PostMessageW(updateWindow, windowsUpdateMessage, 0, 0);
    ReleaseSRWLockExclusive(&updateLock);
}
void publishProgress(void* context, unsigned percent) {
    auto& progress = *static_cast<UpdateSnapshot*>(context);
    progress.percent = percent;
    publish(progress);
}
void cleanupOldStages() {
    wchar_t local[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    if (!length || length >= MAX_PATH) return;
    wchar_t pattern[MAX_PATH];
    if (swprintf(pattern, MAX_PATH, L"%ls\\Vex-Update-*", local) < 0) return;
    WIN32_FIND_DATAW entry;
    HANDLE find = FindFirstFileW(pattern, &entry);
    if (find == INVALID_HANDLE_VALUE) return;
    FILETIME now; GetSystemTimeAsFileTime(&now);
    const uint64_t ticks = uint64_t(now.dwHighDateTime) << 32 | now.dwLowDateTime;
    do {
        const uint64_t created = uint64_t(entry.ftCreationTime.dwHighDateTime) << 32 | entry.ftCreationTime.dwLowDateTime;
        if (!vex::updateStageNameValid(entry.cFileName) || !(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
            (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) || ticks < created || ticks - created < 7ULL * 24 * 3600 * 10000000) continue;
        wchar_t path[MAX_PATH];
        if (swprintf(path, MAX_PATH, L"%ls\\%ls", local, entry.cFileName) > 0 && !vex::registeredUpdateSource(path)) vex::removeUpdateStage(path);
    } while (FindNextFileW(find, &entry));
    FindClose(find);
}
DWORD WINAPI checkUpdateWorker(void*) {
    cleanupOldStages();
    vex::removeUpdateStage(stage); stage[0] = 0;
    UpdateSnapshot next; next.state = UpdateState::checking;
    char bytes[160]; DWORD length = 0;
    DWORD error = download.readManifest(bytes, length);
    vex::UpdateVersion current;
    if (!error && (!vex::parseUpdateManifest(bytes, length, next.manifest) || !vex::parseUpdateVersion(VEX_VERSION, strlen(VEX_VERSION), current))) error = ERROR_INVALID_DATA;
    if (!error && !next.manifest.version.newerThan(current)) { next.state = UpdateState::current; publish(next); return 0; }
    if (!error && !vex::findUpdateInstallation(executable, installation)) { next.state = UpdateState::portable; publish(next); return 0; }
    if (!error) error = vex::createUpdateStage(stage);
    wchar_t partial[MAX_PATH] = {}, package[MAX_PATH] = {}, manifestPath[MAX_PATH] = {};
    if (!error) {
        if (swprintf(partial, MAX_PATH, L"%ls\\update.part", stage) < 0 || swprintf(package, MAX_PATH, L"%ls\\update.msi", stage) < 0 ||
            swprintf(manifestPath, MAX_PATH, L"%ls\\manifest.txt", stage) < 0) error = ERROR_FILENAME_EXCED_RANGE;
    }
    if (!error) {
        HANDLE file = CreateFileW(partial, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) error = GetLastError();
        else {
            next.state = UpdateState::downloading; publish(next);
            error = download.downloadPackage(next.manifest, file, publishProgress, &next);
            if (!error && !FlushFileBuffers(file)) error = GetLastError();
            CloseHandle(file);
        }
    }
    if (!error) {
        next.state = UpdateState::verifying; publish(next);
        error = vex::verifyUpdatePackage(partial, next.manifest, nullptr, workerStop);
    }
    if (!error && !MoveFileExW(partial, package, MOVEFILE_WRITE_THROUGH)) error = GetLastError();
    if (!error) {
        HANDLE file = CreateFileW(manifestPath, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) error = GetLastError();
        else {
            DWORD written = 0;
            if (!WriteFile(file, bytes, length, &written, nullptr) || written != length || !FlushFileBuffers(file)) error = ERROR_WRITE_FAULT;
            CloseHandle(file);
        }
    }
    if (error) { vex::removeUpdateStage(stage); stage[0] = 0; next.failedFrom = next.state; next.state = UpdateState::failed; next.error = error; }
    else next.state = UpdateState::ready;
    publish(next);
    return 0;
}
DWORD WINAPI prepareUpdateWorker(void*) {
    HANDLE previous = preparationPredecessor;
    preparationPredecessor = nullptr;
    if (previous) { WaitForSingleObject(previous, INFINITE); CloseHandle(previous); }
    UpdateSnapshot next;
    AcquireSRWLockShared(&updateLock); next = sharedSnapshot; ReleaseSRWLockShared(&updateLock);
    DWORD error = handoff.prepare(executable, stage);
    if (!error) {
        next.state = UpdateState::confirmation; publish(next);
        error = handoff.waitForDecision();
    }
    handoff.finish();
    if (error) {
        next.state = error == ERROR_CANCELLED ? UpdateState::ready : UpdateState::failed;
        next.failedFrom = UpdateState::preparing; next.error = error;
        publish(next);
    }
    return 0;
}
bool visible() {
    if (dismissed || snapshot.state == UpdateState::idle) return false;
    return manualCheck || snapshot.state == UpdateState::downloading || snapshot.state == UpdateState::verifying ||
        snapshot.state == UpdateState::ready || snapshot.state == UpdateState::portable || snapshot.state == UpdateState::preparing ||
        snapshot.state == UpdateState::confirmation || (snapshot.state == UpdateState::failed && snapshot.failedFrom != UpdateState::checking);
}
bool retryablePreflight() {
    return snapshot.state == UpdateState::failed && snapshot.failedFrom == UpdateState::preparing &&
        (snapshot.error == ERROR_BUSY || snapshot.error == ERROR_SHARING_VIOLATION || snapshot.error == ERROR_ACCESS_DENIED ||
         snapshot.error == ERROR_TIMEOUT || snapshot.error == ERROR_PROCESS_ABORTED);
}
void requestRestart() {
    if (disabled) {
        constexpr uint8_t title[] = "Update preview";
        constexpr uint8_t message[] = "This is a preview. No update will be installed and your terminals keep running.";
        vex_window_notice(VexNoticeRequest{VEX_NOTICE_INFO, {title, sizeof(title) - 1}, {message, sizeof(message) - 1}});
        return;
    }
    if (snapshot.state == UpdateState::ready || snapshot.state == UpdateState::confirmation || retryablePreflight()) PostMessageW(updateWindow, windowsUpdateMessage, 1, 0);
}
void activatePrimary() {
    switch (snapshot.state) {
        case UpdateState::ready: case UpdateState::confirmation: requestRestart(); break;
        case UpdateState::failed:
            if (retryablePreflight()) requestRestart(); else vex_update_check();
            break;
        case UpdateState::portable: {
            constexpr uint8_t url[] = "https://github.com/Rayzerrek/Vex/releases/latest";
            const int error = vex_open_url(url, sizeof(url) - 1);
            if (error) {
                snapshot.failedFrom = UpdateState::portable; snapshot.state = UpdateState::failed; snapshot.error = DWORD(error);
                vex_window_invalidate();
            }
            break;
        }
        default: break;
    }
}
}
extern "C" bool vex_update_can_start() {
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
    // Long portable paths do not support the MSI updater, but must remain launchable.
    if (!length || length >= MAX_PATH) return true;
    vex::UpdateInstallGuard guard;
    const DWORD error = guard.acquire(path);
    if (error) OutputDebugStringW(L"Vex update startup guard: installation is busy or inaccessible; startup declined.\n");
    return !error;
}
void initializeWindowsUpdates(HWND window, bool selftest) {
    updateWindow = window; disabled = selftest;
    const DWORD length = GetModuleFileNameW(nullptr, executable, MAX_PATH);
    if (!length || length >= MAX_PATH) disabled = true;
    if (!disabled) {
        workerStop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!workerStop) disabled = true;
        else {
            SetTimer(window, windowsUpdateTimer, 6 * 60 * 60 * 1000, nullptr);
            // HTTP stays on the worker; do not make an available update wait
            // behind an artificial startup delay before it can be discovered.
            checkWindowsUpdatesAutomatically();
        }
    }
}
void shutdownWindowsUpdates() {
    KillTimer(updateWindow, windowsUpdateTimer);
    KillTimer(updateWindow, windowsUpdateNoticeTimer);
    currentNoticeDeadline = 0;
    AcquireSRWLockExclusive(&updateLock);
    stopping = true; updateWindow = nullptr;
    ReleaseSRWLockExclusive(&updateLock);
    if (workerStop) SetEvent(workerStop);
    download.cancel(); handoff.cancel();
    if (worker) { WaitForSingleObject(worker, INFINITE); CloseHandle(worker); worker = nullptr; }
    if (workerStop) { CloseHandle(workerStop); workerStop = nullptr; }
    if (!installing) vex::removeUpdateStage(stage);
    updateSurface.reset();
}
extern "C" void vex_update_check() {
    if (disabled || stopping || installing) return;
    dismissed = false; manualCheck = true;
    if (worker && WaitForSingleObject(worker, 0) != WAIT_OBJECT_0) { vex_window_invalidate(); return; }
    if (snapshot.state == UpdateState::ready) { vex_window_invalidate(); return; }
    if (worker) { CloseHandle(worker); worker = nullptr; }
    snapshot = {}; snapshot.state = UpdateState::checking;
    resetCurrentNoticeTimer();
    publish(snapshot);
    worker = CreateThread(nullptr, 0, checkUpdateWorker, nullptr, 0, nullptr);
    if (!worker) { snapshot.state = UpdateState::failed; snapshot.error = GetLastError(); publish(snapshot); }
    vex_window_invalidate();
}
void checkWindowsUpdatesAutomatically() {
    if (snapshot.state == UpdateState::ready || (worker && WaitForSingleObject(worker, 0) != WAIT_OBJECT_0)) return;
    vex_update_check();
    manualCheck = false;
}
bool refreshWindowsUpdate() {
    const auto previous = snapshot.state;
    AcquireSRWLockShared(&updateLock); snapshot = sharedSnapshot; ReleaseSRWLockShared(&updateLock);
    if (snapshot.state != previous) resetCurrentNoticeTimer();
    vex_window_invalidate();
    return snapshot.state == UpdateState::confirmation && previous != UpdateState::confirmation;
}
void expireWindowsUpdateNotice() {
    // A queued WM_TIMER from an earlier check must not hide a newer result.
    if (!currentNoticeDeadline || GetTickCount64() < currentNoticeDeadline) return;
    KillTimer(updateWindow, windowsUpdateNoticeTimer);
    currentNoticeDeadline = 0;
    if (snapshot.state == UpdateState::current && !dismissed) {
        dismissed = true;
        vex_window_invalidate();
    }
}
extern "C" bool vex_update_ready() { return !disabled && (snapshot.state == UpdateState::ready || retryablePreflight()); }
extern "C" bool vex_update_prepared() { return !disabled && snapshot.state == UpdateState::confirmation && handoff.prepared(); }
extern "C" void vex_update_cancel_install() { handoff.cancel(); }
extern "C" bool vex_update_prepare() {
    if (disabled || !vex_update_ready() || installing || !worker) return false;
    preparationPredecessor = worker; worker = nullptr;
    handoff.begin();
    snapshot.state = UpdateState::preparing; publish(snapshot);
    worker = CreateThread(nullptr, 0, prepareUpdateWorker, nullptr, 0, nullptr);
    if (!worker) {
        const DWORD error = GetLastError();
        worker = preparationPredecessor; preparationPredecessor = nullptr;
        snapshot.state = UpdateState::ready; snapshot.failedFrom = UpdateState::preparing; snapshot.error = error; publish(snapshot); return false;
    }
    return true;
}
extern "C" bool vex_update_install() {
    if (disabled || installing || snapshot.state != UpdateState::confirmation || !handoff.commit()) return false;
    installing = true;
    return true;
}
uint32_t testWindowsUpdate(uint32_t command) {
    if (!disabled) return 0;
    if (command == 8) return visible() ? 1 : 0;
    if (command > 7 && command != 10 && command != 11) return 0;
    snapshot = {};
    snapshot.state = static_cast<UpdateState>(command >= 10 ? command - 2 : command);
    snapshot.manifest.version.parts[0] = 2;
    snapshot.manifest.version.parts[1] = 1;
    snapshot.percent = 47;
    snapshot.error = ERROR_CRC;
    dismissed = false; manualCheck = true;
    resetCurrentNoticeTimer();
    vex_window_invalidate();
    return 1;
}
NotificationHit windowsUpdatePointer(float x, float y, bool click) {
    const auto hit = updateSurface.pointer(x, y, visible());
    if (click && hit == NotificationHit::action) activatePrimary();
    else if (click && hit == NotificationHit::close) {
        if (snapshot.state == UpdateState::preparing || snapshot.state == UpdateState::confirmation) handoff.cancel();
        dismissed = true; vex_window_invalidate();
    }
    return hit;
}
bool windowsUpdateAnimationPending() { return updateSurface.animationPending(); }
bool windowsUpdateKey(uint32_t key, uint32_t modifiers) {
    // Modifiers use the existing platform bitset: shift=1, control=2, alt=4.
    if (!visible() || modifiers != 3) return false;
    if (key == 'U' && (snapshot.state == UpdateState::ready || snapshot.state == UpdateState::confirmation)) { requestRestart(); return true; }
    if (key == VK_OEM_PERIOD) {
        if (snapshot.state == UpdateState::preparing || snapshot.state == UpdateState::confirmation) handoff.cancel();
        dismissed = true; vex_window_invalidate(); return true;
    }
    return false;
}
float drawWindowsUpdate(UiRenderer& target, UiBrush& brush, IDWriteFactory* factory, IDWriteTextFormat* font, VexChromePalette palette, double now) {
    if (!visible()) return updateSurface.draw(target, brush, factory, font, font, palette, {}, false, 0, now, vex_window_animations_enabled());
    wchar_t title[100];
    const wchar_t* action = L"";
    switch (snapshot.state) {
        case UpdateState::checking: wcscpy(title, L"Checking for updates…"); break;
        case UpdateState::current: wcscpy(title, L"You're up to date"); break;
        case UpdateState::downloading: swprintf(title, 100, L"Downloading update · %u%%", snapshot.percent); break;
        case UpdateState::verifying: wcscpy(title, L"Verifying update…"); break;
        case UpdateState::preparing: wcscpy(title, L"Preparing restart…"); break;
        case UpdateState::confirmation: wcscpy(title, L"Update verified"); action = L"Update & restart"; break;
        case UpdateState::ready: swprintf(title, 100, L"Vex %u.%u.%u is ready", snapshot.manifest.version.parts[0], snapshot.manifest.version.parts[1], snapshot.manifest.version.parts[2]); action = L"Update & restart"; break;
        case UpdateState::portable: wcscpy(title, L"A new Vex is available"); action = L"Open downloads"; break;
        case UpdateState::failed:
            if (snapshot.error == ERROR_BUSY) wcscpy(title, L"Close other Vex windows first");
            else if (snapshot.error == ERROR_DISK_FULL) wcscpy(title, L"Not enough disk space");
            else if (snapshot.error == ERROR_ACCESS_DENIED) wcscpy(title, L"Update blocked by permissions");
            else if (snapshot.error == ERROR_SHARING_VIOLATION) wcscpy(title, L"Update file is busy");
            else if (snapshot.error == ERROR_CRC || snapshot.error == ERROR_INVALID_DATA) wcscpy(title, L"Update verification failed");
            else if (snapshot.error == ERROR_TIMEOUT && snapshot.failedFrom == UpdateState::preparing) wcscpy(title, L"Updater did not respond");
            else if (snapshot.error == ERROR_FILE_NOT_FOUND) wcscpy(title, snapshot.failedFrom == UpdateState::checking ? L"Update feed unavailable" : L"Update download unavailable");
            else swprintf(title, 100, L"Update failed · error %lu", snapshot.error);
            action = L"Try again"; break;
        default: return 0;
    }
    const auto tone = snapshot.state == UpdateState::failed ? NotificationTone::error :
        (snapshot.state == UpdateState::ready || snapshot.state == UpdateState::confirmation || snapshot.state == UpdateState::current) ? NotificationTone::success : NotificationTone::progress;
    return updateSurface.draw(target, brush, factory, font, font, palette,
        NotificationContent{tone, title, L"", action, snapshot.state == UpdateState::downloading ? (int)snapshot.percent : -1},
        true, 0, now, vex_window_animations_enabled());
}
