#include "windows_platform.h"
#include "windows_conpty.h"
#include "conpty_graphics_reply.h"
#include "windows_wsl_probe.h"
#include <windows.h>
#include <tlhelp32.h>
#include <winternl.h>
#include <stdlib.h>
#include <string.h>

HWND vex_native_window();
static void extractLowerStem(const wchar_t* path, wchar_t (&stem)[64]);

struct VexPty {
    uint32_t token;
    HWND window;
    VexPty* next;
    HANDLE input, output, process, job, reader, writer, exitWatcher, outputConsumed, inputReady;
    HPCON console;
    VexConptyApi conpty;
    WslProcessProbe* wslProbe;
    CRITICAL_SECTION inputLock;
    // A full clipboard paste must not starve protocol replies and kill an otherwise healthy pane.
    static constexpr size_t replyReserve = 64 * 1024;
    uint8_t inputQueue[1024 * 1024 + 12 + replyReserve];
    size_t inputHead, inputLength;
    uint8_t outputBuffer[65536];
    DWORD outputLength;
    DWORD testCloseDelayMs;
    volatile LONG stopping, outputPending;
    DWORD fgPid;
    wchar_t rootStem[64];
    bool declaredShell;
    DWORD shellVolume, shellFileHigh, shellFileLow;
    bool shellIdentityKnown;
    uint64_t fgCreateTime;
    uint8_t fgExe[256];
    size_t fgExeLen;
    uint8_t fgCmd[512];
    size_t fgCmdLen;
};

// Only the window thread edits this registry; workers carry their own stable owner.
static VexPty* sessions;
// A bounded native reaper pool owns detached sessions; UI callbacks never wait for ConPTY.
static SRWLOCK closingLock = SRWLOCK_INIT;
static CONDITION_VARIABLE closingChanged = CONDITION_VARIABLE_INIT;
static VexPty* closingHead;
static VexPty* closingTail;
struct PtyReaper { HANDLE thread; bool running; };
static PtyReaper reapers[4];
static size_t closingRunning, closingCount;
static volatile LONG forcedClosures;

static VexPty* findSession(uint32_t token) {
    for (auto session = sessions; session; session = session->next) if (session->token == token) return session;
    return nullptr;
}

static void postSessionError(VexPty* session, DWORD error) {
    if (!InterlockedCompareExchange(&session->stopping, 0, 0))
        PostMessageW(session->window, WM_APP + 3, session->token, error ? error : ERROR_WRITE_FAULT);
}

static DWORD WINAPI readPtyOutput(void* context) {
    auto session = static_cast<VexPty*>(context);
    bool drainOnly = false;
    for (;;) {
        DWORD length;
        if (!ReadFile(session->output, session->outputBuffer, sizeof(session->outputBuffer), &length, nullptr)) {
            DWORD error = GetLastError();
            if (error != ERROR_BROKEN_PIPE && error != ERROR_OPERATION_ABORTED) postSessionError(session, error);
            break;
        }
        if (!length) break;
        if (drainOnly || InterlockedCompareExchange(&session->stopping, 0, 0)) continue;
        session->outputLength = length;
        InterlockedExchange(&session->outputPending, 1);
        if (!PostMessageW(session->window, WM_APP + 1, session->token, 0)) {
            postSessionError(session, GetLastError());
            // Window destruction can race teardown; keep draining until ConPTY closes.
            drainOnly = true;
            continue;
        }
        if (WaitForSingleObject(session->outputConsumed, INFINITE) != WAIT_OBJECT_0) break;
    }
    // A failed reader must not leave ConPTY waiting for a client that can no longer drain.
    CloseHandle(session->output);
    session->output = nullptr;
    return 0;
}

static DWORD WINAPI watchProcessExit(void* context) {
    auto session = static_cast<VexPty*>(context);
    if (WaitForSingleObject(session->process, INFINITE) == WAIT_OBJECT_0 && !InterlockedCompareExchange(&session->stopping, 0, 0)) {
        DWORD code;
        if (GetExitCodeProcess(session->process, &code)) PostMessageW(session->window, WM_APP + 4, session->token, code);
        else postSessionError(session, GetLastError());
    }
    return 0;
}

