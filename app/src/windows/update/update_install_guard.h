#pragma once
#include <windows.h>

namespace vex {
// Canonical, trailing-separator directory identity shared by startup, helpers and process checks.
DWORD canonicalUpdateDirectory(const wchar_t* executable, wchar_t (&directory)[MAX_PATH]);
// Preflight may ignore the parent and its ConPTY hosts; final installation checks ignore neither.
DWORD updateInstallationInUse(const wchar_t* executable, DWORD allowedProcess, bool includeConpty);
// One update transaction per canonical installation path, including other desktop sessions.
class UpdateInstallGuard final {
public:
    UpdateInstallGuard() = default;
    UpdateInstallGuard(const UpdateInstallGuard&) = delete;
    UpdateInstallGuard& operator=(const UpdateInstallGuard&) = delete;
    ~UpdateInstallGuard();
    // ERROR_BUSY means another helper owns this installation; abandoned owners are recoverable.
    DWORD acquire(const wchar_t* executable);
    void release();
private:
    HANDLE mutex = nullptr;
};
}
