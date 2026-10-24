#include "update_handoff.h"
#include <stdint.h>
#include <wchar.h>

namespace vex {
void UpdateHandoff::begin() {
    AcquireSRWLockExclusive(&lock);
    cancelRequested = false; committed = false;
    ReleaseSRWLockExclusive(&lock);
}
DWORD UpdateHandoff::prepare(const wchar_t* executable, const wchar_t* stage) {
    HANDLE parent = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), &parent,
        SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, TRUE, 0)) return GetLastError();
    SECURITY_ATTRIBUTES security = {sizeof(security), nullptr, TRUE};
    HANDLE ready = CreateEventW(&security, TRUE, FALSE, nullptr);
    DWORD error = ready ? ERROR_SUCCESS : GetLastError();
    HANDLE approve = error ? nullptr : CreateEventW(&security, TRUE, FALSE, nullptr);
    if (!error && !approve) error = GetLastError();
    HANDLE stop = error ? nullptr : CreateEventW(&security, TRUE, FALSE, nullptr);
    if (!error && !stop) error = GetLastError();
    AcquireSRWLockExclusive(&lock);
    readiness = ready; approval = approve; cancellation = stop;
    if (cancelRequested && stop) SetEvent(stop);
    ReleaseSRWLockExclusive(&lock);
    wchar_t source[MAX_PATH], command[1024];
    if (!error && (wcslen(executable) >= MAX_PATH || swprintf(helperPath, MAX_PATH, L"%ls\\vex-update.exe", stage) < 0)) error = ERROR_FILENAME_EXCED_RANGE;
    if (!error) {
        wcscpy(source, executable);
        wchar_t* slash = wcsrchr(source, L'\\');
        if (!slash || size_t(slash - source) + 16 >= MAX_PATH) error = ERROR_FILENAME_EXCED_RANGE;
        else {
            wcscpy(slash + 1, L"vex-update.exe");
            if (!CopyFileW(source, helperPath, FALSE)) error = GetLastError();
        }
    }
    if (!error) {
        alignas(void*) uint8_t bytes[256]; SIZE_T length = sizeof(bytes);
        auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(bytes);
        const bool initialized = InitializeProcThreadAttributeList(attributes, 1, 0, &length) != FALSE;
        HANDLE inherited[] = {parent, ready, approve, stop};
        if (!initialized || !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            inherited, sizeof(inherited), nullptr, nullptr)) error = GetLastError();
        else {
            swprintf(command, 1024, L"\"%ls\" --apply %llu %llu %llu %llu \"%ls\"", helperPath,
                (unsigned long long)reinterpret_cast<uintptr_t>(parent), (unsigned long long)reinterpret_cast<uintptr_t>(ready),
                (unsigned long long)reinterpret_cast<uintptr_t>(approve), (unsigned long long)reinterpret_cast<uintptr_t>(stop), stage);
            STARTUPINFOEXW startup = {}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = attributes;
            PROCESS_INFORMATION child = {};
            if (!CreateProcessW(helperPath, command, nullptr, nullptr, TRUE, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                nullptr, stage, &startup.StartupInfo, &child)) error = GetLastError();
            else {
                CloseHandle(child.hThread);
                AcquireSRWLockExclusive(&lock); process = child.hProcess; ReleaseSRWLockExclusive(&lock);
            }
        }
        if (initialized) DeleteProcThreadAttributeList(attributes);
    }
    CloseHandle(parent);
    if (error) return error;
    const HANDLE waits[] = {stop, process, ready};
    const DWORD wait = WaitForMultipleObjects(3, waits, FALSE, 90000);
    if (wait == WAIT_OBJECT_0 + 2) return ERROR_SUCCESS;
    if (wait == WAIT_OBJECT_0) return ERROR_CANCELLED;
    if (wait == WAIT_OBJECT_0 + 1) {
        DWORD exitCode = ERROR_PROCESS_ABORTED;
        if (!GetExitCodeProcess(process, &exitCode)) return GetLastError();
        return exitCode ? exitCode : ERROR_PROCESS_ABORTED;
    }
    return wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
}
DWORD UpdateHandoff::waitForDecision() {
    // Approval wins if the helper exits before this worker is scheduled again.
    const HANDLE waits[] = {approval, cancellation, process};
    const DWORD wait = WaitForMultipleObjects(3, waits, FALSE, INFINITE);
    if (wait == WAIT_OBJECT_0) return ERROR_SUCCESS;
    if (wait == WAIT_OBJECT_0 + 1) return ERROR_CANCELLED;
    if (wait == WAIT_OBJECT_0 + 2) {
        DWORD error = ERROR_PROCESS_ABORTED;
        if (!GetExitCodeProcess(process, &error)) return GetLastError();
        return error ? error : ERROR_PROCESS_ABORTED;
    }
    return GetLastError();
}
bool UpdateHandoff::prepared() {
    AcquireSRWLockShared(&lock);
    const bool result = !cancelRequested && process && readiness && WaitForSingleObject(process, 0) == WAIT_TIMEOUT &&
        WaitForSingleObject(readiness, 0) == WAIT_OBJECT_0;
    ReleaseSRWLockShared(&lock);
    return result;
}
bool UpdateHandoff::commit() {
    AcquireSRWLockExclusive(&lock);
    const bool result = !cancelRequested && !committed && process && readiness && approval &&
        WaitForSingleObject(process, 0) == WAIT_TIMEOUT && WaitForSingleObject(readiness, 0) == WAIT_OBJECT_0 && SetEvent(approval);
    if (result) committed = true;
    ReleaseSRWLockExclusive(&lock);
    return result;
}
void UpdateHandoff::cancel() {
    AcquireSRWLockExclusive(&lock);
    if (!committed) { cancelRequested = true; if (cancellation) SetEvent(cancellation); }
    ReleaseSRWLockExclusive(&lock);
}
void UpdateHandoff::finish() {
    AcquireSRWLockExclusive(&lock);
    HANDLE child = process, ready = readiness, approve = approval, stop = cancellation;
    const bool approved = committed;
    process = nullptr; readiness = nullptr; approval = nullptr; cancellation = nullptr;
    ReleaseSRWLockExclusive(&lock);
    if (!approved && child) {
        if (stop) SetEvent(stop);
        if (WaitForSingleObject(child, 5000) == WAIT_TIMEOUT) { TerminateProcess(child, ERROR_CANCELLED); WaitForSingleObject(child, INFINITE); }
    }
    const HANDLE handles[] = {child, ready, approve, stop};
    for (HANDLE handle : handles) if (handle) CloseHandle(handle);
    if (!approved && helperPath[0]) DeleteFileW(helperPath);
    helperPath[0] = 0;
}
}