static DWORD WINAPI writePtyInput(void* context) {
    auto session = static_cast<VexPty*>(context);
    uint8_t block[32768];
    for (;;) {
        if (InterlockedCompareExchange(&session->stopping, 0, 0)) return 0;
        EnterCriticalSection(&session->inputLock);
        size_t length = session->inputLength < sizeof(block) ? session->inputLength : sizeof(block);
        size_t first = length < sizeof(session->inputQueue) - session->inputHead ? length : sizeof(session->inputQueue) - session->inputHead;
        memcpy(block, session->inputQueue + session->inputHead, first);
        memcpy(block + first, session->inputQueue, length - first);
        session->inputHead = (session->inputHead + length) % sizeof(session->inputQueue);
        session->inputLength -= length;
        LeaveCriticalSection(&session->inputLock);
        if (!length) { WaitForSingleObject(session->inputReady, INFINITE); continue; }
        DWORD written;
        if (!WriteFile(session->input, block, (DWORD)length, &written, nullptr)) {
            DWORD error = GetLastError();
            // The exit watcher owns ordinary child exit; a queued keystroke must not kill the window.
            if (error != ERROR_BROKEN_PIPE || WaitForSingleObject(session->process, 0) != WAIT_OBJECT_0) postSessionError(session, error);
            return 0;
        }
        if (written != length) { postSessionError(session, ERROR_WRITE_FAULT); return 0; }
    }
}

static DWORD startPty(VexPty* session, uint16_t columns, uint16_t rows, VexPtyLaunch launch) {
    DWORD apiError = session->conpty.load();
    if (apiError) return apiError;
    extractLowerStem(reinterpret_cast<const wchar_t*>(launch.program), session->rootStem);
    session->declaredShell = launch.declared_shell;
    session->outputConsumed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!session->outputConsumed) return GetLastError();
    session->inputReady = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!session->inputReady) return GetLastError();
    HANDLE inputRead = nullptr, outputWrite = nullptr;
    if (!CreatePipe(&inputRead, &session->input, nullptr, 0)) return GetLastError();
    // Match the bounded reader block so bursts can drain in one handoff instead of 4 KiB round trips.
    if (!CreatePipe(&session->output, &outputWrite, nullptr, sizeof(session->outputBuffer))) {
        DWORD error = GetLastError(); CloseHandle(inputRead); return error;
    }
    HRESULT result = session->conpty.create(COORD{(SHORT)columns, (SHORT)rows}, inputRead, outputWrite, 0, &session->console);
    CloseHandle(inputRead); CloseHandle(outputWrite);
    if (FAILED(result)) return (DWORD)result;
    // Drain before spawning: startup failure must also be able to close ConPTY safely.
    session->reader = CreateThread(nullptr, 0, readPtyOutput, session, 0, nullptr);
    if (!session->reader) return GetLastError();
    session->writer = CreateThread(nullptr, 0, writePtyInput, session, 0, nullptr);
    if (!session->writer) return GetLastError();
    session->job = CreateJobObjectW(nullptr, nullptr);
    if (!session->job) return GetLastError();
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(session->job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return GetLastError();
    SIZE_T attributeSize = 0;
    InitializeProcThreadAttributeList(nullptr, 2, 0, &attributeSize);
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, attributeSize));
    if (!attributes) return ERROR_OUTOFMEMORY;
    if (!InitializeProcThreadAttributeList(attributes, 2, 0, &attributeSize)) {
        DWORD error = GetLastError(); HeapFree(GetProcessHeap(), 0, attributes); return error;
    }
    DWORD error = ERROR_SUCCESS;
    // Atomic job assignment avoids leaving a launcher suspended during DLL initialization.
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, session->console, sizeof(session->console), nullptr, nullptr) ||
        !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &session->job, sizeof(session->job), nullptr, nullptr)) error = GetLastError();
    else {
        STARTUPINFOEXW startup = {};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.lpAttributeList = attributes;
        PROCESS_INFORMATION process = {};
        if (!CreateProcessW(reinterpret_cast<const wchar_t*>(launch.program), reinterpret_cast<wchar_t*>(launch.command), nullptr, nullptr, FALSE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            const_cast<uint16_t*>(launch.environment), reinterpret_cast<const wchar_t*>(launch.directory), &startup.StartupInfo, &process)) error = GetLastError();
        else {
            session->process = process.hProcess;
            if (session->declaredShell) {
                HANDLE image = CreateFileW(reinterpret_cast<const wchar_t*>(launch.program), FILE_READ_ATTRIBUTES,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
                BY_HANDLE_FILE_INFORMATION info = {};
                if (image != INVALID_HANDLE_VALUE) {
                    session->shellIdentityKnown = GetFileInformationByHandle(image, &info) != FALSE;
                    session->shellVolume = info.dwVolumeSerialNumber;
                    session->shellFileHigh = info.nFileIndexHigh;
                    session->shellFileLow = info.nFileIndexLow;
                    CloseHandle(image);
                }
            }
            CloseHandle(process.hThread);
        }
    }
    DeleteProcThreadAttributeList(attributes);
    HeapFree(GetProcessHeap(), 0, attributes);
    if (!error) {
        session->exitWatcher = CreateThread(nullptr, 0, watchProcessExit, session, 0, nullptr);
        if (!session->exitWatcher) error = GetLastError();
    }
    return error;
}

