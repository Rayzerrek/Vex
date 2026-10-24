#include "update_install_guard.h"
#include <stdint.h>
#include <wchar.h>
#include <tlhelp32.h>

namespace vex {
UpdateInstallGuard::~UpdateInstallGuard() { release(); }
void UpdateInstallGuard::release() {
    if (mutex) { ReleaseMutex(mutex); CloseHandle(mutex); mutex = nullptr; }
}
DWORD canonicalUpdateDirectory(const wchar_t* executable, wchar_t (&directory)[MAX_PATH]) {
    // Resolve junctions/short names so aliases cannot acquire independent locks.
    HANDLE file = CreateFileW(executable, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    const DWORD length = GetFinalPathNameByHandleW(file, directory, MAX_PATH, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    const DWORD error = length ? ERROR_SUCCESS : GetLastError();
    CloseHandle(file);
    if (error) return error;
    if (length >= MAX_PATH) return ERROR_FILENAME_EXCED_RANGE;
    wchar_t* slash = wcsrchr(directory, L'\\');
    if (!slash) return ERROR_INVALID_NAME;
    slash[1] = 0;
    return ERROR_SUCCESS;
}
DWORD updateInstallationInUse(const wchar_t* executable, DWORD allowedProcess, bool includeConpty) {
    wchar_t directory[MAX_PATH];
    DWORD error = canonicalUpdateDirectory(executable, directory);
    if (error) return error;
    HANDLE processes = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (processes == INVALID_HANDLE_VALUE) return GetLastError();
    PROCESSENTRY32W entry = {}; entry.dwSize = sizeof(entry);
    if (!Process32FirstW(processes, &entry)) error = GetLastError();
    else do {
        if (entry.th32ProcessID == allowedProcess) continue;
        const bool vex = !_wcsicmp(entry.szExeFile, L"Vex.exe") || !_wcsicmp(entry.szExeFile, L"vex-native.exe");
        if (!vex && !(includeConpty && !_wcsicmp(entry.szExeFile, L"OpenConsole.exe"))) continue;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) {
            const DWORD reason = GetLastError();
            if (reason == ERROR_INVALID_PARAMETER || reason == ERROR_NOT_FOUND) continue;
            error = reason; break;
        }
        DWORD exitCode = STILL_ACTIVE;
        wchar_t image[MAX_PATH], other[MAX_PATH]; DWORD length = MAX_PATH;
        if (!GetExitCodeProcess(process, &exitCode)) error = GetLastError();
        else if (exitCode == STILL_ACTIVE) {
            if (!QueryFullProcessImageNameW(process, 0, image, &length)) error = GetLastError();
            else if (!(error = canonicalUpdateDirectory(image, other)) &&
                CompareStringOrdinal(directory, -1, other, -1, TRUE) == CSTR_EQUAL) error = ERROR_BUSY;
        }
        CloseHandle(process);
        if (error) break;
    } while (Process32NextW(processes, &entry));
    CloseHandle(processes);
    return error;
}
DWORD UpdateInstallGuard::acquire(const wchar_t* executable) {
    if (mutex) return ERROR_ALREADY_EXISTS;
    wchar_t canonical[MAX_PATH], folded[MAX_PATH];
    const DWORD error = canonicalUpdateDirectory(executable, canonical);
    if (error) return error;
    const int count = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, canonical, -1, folded, MAX_PATH, nullptr, nullptr, 0);
    if (!count) return GetLastError();
    uint64_t hash = 14695981039346656037ULL;
    for (int i = 0; i < count - 1; ++i) { hash ^= uint16_t(folded[i]); hash *= 1099511628211ULL; }
    wchar_t name[80]; swprintf(name, 80, L"Global\\Vex.Native.Update.%016llx", (unsigned long long)hash);
    HANDLE candidate = CreateMutexExW(nullptr, name, 0, SYNCHRONIZE | MUTEX_MODIFY_STATE);
    if (!candidate) return GetLastError();
    const DWORD wait = WaitForSingleObject(candidate, 0);
    if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED) { mutex = candidate; return ERROR_SUCCESS; }
    const DWORD result = wait == WAIT_TIMEOUT ? ERROR_BUSY : GetLastError();
    CloseHandle(candidate);
    return result;
}
}
