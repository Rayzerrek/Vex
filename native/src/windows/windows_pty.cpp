#include "windows_platform.h"
#include <windows.h>
#include <tlhelp32.h>
#include <winternl.h>
#include <stdlib.h>
#include <string.h>

HWND vex_native_window();

struct VexPty {
    uint32_t token;
    HWND window;
    VexPty* next;
    HANDLE input, output, process, job, reader, writer, exitWatcher, outputConsumed, inputReady;
    HPCON console;
    CRITICAL_SECTION inputLock;
    uint8_t inputQueue[1024 * 1024];
    size_t inputHead, inputLength;
    uint8_t outputBuffer[65536];
    DWORD outputLength;
    volatile LONG stopping, outputPending;
    DWORD fgPid;
    uint64_t fgCreateTime;
    uint8_t fgExe[64];
    size_t fgExeLen;
    uint8_t fgCmd[512];
    size_t fgCmdLen;
};

// Only the window thread edits this registry; workers carry their own stable owner.
static VexPty* sessions;

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
    session->outputConsumed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!session->outputConsumed) return GetLastError();
    session->inputReady = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!session->inputReady) return GetLastError();
    HANDLE inputRead = nullptr, outputWrite = nullptr;
    if (!CreatePipe(&inputRead, &session->input, nullptr, 0)) return GetLastError();
    if (!CreatePipe(&session->output, &outputWrite, nullptr, 0)) {
        DWORD error = GetLastError(); CloseHandle(inputRead); return error;
    }
    HRESULT result = CreatePseudoConsole(COORD{(SHORT)columns, (SHORT)rows}, inputRead, outputWrite, 0, &session->console);
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
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeSize);
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, attributeSize));
    if (!attributes) return ERROR_OUTOFMEMORY;
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attributeSize)) {
        DWORD error = GetLastError(); HeapFree(GetProcessHeap(), 0, attributes); return error;
    }
    DWORD error = ERROR_SUCCESS;
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, session->console, sizeof(session->console), nullptr, nullptr)) error = GetLastError();
    else {
        STARTUPINFOEXW startup = {};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.lpAttributeList = attributes;
        PROCESS_INFORMATION process = {};
        if (!CreateProcessW(reinterpret_cast<const wchar_t*>(launch.program), reinterpret_cast<wchar_t*>(launch.command), nullptr, nullptr, FALSE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
            const_cast<uint16_t*>(launch.environment), reinterpret_cast<const wchar_t*>(launch.directory), &startup.StartupInfo, &process)) error = GetLastError();
        else {
            session->process = process.hProcess;
            if (!AssignProcessToJobObject(session->job, process.hProcess)) error = GetLastError();
            else if (ResumeThread(process.hThread) == (DWORD)-1) error = GetLastError();
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
    auto session = static_cast<VexPty*>(calloc(1, sizeof(VexPty)));
    if (!session) return ERROR_OUTOFMEMORY;
    session->token = token;
    session->window = vex_native_window();
    InitializeCriticalSection(&session->inputLock);
    session->next = sessions;
    sessions = session;
    DWORD error = startPty(session, columns, rows, launch);
    if (error) { vex_pty_close(session); return (int)error; }
    *output = session;
    return 0;
}

extern "C" void vex_pty_close(VexPty* session) {
    if (!session) return;
    auto link = &sessions;
    while (*link && *link != session) link = &(*link)->next;
    if (*link == session) *link = session->next;
    // Old queued messages now resolve to nothing; IDs can never identify a new owner.
    InterlockedExchange(&session->stopping, 1);
    if (session->outputConsumed) SetEvent(session->outputConsumed);
    if (session->inputReady) SetEvent(session->inputReady);
    if (session->process) TerminateProcess(session->process, 0);
    if (session->job) { CloseHandle(session->job); session->job = nullptr; }
    // No reader means thread creation failed; close its pipe before closing ConPTY.
    if (!session->reader && session->output) { CloseHandle(session->output); session->output = nullptr; }
    // Keep the output reader alive to drain ClosePseudoConsole, including partial startup.
    if (session->console) ClosePseudoConsole(session->console);
    if (session->writer) { CancelSynchronousIo(session->writer); WaitForSingleObject(session->writer, INFINITE); }
    if (session->reader) { CancelSynchronousIo(session->reader); WaitForSingleObject(session->reader, INFINITE); }
    if (session->exitWatcher) WaitForSingleObject(session->exitWatcher, INFINITE);
    const HANDLE handles[] = { session->input, session->output, session->process, session->reader, session->writer, session->exitWatcher, session->outputConsumed, session->inputReady };
    for (HANDLE handle : handles) if (handle) CloseHandle(handle);
    DeleteCriticalSection(&session->inputLock);
    free(session);
}

extern "C" bool vex_pty_write(VexPty* session, const uint8_t* bytes, size_t length) {
    if (!session || (!bytes && length) || InterlockedCompareExchange(&session->stopping, 0, 0)) return false;
    if (!length) return true;
    EnterCriticalSection(&session->inputLock);
    bool fits = length <= sizeof(session->inputQueue) - session->inputLength;
    if (fits) {
        size_t tail = (session->inputHead + session->inputLength) % sizeof(session->inputQueue);
        size_t first = length < sizeof(session->inputQueue) - tail ? length : sizeof(session->inputQueue) - tail;
        memcpy(session->inputQueue + tail, bytes, first);
        memcpy(session->inputQueue, bytes + first, length - first);
        session->inputLength += length;
    }
    LeaveCriticalSection(&session->inputLock);
    if (fits) SetEvent(session->inputReady);
    return fits;
}

extern "C" bool vex_pty_resize(VexPty* session, uint16_t columns, uint16_t rows) {
    return session && columns >= 2 && rows > 0 && columns <= 32767 && rows <= 32767 &&
        SUCCEEDED(ResizePseudoConsole(session->console, COORD{(SHORT)columns, (SHORT)rows}));
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
        L"conhost", L"openconsole", L"wslhost", L"wslrelay", L"wslservice",
        L"git-remote-https", L"git-credential-manager", L"ssh-agent", L"gpg-agent"
    };
    return matchStem(stem, list, sizeof(list) / sizeof(list[0]));
}

