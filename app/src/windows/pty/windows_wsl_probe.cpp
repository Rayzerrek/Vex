#include "windows_wsl_probe.h"
#include "wsl_probe_schedule.h"
#include <windows.h>
#include <stdlib.h>
#include <string.h>

struct WslProcessProbe {
    CRITICAL_SECTION lock;
    HANDLE thread, requested, stopping;
    wchar_t* command;
    uint8_t snapshot[128 * 1024];
    size_t length;
    ULONGLONG createdAt, requestedAt, completedAt;
};

namespace {
// One advisory launch at a time, shared by all panes; contention drops a query, not user input.
SRWLOCK queryGate = SRWLOCK_INIT;
// Keep WSL startup, /proc scans and timeouts off the window thread. Only this worker owns query handles.
bool query(WslProcessProbe* probe, uint8_t* bytes, size_t capacity, size_t& length) {
    if (WaitForSingleObject(probe->stopping, 0) != WAIT_TIMEOUT) return false;
    SECURITY_ATTRIBUTES security = { sizeof(security), nullptr, TRUE };
    HANDLE read = nullptr, write = nullptr;
    if (!CreatePipe(&read, &write, &security, 0)) return false;
    if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0)) { CloseHandle(read); CloseHandle(write); return false; }
    HANDLE null = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        &security, OPEN_EXISTING, 0, nullptr);
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    wchar_t* command = _wcsdup(probe->command);
    SIZE_T attributeSize = 0;
    InitializeProcThreadAttributeList(nullptr, 2, 0, &attributeSize);
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, attributeSize));
    bool attributesReady = attributes && InitializeProcThreadAttributeList(attributes, 2, 0, &attributeSize);
    HANDLE inherited[] = { write, null };
    PROCESS_INFORMATION process = {};
    bool started = false;
    if (null != INVALID_HANDLE_VALUE && job && command && attributesReady &&
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) &&
        UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr) &&
        UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(job), nullptr, nullptr) &&
        WaitForSingleObject(probe->stopping, 0) == WAIT_TIMEOUT) {
        STARTUPINFOEXW startup = {};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = null;
        startup.StartupInfo.hStdOutput = write;
        startup.StartupInfo.hStdError = null;
        startup.lpAttributeList = attributes;
        started = CreateProcessW(nullptr, command, nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &process) != FALSE;
    }
    if (attributesReady) DeleteProcThreadAttributeList(attributes);
    if (attributes) HeapFree(GetProcessHeap(), 0, attributes);
    free(command);
    CloseHandle(write);
    if (null != INVALID_HANDLE_VALUE) CloseHandle(null);
    bool success = false;
    length = 0;
    if (started) {
        {
            const ULONGLONG began = GetTickCount64();
            const ULONGLONG deadline = began + wslProbeSchedule::queryTimeoutMs;
            bool failed = false;
            for (;;) {
                DWORD available = 0;
                if (PeekNamedPipe(read, nullptr, 0, nullptr, &available, nullptr) && available) {
                    if (length == capacity) { failed = true; break; }
                    DWORD count = 0;
                    size_t remaining = capacity - length;
                    const DWORD step = available < remaining ? available : static_cast<DWORD>(remaining);
                    if (!ReadFile(read, bytes + length, step, &count, nullptr) || !count) { failed = true; break; }
                    length += count;
                    continue;
                }
                if (WaitForSingleObject(process.hProcess, 0) == WAIT_OBJECT_0) {
                    DWORD code = 1;
                    success = !failed && GetExitCodeProcess(process.hProcess, &code) && code == 0 && length > 0;
                    break;
                }
                const ULONGLONG now = GetTickCount64();
                if (now >= deadline) break;
                if (WaitForSingleObject(probe->stopping, 10) == WAIT_OBJECT_0) {
                    // Avoid immediately killing a client still initializing its DLLs.
                    const ULONGLONG elapsed = GetTickCount64() - began;
                    if (elapsed < wslProbeSchedule::shutdownGraceMs)
                        WaitForSingleObject(process.hProcess, (DWORD)(wslProbeSchedule::shutdownGraceMs - elapsed));
                    break;
                }
            }
        }
        if (WaitForSingleObject(process.hProcess, 0) != WAIT_OBJECT_0) TerminateProcess(process.hProcess, 0);
        CloseHandle(job); job = nullptr;
        WaitForSingleObject(process.hProcess, INFINITE);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
    if (job) CloseHandle(job);
    CloseHandle(read);
    return success;
}

