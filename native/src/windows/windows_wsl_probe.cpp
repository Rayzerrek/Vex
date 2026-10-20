#include "windows_wsl_probe.h"
#include <windows.h>
#include <stdlib.h>
#include <string.h>

struct WslProcessProbe {
    CRITICAL_SECTION lock;
    HANDLE thread, requested, stopping;
    wchar_t* command;
    uint8_t snapshot[128 * 1024];
    size_t length;
    ULONGLONG requestedAt, completedAt;
};

namespace {
// Keep WSL startup, /proc scans and timeouts off the window thread. Only this worker owns query handles.
bool query(WslProcessProbe* probe, uint8_t* bytes, size_t capacity, size_t& length) {
    SECURITY_ATTRIBUTES security = { sizeof(security), nullptr, TRUE };
    HANDLE read = nullptr, write = nullptr;
    if (!CreatePipe(&read, &write, &security, 0)) return false;
    SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0);
    HANDLE null = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        &security, OPEN_EXISTING, 0, nullptr);
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    wchar_t* command = _wcsdup(probe->command);
    SIZE_T attributeSize = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeSize);
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, attributeSize));
    bool attributesReady = attributes && InitializeProcThreadAttributeList(attributes, 1, 0, &attributeSize);
    HANDLE inherited[] = { write, null };
    PROCESS_INFORMATION process = {};
    bool started = false;
    if (null != INVALID_HANDLE_VALUE && job && command && attributesReady &&
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) &&
        UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr)) {
        STARTUPINFOEXW startup = {};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = null;
        startup.StartupInfo.hStdOutput = write;
        startup.StartupInfo.hStdError = null;
        startup.lpAttributeList = attributes;
        started = CreateProcessW(nullptr, command, nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &process) != FALSE;
    }
    if (attributesReady) DeleteProcThreadAttributeList(attributes);
    if (attributes) HeapFree(GetProcessHeap(), 0, attributes);
    free(command);
    CloseHandle(write);
    if (null != INVALID_HANDLE_VALUE) CloseHandle(null);
    bool success = false;
    length = 0;
    if (started) {
        if (AssignProcessToJobObject(job, process.hProcess) && ResumeThread(process.hThread) != (DWORD)-1) {
            const ULONGLONG deadline = GetTickCount64() + 1500;
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
                if (GetTickCount64() >= deadline || WaitForSingleObject(probe->stopping, 10) == WAIT_OBJECT_0) break;
            }
        }
        TerminateProcess(process.hProcess, 0);
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
    for (;;) {
        const DWORD wake = WaitForMultipleObjects(2, events, FALSE, 1000);
        if (wake != WAIT_OBJECT_0 + 1 && wake != WAIT_TIMEOUT) break;
        size_t length = 0;
        const bool valid = query(probe, bytes, sizeof(probe->snapshot), length);
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
    probe->requested = CreateEventW(nullptr, FALSE, TRUE, nullptr);
    probe->stopping = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (probe->command && probe->requested && probe->stopping) probe->thread = CreateThread(nullptr, 0, run, probe, 0, nullptr);
    if (!probe->thread) { destroyWslProcessProbe(probe); return nullptr; }
    return probe;
}

void destroyWslProcessProbe(WslProcessProbe* probe) {
    if (!probe) return;
    if (probe->stopping) SetEvent(probe->stopping);
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
    if (now - probe->requestedAt >= 250) { probe->requestedAt = now; SetEvent(probe->requested); }
    LeaveCriticalSection(&probe->lock);
    return valid;
}