static bool validLaunchString(const uint16_t* text, size_t length) {
    if (!text || !length || length >= 32767 || text[length] != 0) return false;
    for (size_t index = 0; index < length; ++index) if (!text[index]) return false;
    return true;
}
extern "C" int vex_pty_open(uint32_t token, uint16_t columns, uint16_t rows, VexPtyLaunch launch, VexPty** output) {
    if (!output || token == 0 || columns < 2 || rows == 0 || columns > 32767 || rows > 32767 || findSession(token) ||
        !validLaunchString(launch.program, launch.program_length) || !validLaunchString(launch.command, launch.command_length) ||
        !validLaunchString(launch.directory, launch.directory_length) || !launch.environment || launch.environment_length < 2 ||
        launch.environment_length > 1024 * 1024 || launch.environment[launch.environment_length - 1] != 0 || launch.environment[launch.environment_length - 2] != 0) return ERROR_INVALID_PARAMETER;
    *output = nullptr;
    AcquireSRWLockShared(&closingLock);
    const bool busy = closingCount >= 16;
    ReleaseSRWLockShared(&closingLock);
    // Bound processes, pipe workers and retained queues during rapid tab churn.
    if (busy) return ERROR_BUSY;
    auto session = static_cast<VexPty*>(calloc(1, sizeof(VexPty)));
    if (!session) return ERROR_OUTOFMEMORY;
    session->token = token;
    session->window = vex_native_window();
    InitializeCriticalSection(&session->inputLock);
    session->next = sessions;
    sessions = session;
    DWORD error = startPty(session, columns, rows, launch);
    if (!error && launch.wsl_probe_command_length) {
        if (!validLaunchString(launch.wsl_probe_command, launch.wsl_probe_command_length)) error = ERROR_INVALID_PARAMETER;
        else {
            session->wslProbe = createWslProcessProbe(reinterpret_cast<const wchar_t*>(launch.wsl_probe_command), session->window, token);
            // Process metadata is optional; an unavailable probe must not kill a live shell.
        }
    }
    if (error) { vex_pty_close(session); return (int)error; }
    *output = session;
    return 0;
}

static void CALLBACK forceClosePty(PTP_CALLBACK_INSTANCE, void* context, PTP_TIMER) {
    auto session = static_cast<VexPty*>(context);
    InterlockedIncrement(&forcedClosures);
    if (session->job) TerminateJobObject(session->job, ERROR_OPERATION_ABORTED);
    if (session->process) TerminateProcess(session->process, ERROR_OPERATION_ABORTED);
}

static void joinPipeWorker(HANDLE thread) {
    if (!thread) return;
    DWORD status;
    do {
        // Cancellation can precede the worker entering its next synchronous Read/WriteFile.
        CancelSynchronousIo(thread);
        status = WaitForSingleObject(thread, 10);
        if (status == WAIT_FAILED) RaiseFailFastException(nullptr, nullptr, 0);
    } while (status == WAIT_TIMEOUT);
}