DWORD WINAPI run(void* context) {
    auto probe = static_cast<WslProcessProbe*>(context);
    uint8_t* bytes = static_cast<uint8_t*>(malloc(sizeof(probe->snapshot)));
    if (!bytes) return 0;
    HANDLE events[] = { probe->stopping, probe->requested };
    ULONGLONG nextQueryAt = 0;
    unsigned failures = 0;
    for (;;) {
        // No periodic subprocesses for inactive panes. Only visible metadata reads request work.
        if (WaitForMultipleObjects(2, events, FALSE, INFINITE) != WAIT_OBJECT_0 + 1) break;
        if (GetTickCount64() < nextQueryAt || !TryAcquireSRWLockExclusive(&queryGate)) continue;
        size_t length = 0;
        const bool valid = query(probe, bytes, sizeof(probe->snapshot), length);
        ReleaseSRWLockExclusive(&queryGate);
        failures = valid ? 0 : (failures < 4 ? failures + 1 : 4);
        nextQueryAt = GetTickCount64() + wslProbeSchedule::retryDelayMs(failures);
        EnterCriticalSection(&probe->lock);
        probe->length = valid ? length : 0;
        if (valid) memcpy(probe->snapshot, bytes, length);
        probe->completedAt = GetTickCount64();
        LeaveCriticalSection(&probe->lock);
    }
    free(bytes);
    return 0;
}
}

WslProcessProbe* createWslProcessProbe(const wchar_t* command) {
    auto probe = static_cast<WslProcessProbe*>(calloc(1, sizeof(WslProcessProbe)));
    if (!probe) return nullptr;
    InitializeCriticalSection(&probe->lock);
    probe->command = _wcsdup(command);
    probe->createdAt = GetTickCount64();
    probe->requested = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    probe->stopping = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (probe->command && probe->requested && probe->stopping) probe->thread = CreateThread(nullptr, 0, run, probe, 0, nullptr);
    if (!probe->thread) { destroyWslProcessProbe(probe); return nullptr; }
    return probe;
}

void stopWslProcessProbe(WslProcessProbe* probe) {
    if (probe && probe->stopping) SetEvent(probe->stopping);
}

void destroyWslProcessProbe(WslProcessProbe* probe) {
    if (!probe) return;
    stopWslProcessProbe(probe);
    if (probe->thread) { WaitForSingleObject(probe->thread, INFINITE); CloseHandle(probe->thread); }
    if (probe->requested) CloseHandle(probe->requested);
    if (probe->stopping) CloseHandle(probe->stopping);
    free(probe->command);
    DeleteCriticalSection(&probe->lock);
    free(probe);
}

bool readWslProcessSnapshot(WslProcessProbe* probe, uint8_t* bytes, size_t capacity, size_t* length) {
    if (!probe || !bytes || !length) return false;
    const ULONGLONG now = GetTickCount64();
    EnterCriticalSection(&probe->lock);
    const bool valid = probe->length > 0 && probe->length <= capacity && now - probe->completedAt <= 2000;
    *length = valid ? probe->length : 0;
    if (valid) memcpy(bytes, probe->snapshot, *length);
    if (wslProbeSchedule::requestDue(now, probe->createdAt, probe->requestedAt)) { probe->requestedAt = now; SetEvent(probe->requested); }
    LeaveCriticalSection(&probe->lock);
    return valid;
}