static bool isShellStem(const wchar_t* stem) {
    static const wchar_t* const list[] = {
        L"pwsh", L"powershell", L"cmd", L"bash", L"zsh", L"fish", L"nu",
        L"elvish", L"sh", L"dash", L"ash", L"ksh", L"tcsh", L"csh", L"wsl"
    };
    return matchStem(stem, list, sizeof(list) / sizeof(list[0]));
}

static bool isTerminalHostStem(const wchar_t* stem) {
    static const wchar_t* const list[] = {
        L"nvim", L"vim", L"vi", L"hx", L"helix", L"nano", L"micro", L"emacs", L"kak",
        L"lazygit", L"gitui", L"tig", L"btop", L"btm", L"htop", L"top", L"yazi", L"ranger", L"lf",
        L"claude", L"codex", L"opencode", L"aider", L"zig", L"cargo", L"rustc", L"go", L"dotnet",
        L"rg", L"fd", L"fzf"
    };
    return matchStem(stem, list, sizeof(list) / sizeof(list[0]));
}

static bool isRuntimeHostStem(const wchar_t* stem) {
    static const wchar_t* const list[] = {
        L"node", L"nodejs", L"bun", L"deno", L"python", L"python3", L"py"
    };
    return matchStem(stem, list, sizeof(list) / sizeof(list[0]));
}