static void destroyDetachedPty(VexPty* session) {
    destroyWslProcessProbe(session->wslProbe);
    joinPipeWorker(session->writer);
    if (session->input) { CloseHandle(session->input); session->input = nullptr; }
    // Prefer ConPTY's own disconnect. A watchdog bounds a wedged client/launcher.
    // Cancellation waits for the timer callback before any job/process handle is freed.
    PTP_TIMER timeout = CreateThreadpoolTimer(forceClosePty, session, nullptr);
    if (timeout) {
        LARGE_INTEGER due; due.QuadPart = -1000LL * 10000;
        FILETIME when = { due.LowPart, (DWORD)due.HighPart };
        SetThreadpoolTimer(timeout, &when, 0, 0);
    } else forceClosePty(nullptr, session, nullptr);
    // No reader means thread creation failed; close its pipe before closing ConPTY.
    if (!session->reader && session->output) { CloseHandle(session->output); session->output = nullptr; }
    // Only the selftest message adapter can arm this deterministic watchdog/ownership regression.
    if (session->testCloseDelayMs) Sleep(session->testCloseDelayMs);
    // Keep the output reader alive to drain ClosePseudoConsole, including partial startup.
    if (session->console) session->conpty.close(session->console);
    if (timeout) {
        SetThreadpoolTimer(timeout, nullptr, 0, 0);
        WaitForThreadpoolTimerCallbacks(timeout, TRUE);
        CloseThreadpoolTimer(timeout);
    }
    if (session->process && WaitForSingleObject(session->process, 0) != WAIT_OBJECT_0) TerminateProcess(session->process, 0);
    if (session->job) { CloseHandle(session->job); session->job = nullptr; }
    joinPipeWorker(session->reader);
    if (session->exitWatcher) WaitForSingleObject(session->exitWatcher, INFINITE);
    const HANDLE handles[] = { session->input, session->output, session->process, session->reader, session->writer, session->exitWatcher, session->outputConsumed, session->inputReady };
    for (HANDLE handle : handles) if (handle) CloseHandle(handle);
    DeleteCriticalSection(&session->inputLock);
    session->conpty.unload();
    free(session);
}

static DWORD WINAPI reapPtys(void* context) {
    auto reaper = static_cast<PtyReaper*>(context);
    for (;;) {
        AcquireSRWLockExclusive(&closingLock);
        auto session = closingHead;
        if (!session) {
            reaper->running = false;
            --closingRunning;
            WakeAllConditionVariable(&closingChanged);
            ReleaseSRWLockExclusive(&closingLock);
            return 0;
        }
        closingHead = session->next;
        if (!closingHead) closingTail = nullptr;
        ReleaseSRWLockExclusive(&closingLock);
        destroyDetachedPty(session);
        AcquireSRWLockExclusive(&closingLock);
        --closingCount;
        ReleaseSRWLockExclusive(&closingLock);
    }
}

extern "C" void vex_pty_close(VexPty* session) {
    if (!session) return;
    auto link = &sessions;
    while (*link && *link != session) link = &(*link)->next;
    if (*link != session) return;
    *link = session->next;
    // Remove tokens before scheduling teardown: neither late messages nor workers borrow Zig owners.
    InterlockedExchange(&session->stopping, 1);
    if (session->outputConsumed) SetEvent(session->outputConsumed);
    if (session->inputReady) SetEvent(session->inputReady);
    stopWslProcessProbe(session->wslProbe);
    session->next = nullptr;
    AcquireSRWLockExclusive(&closingLock);
    if (closingTail) closingTail->next = session;
    else closingHead = session;
    closingTail = session;
    ++closingCount;
    if (closingRunning < ARRAYSIZE(reapers)) {
        for (auto& reaper : reapers) {
            if (reaper.running) continue;
            // A retired reaper has released all resources; only its function return can remain.
            if (reaper.thread) { WaitForSingleObject(reaper.thread, INFINITE); CloseHandle(reaper.thread); }
            reaper.thread = CreateThread(nullptr, 0, reapPtys, &reaper, 0, nullptr);
            reaper.running = reaper.thread != nullptr;
            if (reaper.running) ++closingRunning;
            break;
        }
    }
    if (!closingRunning) {
        closingHead = closingTail = nullptr;
        --closingCount;
        ReleaseSRWLockExclusive(&closingLock);
        // Thread creation failure must still release the detached owner.
        destroyDetachedPty(session);
        return;
    }
    ReleaseSRWLockExclusive(&closingLock);
}

extern "C" void vex_pty_drain_closed() {
    AcquireSRWLockExclusive(&closingLock);
    while (closingRunning) SleepConditionVariableSRW(&closingChanged, &closingLock, INFINITE, 0);
    ReleaseSRWLockExclusive(&closingLock);
    for (auto& reaper : reapers) {
        if (reaper.thread) { WaitForSingleObject(reaper.thread, INFINITE); CloseHandle(reaper.thread); reaper.thread = nullptr; }
    }
}

