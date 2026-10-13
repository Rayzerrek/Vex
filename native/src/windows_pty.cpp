#include "windows_platform.h"
#include <windows.h>
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

static DWORD startPty(VexPty* session, uint16_t columns, uint16_t rows) {
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
        wchar_t command[] = L"cmd.exe /d";
        wchar_t shell[MAX_PATH];
        UINT length = GetSystemDirectoryW(shell, MAX_PATH);
        if (!length || length + 9 >= MAX_PATH) error = ERROR_INSUFFICIENT_BUFFER;
        else {
            wcscat(shell, L"\\cmd.exe");
            if (!CreateProcessW(shell, command, nullptr, nullptr, FALSE,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                nullptr, nullptr, &startup.StartupInfo, &process)) error = GetLastError();
            else {
                session->process = process.hProcess;
                if (!AssignProcessToJobObject(session->job, process.hProcess)) error = GetLastError();
                else if (ResumeThread(process.hThread) == (DWORD)-1) error = GetLastError();
                CloseHandle(process.hThread);
            }
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

extern "C" int vex_pty_open(uint32_t token, uint16_t columns, uint16_t rows, VexPty** output) {
    if (!output || token == 0 || columns < 2 || rows == 0 || columns > 32767 || rows > 32767 || findSession(token)) return ERROR_INVALID_PARAMETER;
    *output = nullptr;
    auto session = static_cast<VexPty*>(calloc(1, sizeof(VexPty)));
    if (!session) return ERROR_OUTOFMEMORY;
    session->token = token;
    session->window = vex_native_window();
    InitializeCriticalSection(&session->inputLock);
    session->next = sessions;
    sessions = session;
    DWORD error = startPty(session, columns, rows);
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