static bool needsCommandLineRead(const wchar_t* stem) {
    static const wchar_t* const list[] = {
        L"node", L"nodejs", L"bun", L"deno", L"python", L"python3", L"py", L"ruby", L"perl",
        L"java", L"javaw", L"dotnet", L"wsl", L"npx", L"pnpm", L"yarn", L"npm",
        L"uv", L"uvx", L"pipx", L"cmd", L"pwsh", L"powershell", L"bash", L"sh"
    };
    return matchStem(stem, list, sizeof(list) / sizeof(list[0]));
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

static bool containsAsciiIgnoreCase(const uint8_t* hay, size_t hayLen, const char* needle) {
    size_t nLen = strlen(needle);
    if (!nLen || hayLen < nLen) return false;
    for (size_t i = 0; i + nLen <= hayLen; ++i) {
        bool match = true;
        for (size_t j = 0; j < nLen; ++j) {
            uint8_t c = hay[i + j];
            if (c >= 'A' && c <= 'Z') c = static_cast<uint8_t>(c + 32);
            if (c != static_cast<uint8_t>(needle[j])) { match = false; break; }
        }
        if (match) return true;
    }
    return false;
}

static bool isPackageRunnerCommandLine(DWORD pid) {
    HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!proc) return false;
    uint8_t cmd[512];
    size_t len = readProcessCommandLineUtf8(proc, cmd, sizeof(cmd));
    CloseHandle(proc);
    if (!len) return false;
    return containsAsciiIgnoreCase(cmd, len, "npm-cli.js") ||
           containsAsciiIgnoreCase(cmd, len, "npx-cli.js") ||
           containsAsciiIgnoreCase(cmd, len, "\\npm\\bin\\") ||
           containsAsciiIgnoreCase(cmd, len, "pnpm.cjs") ||
           containsAsciiIgnoreCase(cmd, len, "\\pnpm\\") ||
           containsAsciiIgnoreCase(cmd, len, "\\yarn\\") ||
           containsAsciiIgnoreCase(cmd, len, "corepack") ||
           containsAsciiIgnoreCase(cmd, len, "bun run ") ||
           containsAsciiIgnoreCase(cmd, len, "bunx ");
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

extern "C" int vex_pty_foreground_process(VexPty* session, uint8_t* exe_bytes, size_t exe_capacity, size_t* exe_length, uint8_t* cmd_bytes, size_t cmd_capacity, size_t* cmd_length) {
    if (!session || !exe_bytes || !exe_length || !cmd_bytes || !cmd_length || !exe_capacity || !cmd_capacity) return ERROR_INVALID_PARAMETER;
    *exe_length = 0;
    *cmd_length = 0;
    if (!session->process || InterlockedCompareExchange(&session->stopping, 0, 0)) return 0;
    DWORD rootPid = GetProcessId(session->process);
    if (!rootPid) return 0;

    refreshProcessSnapshot();

    struct QueueEntry {
        DWORD pid;
        uint8_t depth;
    };
    QueueEntry queue[64];
    size_t qHead = 0;
    size_t qTail = 0;
    queue[qTail++] = { rootPid, 0 };

    DWORD chosenPid = 0;
    const wchar_t* chosenExe = nullptr;
    wchar_t chosenStem[64] = {};

    DWORD fallbackShellPid = 0;
    const wchar_t* fallbackShellExe = nullptr;
    wchar_t fallbackShellStem[64] = {};

    while (qHead < qTail) {
        QueueEntry curr = queue[qHead++];
        if (curr.depth >= 12) continue;

        for (size_t i = 0; i < snapshotCount; ++i) {
            DWORD childPid = snapshotNodes[i].pid;
            if (snapshotNodes[i].ppid != curr.pid || childPid == curr.pid || childPid == rootPid) continue;
            bool alreadyQueued = false;
            for (size_t q = 0; q < qTail; ++q) {
                if (queue[q].pid == childPid) { alreadyQueued = true; break; }
            }
            if (alreadyQueued) continue;

            wchar_t stem[64];
            extractLowerStem(snapshotNodes[i].exe, stem);
            if (isIgnoredHelper(stem)) continue;

            bool isShell = isShellStem(stem) && wcscmp(stem, L"wsl") != 0;
            if (!isShell) {
                chosenPid = childPid;
                chosenExe = snapshotNodes[i].exe;
                wcscpy_s(chosenStem, stem);
            } else if (fallbackShellPid == 0) {
                fallbackShellPid = childPid;
                fallbackShellExe = snapshotNodes[i].exe;
                wcscpy_s(fallbackShellStem, stem);
            }

            bool expandChildren = true;
            if (isTerminalHostStem(stem)) {
                expandChildren = false;
            } else if (isRuntimeHostStem(stem)) {
                expandChildren = isPackageRunnerCommandLine(childPid);
            }
            if (expandChildren && qTail < 64) {
                queue[qTail++] = { childPid, static_cast<uint8_t>(curr.depth + 1) };
            }
        }
    }

    if (!chosenPid && fallbackShellPid) {
        chosenPid = fallbackShellPid;
        chosenExe = fallbackShellExe;
        wcscpy_s(chosenStem, fallbackShellStem);
    }

    if (!chosenPid || !chosenExe || !chosenStem[0]) {
        session->fgPid = 0;
        session->fgCreateTime = 0;
        session->fgExeLen = 0;
        session->fgCmdLen = 0;
        return 0;
    }

    uint64_t createTime = 0;
    HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, chosenPid);
    bool canReadVm = proc != nullptr;
    if (!proc) proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, chosenPid);
    if (proc) {
        FILETIME ftCreate = {}, ftExit = {}, ftKernel = {}, ftUser = {};
        if (GetProcessTimes(proc, &ftCreate, &ftExit, &ftKernel, &ftUser)) {
            createTime = (static_cast<uint64_t>(ftCreate.dwHighDateTime) << 32) | ftCreate.dwLowDateTime;
        }
    }

    // Fast path: if PID and creation timestamp match cached state (and PEB command line succeeded when needed), skip memory reads.
    if (chosenPid == session->fgPid && createTime != 0 && createTime == session->fgCreateTime &&
        (!needsCommandLineRead(chosenStem) || session->fgCmdLen > 0)) {
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
    session->fgCmdLen = (canReadVm && needsCommandLineRead(chosenStem))
        ? readProcessCommandLineUtf8(proc, session->fgCmd, sizeof(session->fgCmd))
        : 0;
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