extern "C" size_t vex_pty_pending_closures() {
    AcquireSRWLockShared(&closingLock);
    const size_t count = closingCount;
    ReleaseSRWLockShared(&closingLock);
    return count;
}
extern "C" uint32_t vex_pty_forced_closures() { return (uint32_t)InterlockedCompareExchange(&forcedClosures, 0, 0); }
extern "C" bool vex_pty_test_delay_close(uint32_t token) {
    auto session = findSession(token);
    if (!session) return false;
    session->testCloseDelayMs = 1300;
    return true;
}

static int queuePtyInput(VexPty* session, const uint8_t* bytes, size_t length, bool response) {
    if (!session || (!bytes && length)) return ERROR_INVALID_PARAMETER;
    if (InterlockedCompareExchange(&session->stopping, 0, 0)) return ERROR_BROKEN_PIPE;
    if (!length) return ERROR_SUCCESS;
    EnterCriticalSection(&session->inputLock);
    const size_t limit = sizeof(session->inputQueue) - (response ? 0 : VexPty::replyReserve);
    bool fits = session->inputLength <= limit && length <= limit - session->inputLength;
    if (fits) {
        size_t tail = (session->inputHead + session->inputLength) % sizeof(session->inputQueue);
        size_t first = length < sizeof(session->inputQueue) - tail ? length : sizeof(session->inputQueue) - tail;
        memcpy(session->inputQueue + tail, bytes, first);
        memcpy(session->inputQueue, bytes + first, length - first);
        session->inputLength += length;
    }
    LeaveCriticalSection(&session->inputLock);
    if (fits) SetEvent(session->inputReady);
    return fits ? ERROR_SUCCESS : ERROR_NOT_ENOUGH_QUOTA;
}

extern "C" int vex_pty_write(VexPty* session, const uint8_t* bytes, size_t length) {
    return queuePtyInput(session, bytes, length, false);
}
extern "C" int vex_pty_write_response(VexPty* session, const uint8_t* bytes, size_t length) {
    if (bytes && length >= 3 && bytes[0] == 0x1b && bytes[1] == '_' && bytes[2] == 'G') {
        uint8_t encoded[64 * 1024];
        const size_t count = encodeConptyGraphicsReply(bytes, length, encoded, sizeof(encoded));
        if (!count) return ERROR_INVALID_DATA;
        return queuePtyInput(session, encoded, count, true);
    }
    return queuePtyInput(session, bytes, length, true);
}

extern "C" bool vex_pty_resize(VexPty* session, uint16_t columns, uint16_t rows) {
    return session && columns >= 2 && rows > 0 && columns <= 32767 && rows <= 32767 &&
        SUCCEEDED(session->conpty.resize(session->console, COORD{(SHORT)columns, (SHORT)rows}));
}

extern "C" bool vex_pty_borrow_output(uint32_t token, const uint8_t** bytes, size_t* length) {
    if (!bytes || !length) return false;
    auto session = findSession(token);
    if (!session || InterlockedCompareExchange(&session->outputPending, 2, 1) != 1) return false;
    *bytes = session->outputBuffer;
    *length = session->outputLength;
    return true;
}
extern "C" void vex_pty_ack_output(uint32_t token) {
    auto session = findSession(token);
    if (session && InterlockedCompareExchange(&session->outputPending, 0, 2) == 2) SetEvent(session->outputConsumed);
}
extern "C" bool vex_pty_exists(uint32_t token) { return findSession(token) != nullptr; }
extern "C" uint32_t vex_pty_process_id(VexPty* session) { return session && session->process ? GetProcessId(session->process) : 0; }
extern "C" uint32_t vex_pty_clipboard_token(uint32_t process_id) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (!process) return 0;
    uint32_t token = 0;
    for (auto session = sessions; session; session = session->next) {
        BOOL member = FALSE;
        if (session->job && IsProcessInJob(process, session->job, &member) && member) {
            token = session->token;
            break;
        }
    }
    CloseHandle(process);
    return token;
}
extern "C" void vex_pty_note_process_input(VexPty* session) {
    if (session) noteWslProcessInput(session->wslProbe);
}

extern "C" bool vex_pty_wsl_snapshot(VexPty* session, uint8_t* bytes, size_t capacity, size_t* length) {
    return session && readWslProcessSnapshot(session->wslProbe, bytes, capacity, length);
}

