#include "update_package.h"
#include <shellapi.h>
#include "update_install_guard.h"
#include <wchar.h>
#ifndef VEX_VERSION
#error VEX_VERSION must come from app/VERSION
#endif

namespace {
bool readManifest(const wchar_t* directory, vex::UpdateManifest& manifest) {
    wchar_t path[MAX_PATH];
    if (swprintf(path, MAX_PATH, L"%ls\\manifest.txt", directory) < 0) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char bytes[161]; DWORD length = 0;
    const BOOL read = ReadFile(file, bytes, sizeof(bytes), &length, nullptr);
    CloseHandle(file);
    vex::UpdateVersion current;
    return read && vex::parseUpdateManifest(bytes, length, manifest) &&
        vex::parseUpdateVersion(VEX_VERSION, strlen(VEX_VERSION), current) && manifest.version.newerThan(current);
}
struct PackageLease {
    HANDLE file = INVALID_HANDLE_VALUE;
    ~PackageLease() { close(); }
    void close() { if (file != INVALID_HANDLE_VALUE) { CloseHandle(file); file = INVALID_HANDLE_VALUE; } }
};
DWORD installPackage(const wchar_t* msi, const wchar_t* log, const vex::UpdateInstallation& installation) {
    wchar_t system[MAX_PATH], installer[MAX_PATH], command[2048], directory[MAX_PATH];
    const UINT length = GetSystemDirectoryW(system, MAX_PATH);
    if (!length || length >= MAX_PATH || swprintf(installer, MAX_PATH, L"%ls\\msiexec.exe", system) < 0) return ERROR_PATH_NOT_FOUND;
    wcscpy(directory, installation.directory);
    directory[wcslen(directory) - 1] = 0;
    if (swprintf(command, 2048,
        L"\"%ls\" /i \"%ls\" /passive /norestart REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable ALLUSERS=%ls MSIINSTALLPERUSER=%ls INSTALLFOLDER=\"%ls\" /l*v \"%ls\"",
        installer, msi, installation.machine ? L"1" : L"2", installation.machine ? L"\"\"" : L"1", directory, log) < 0) return ERROR_FILENAME_EXCED_RANGE;
    STARTUPINFOW startup = {}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    if (!CreateProcessW(installer, command, nullptr, nullptr, FALSE, 0, nullptr, directory, &startup, &process)) return GetLastError();
    CloseHandle(process.hThread);
    const DWORD wait = WaitForSingleObject(process.hProcess, INFINITE);
    DWORD error = ERROR_SUCCESS;
    if (wait != WAIT_OBJECT_0 || !GetExitCodeProcess(process.hProcess, &error)) error = GetLastError();
    CloseHandle(process.hProcess);
    return error;
}
void relaunch(const wchar_t* executable) {
    // The helper remains the original user even if Windows Installer requested elevation.
    wchar_t directory[MAX_PATH], command[MAX_PATH + 4];
    wcscpy(directory, executable);
    wchar_t* slash = wcsrchr(directory, L'\\');
    if (!slash) return;
    *slash = 0;
    swprintf(command, MAX_PATH + 4, L"\"%ls\"", executable);
    STARTUPINFOW startup = {}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    if (CreateProcessW(executable, command, nullptr, nullptr, FALSE, 0, nullptr, directory, &startup, &process)) {
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    } else MessageBoxW(nullptr, L"The update finished, but Vex could not restart. Open Vex from the Start menu.", L"Vex update", MB_OK | MB_ICONWARNING);
}
DWORD startupFailure(DWORD error) {
    wchar_t text[200];
    swprintf(text, 200, L"Vex updater could not start (Windows error %lu). Open Vex and try again.", error);
    MessageBoxW(nullptr, text, L"Vex update", MB_OK | MB_ICONWARNING);
    return error;
}
DWORD applyUpdate(int argc, wchar_t** argv) {
    if (argc != 7 || wcscmp(argv[1], L"--apply") || wcslen(argv[6]) >= MAX_PATH - 20) return startupFailure(ERROR_INVALID_PARAMETER);
    HANDLE inherited[4] = {};
    for (unsigned i = 0; i < 4; ++i) {
        const wchar_t* text = argv[i + 2];
        if (*text < L'1' || *text > L'9') return ERROR_INVALID_PARAMETER;
        wchar_t* end = nullptr;
        const unsigned long long value = wcstoull(text, &end, 10);
        if (!value || !end || *end) return ERROR_INVALID_PARAMETER;
        inherited[i] = reinterpret_cast<HANDLE>(uintptr_t(value));
    }
    const HANDLE parent = inherited[0], ready = inherited[1], approval = inherited[2], cancel = inherited[3];
    wchar_t executable[MAX_PATH]; DWORD length = MAX_PATH;
    if (!QueryFullProcessImageNameW(parent, 0, executable, &length)) return ERROR_INVALID_HANDLE;
    vex::UpdateInstallation installation;
    if (!vex::findUpdateInstallation(executable, installation)) return ERROR_UNKNOWN_PRODUCT;
    vex::UpdateInstallGuard guard;
    DWORD error = guard.acquire(executable);
    if (error) return error;
    if ((error = vex::updateInstallationInUse(executable, GetProcessId(parent), false))) return error;
    vex::UpdateManifest manifest;
    if (!readManifest(argv[6], manifest)) return ERROR_INVALID_DATA;
    wchar_t msi[MAX_PATH], log[MAX_PATH];
    swprintf(msi, MAX_PATH, L"%ls\\update.msi", argv[6]);
    swprintf(log, MAX_PATH, L"%ls\\install.log", argv[6]);
    PackageLease lease;
    if ((error = vex::verifyUpdatePackage(msi, manifest, &lease.file, cancel))) return error;
    // A read lease and the per-install mutex stay held from readiness through the MSI transaction.
    // Failures before this acknowledgement stay in the running app; they never close terminal sessions.
    if (!SetEvent(ready)) return GetLastError();
    // A committed approval must win even when the parent has already completed teardown.
    const HANDLE decision[] = {approval, cancel, parent};
    const DWORD decisionWait = WaitForMultipleObjects(3, decision, FALSE, 120000);
    if (decisionWait != WAIT_OBJECT_0) return decisionWait == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_CANCELLED;
    // The inherited process handle covers full PTY teardown and cannot be confused with a recycled PID.
    if (WaitForSingleObject(parent, 120000) != WAIT_OBJECT_0) return startupFailure(ERROR_TIMEOUT);
    if (!(error = vex::updateInstallationInUse(executable, 0, true))) error = installPackage(msi, log, installation);
    lease.close();
    guard.release();
    if (error) {
        wchar_t message[1024];
        if (error == ERROR_SUCCESS_REBOOT_REQUIRED) swprintf(message, 1024,
            L"Windows needs a restart to finish updating Vex. Your settings were preserved.\n\nInstaller log: %ls", log);
        else swprintf(message, 1024,
            L"Vex update failed (Windows error %lu). Close any other Vex windows and try again. Your settings were preserved.\n\nInstaller log: %ls", error, log);
        MessageBoxW(nullptr, message, L"Vex update", MB_OK | MB_ICONWARNING);
    }
    if (error != ERROR_SUCCESS_REBOOT_REQUIRED) relaunch(executable);
    // Keep the exact installed MSI source. Its cabinet can be required by repair, removal or rollback.
    // A later check evicts this directory only after it is no longer a registered install source.
    return error;
}
}
int main() {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return int(startupFailure(ERROR_INVALID_PARAMETER));
    const DWORD error = applyUpdate(argc, argv);
    LocalFree(argv);
    return int(error);
}
