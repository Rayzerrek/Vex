#pragma once
#include <windows.h>

namespace vex {
// The worker owns helper resources; the window thread can approve/cancel without waiting for IO.
class UpdateHandoff final {
public:
    UpdateHandoff() = default;
    UpdateHandoff(const UpdateHandoff&) = delete;
    UpdateHandoff& operator=(const UpdateHandoff&) = delete;
    void begin();
    DWORD prepare(const wchar_t* executable, const wchar_t* stage);
    DWORD waitForDecision();
    bool prepared();
    bool commit();
    void cancel();
    // Worker only. Never terminates a committed installer; only an unapproved helper can be stopped.
    void finish();
private:
    SRWLOCK lock = SRWLOCK_INIT;
    HANDLE process = nullptr, readiness = nullptr, approval = nullptr, cancellation = nullptr;
    bool cancelRequested = false, committed = false;
    wchar_t helperPath[MAX_PATH] = {};
};
}