static void extractLowerStem(const wchar_t* path, wchar_t (&stem)[64]) {
    const wchar_t* start = path;
    for (const wchar_t* p = path; *p; ++p) {
        if (*p == L'\\' || *p == L'/') start = p + 1;
    }
    size_t len = 0;
    while (start[len] && len < 63) {
        wchar_t c = start[len];
        stem[len] = (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + 32) : c;
        ++len;
    }
    stem[len] = 0;
    if (len > 4 && (wcscmp(stem + len - 4, L".exe") == 0 || wcscmp(stem + len - 4, L".cmd") == 0 ||
                    wcscmp(stem + len - 4, L".bat") == 0 || wcscmp(stem + len - 4, L".com") == 0)) {
        stem[len - 4] = 0;
    }
}

static bool matchStem(const wchar_t* stem, const wchar_t* const* list, size_t count) {
    for (size_t i = 0; i < count; ++i) {
        if (wcscmp(stem, list[i]) == 0) return true;
    }
    return false;
}

static bool isIgnoredHelper(const wchar_t* stem) {
    static const wchar_t* const list[] = {
        L"conhost", L"openconsole", L"wslhost", L"wslrelay", L"wslservice"
    };
    return matchStem(stem, list, sizeof(list) / sizeof(list[0]));
}

static bool isConfiguredShell(DWORD pid, const wchar_t* stem) {
    // Names only avoid unnecessary process/file queries; file identity makes the decision.
    bool candidate = false;
    for (auto profile = sessions; profile; profile = profile->next) {
        if (profile->declaredShell && profile->shellIdentityKnown && wcscmp(profile->rootStem, stem) == 0) {
            candidate = true;
            break;
        }
    }
    if (!candidate) return false;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return false;
    wchar_t path[32768];
    DWORD length = sizeof(path) / sizeof(path[0]);
    const bool queried = QueryFullProcessImageNameW(process, 0, path, &length) != FALSE;
    CloseHandle(process);
    if (!queried) return false;
    HANDLE image = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, 0, nullptr);
    if (image == INVALID_HANDLE_VALUE) return false;
    BY_HANDLE_FILE_INFORMATION info = {};
    const bool known = GetFileInformationByHandle(image, &info) != FALSE;
    CloseHandle(image);
    if (!known) return false;
    for (auto profile = sessions; profile; profile = profile->next) {
        if (profile->declaredShell && profile->shellIdentityKnown && profile->shellVolume == info.dwVolumeSerialNumber &&
            profile->shellFileHigh == info.nFileIndexHigh && profile->shellFileLow == info.nFileIndexLow) return true;
    }
    return false;
}

typedef NTSTATUS(NTAPI* NtQueryInformationProcessFn)(HANDLE, PROCESSINFOCLASS, PVOID, ULONG, PULONG);
static NtQueryInformationProcessFn ntQueryInfoProcess;

static size_t readProcessCommandLineUtf8(HANDLE process, uint8_t* out, size_t cap) {
    if (!process || !out || !cap) return 0;
    if (!ntQueryInfoProcess) {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll) ntQueryInfoProcess = reinterpret_cast<NtQueryInformationProcessFn>(reinterpret_cast<void*>(GetProcAddress(ntdll, "NtQueryInformationProcess")));
    }
    if (!ntQueryInfoProcess) return 0;
    PROCESS_BASIC_INFORMATION pbi = {};
    ULONG retLen = 0;
    if (ntQueryInfoProcess(process, ProcessBasicInformation, &pbi, sizeof(pbi), &retLen) < 0 || !pbi.PebBaseAddress) return 0;
    PEB peb = {};
    SIZE_T bytesRead = 0;
    if (!ReadProcessMemory(process, pbi.PebBaseAddress, &peb, sizeof(peb), &bytesRead) || !peb.ProcessParameters) return 0;
    RTL_USER_PROCESS_PARAMETERS params = {};
    if (!ReadProcessMemory(process, peb.ProcessParameters, &params, sizeof(params), &bytesRead)) return 0;
    if (!params.CommandLine.Buffer || params.CommandLine.Length < sizeof(wchar_t)) return 0;
    size_t wcharCount = params.CommandLine.Length / sizeof(wchar_t);
    if (wcharCount > 1024) wcharCount = 1024;
    wchar_t wideBuf[1024];
    if (!ReadProcessMemory(process, params.CommandLine.Buffer, wideBuf, wcharCount * sizeof(wchar_t), &bytesRead)) return 0;
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, wideBuf, static_cast<int>(wcharCount), reinterpret_cast<char*>(out), static_cast<int>(cap), nullptr, nullptr);
    return utf8Len > 0 ? static_cast<size_t>(utf8Len) : 0;
}

struct SnapshotNode {
    DWORD pid;
    DWORD ppid;
    wchar_t exe[64];
};
static SnapshotNode snapshotNodes[4096];
static size_t snapshotCount = 0;
static ULONGLONG snapshotTickMs = 0;

static void refreshProcessSnapshot() {
    ULONGLONG now = GetTickCount64();
    if (snapshotCount > 0 && now - snapshotTickMs < 50) return;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    size_t count = 0;
    PROCESSENTRY32W pe = {};
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe)) {
        do {
            if (count < 4096) {
                snapshotNodes[count].pid = pe.th32ProcessID;
                snapshotNodes[count].ppid = pe.th32ParentProcessID;
                wcsncpy_s(snapshotNodes[count].exe, pe.szExeFile, _TRUNCATE);
                ++count;
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    snapshotCount = count;
    snapshotTickMs = now;
}

extern "C" int vex_pty_foreground_process(VexPty* session, uint8_t* exe_bytes, size_t exe_capacity, size_t* exe_length, uint8_t* cmd_bytes, size_t cmd_capacity, size_t* cmd_length, bool* is_root, bool* is_shell) {
    if (!session || !exe_bytes || !exe_length || !cmd_bytes || !cmd_length || !is_root || !is_shell || !exe_capacity || !cmd_capacity) return ERROR_INVALID_PARAMETER;
    *exe_length = 0;
    *cmd_length = 0;
    *is_root = false;
    *is_shell = false;
    if (!session->process || InterlockedCompareExchange(&session->stopping, 0, 0)) return 0;
    DWORD rootPid = GetProcessId(session->process);
    if (!rootPid) return 0;

    struct QueueEntry {
        DWORD pid;
        uint8_t depth;
    };
    QueueEntry queue[64];
    size_t qHead = 0;
    size_t qTail = 0;
    queue[qTail++] = { rootPid, 0 };

    DWORD chosenPid = 0;
    bool chosenShell = false;
    const wchar_t* chosenExe = nullptr;
    wchar_t chosenStem[64] = {};
    // A user-declared shell is a shell even when its executable has an unknown name.
    // Arbitrary CLI programs still own their workers rather than masquerading as interactive shells.
    if (!session->declaredShell) {
        chosenPid = rootPid;
        chosenExe = session->rootStem;
        wcscpy_s(chosenStem, session->rootStem);
        qHead = qTail;
    } else {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting = {};
        // A live root alone in its non-breakaway job cannot have foreground children.
        const bool rootAlone = session->job && WaitForSingleObject(session->process, 0) == WAIT_TIMEOUT &&
            QueryInformationJobObject(session->job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) && accounting.ActiveProcesses == 1;
        if (rootAlone) qHead = qTail;
        else refreshProcessSnapshot();
    }

    // MSYS can leave live commands behind an already-exited fork parent. The
    // non-breakaway job remains authoritative even when the snapshot tree has a gap.
    struct JobPids { DWORD assigned, count; ULONG_PTR ids[4096]; } jobPids = {};
    if (qHead < qTail && session->job &&
        !QueryInformationJobObject(session->job, JobObjectBasicProcessIdList, &jobPids, sizeof(jobPids), nullptr)) jobPids.count = 0;
    auto inJob = [&](DWORD pid) {
        for (DWORD i = 0; i < jobPids.count; ++i) if (jobPids.ids[i] == pid) return true;
        return false;
    };

    DWORD fallbackShellPid = 0;
    const wchar_t* fallbackShellExe = nullptr;
    wchar_t fallbackShellStem[64] = {};
    while (qHead < qTail) {
        QueueEntry curr = queue[qHead++];
        if (curr.depth >= 12) continue;

        for (size_t i = 0; i < snapshotCount; ++i) {
            DWORD childPid = snapshotNodes[i].pid;
            const bool orphan = curr.pid == rootPid && inJob(childPid) && !inJob(snapshotNodes[i].ppid);
            if ((snapshotNodes[i].ppid != curr.pid && !orphan) || childPid == curr.pid || childPid == rootPid) continue;
            bool alreadyQueued = false;
            for (size_t q = 0; q < qTail; ++q) {
                if (queue[q].pid == childPid) { alreadyQueued = true; break; }
            }
            if (alreadyQueued) continue;

            wchar_t stem[64];
            extractLowerStem(snapshotNodes[i].exe, stem);
            if (isIgnoredHelper(stem)) continue;

            // Shell distributions can bootstrap another image with the same executable name
            // (for example Git's bin/bash.exe -> usr/bin/bash.exe). This is a handoff
            // from this pane's declared shell, not a global executable-name allowlist.
            bool isShell = (session->declaredShell && wcscmp(session->rootStem, stem) == 0) ||
                isConfiguredShell(childPid, stem);
            if (!isShell && !chosenPid) {
                chosenPid = childPid;
                chosenExe = snapshotNodes[i].exe;
                wcscpy_s(chosenStem, stem);
            } else if (isShell && !fallbackShellPid) {
                fallbackShellPid = childPid;
                fallbackShellExe = snapshotNodes[i].exe;
                wcscpy_s(fallbackShellStem, stem);
            }

            // Any application owns its workers; launcher arguments are decoded once by the Zig identity parser.
            const bool expandChildren = isShell;
            if (expandChildren && qTail < 64) {
                queue[qTail++] = { childPid, static_cast<uint8_t>(curr.depth + 1) };
            }
        }
    }

    // Preserve nested shell identity; Zig distinguishes interactive shells from scripts.
    if (!chosenPid && fallbackShellPid) {
        chosenPid = fallbackShellPid;
        chosenExe = fallbackShellExe;
        wcscpy_s(chosenStem, fallbackShellStem);
        chosenShell = true;
    }
    // A profile defines its root as a shell, regardless of executable name or startup flags.
    if (!chosenPid && session->declaredShell) {
        chosenPid = rootPid;
        chosenExe = session->rootStem;
        wcscpy_s(chosenStem, session->rootStem);
        chosenShell = true;
    }
    if (!chosenPid || !chosenExe || !chosenStem[0]) {
        session->fgPid = 0;
        session->fgCreateTime = 0;
        session->fgExeLen = 0;
        session->fgCmdLen = 0;
        return 0;
    }

    *is_root = chosenPid == rootPid;
    *is_shell = chosenShell;
    uint64_t createTime = 0;
    HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, chosenPid);
    if (proc) {
        FILETIME ftCreate = {}, ftExit = {}, ftKernel = {}, ftUser = {};
        if (GetProcessTimes(proc, &ftCreate, &ftExit, &ftKernel, &ftUser)) {
            createTime = (static_cast<uint64_t>(ftCreate.dwHighDateTime) << 32) | ftCreate.dwLowDateTime;
        }
    }

    // Cache failed/denied command-line reads too: unavailable metadata must not trigger repeated PEB reads.
    if (chosenPid == session->fgPid && createTime != 0 && createTime == session->fgCreateTime) {
        if (proc) CloseHandle(proc);
        size_t eLen = session->fgExeLen < exe_capacity ? session->fgExeLen : exe_capacity;
        size_t cLen = session->fgCmdLen < cmd_capacity ? session->fgCmdLen : cmd_capacity;
        if (eLen) memcpy(exe_bytes, session->fgExe, eLen);
        if (cLen) memcpy(cmd_bytes, session->fgCmd, cLen);
        *exe_length = eLen;
        *cmd_length = cLen;
        return 0;
    }

    int exeUtf8 = WideCharToMultiByte(CP_UTF8, 0, chosenExe, -1, reinterpret_cast<char*>(session->fgExe), sizeof(session->fgExe), nullptr, nullptr);
    session->fgExeLen = exeUtf8 > 1 ? static_cast<size_t>(exeUtf8 - 1) : 0;
    if (proc) CloseHandle(proc);
    proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, chosenPid);
    session->fgCmdLen = readProcessCommandLineUtf8(proc, session->fgCmd, sizeof(session->fgCmd));
    session->fgPid = chosenPid;
    session->fgCreateTime = createTime;
    if (proc) CloseHandle(proc);

    size_t eLen = session->fgExeLen < exe_capacity ? session->fgExeLen : exe_capacity;
    size_t cLen = session->fgCmdLen < cmd_capacity ? session->fgCmdLen : cmd_capacity;
    if (eLen) memcpy(exe_bytes, session->fgExe, eLen);
    if (cLen) memcpy(cmd_bytes, session->fgCmd, cLen);
    *exe_length = eLen;
    *cmd_length = cLen;
    return 0;
}


