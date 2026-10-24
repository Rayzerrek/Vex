#include "update_install_guard.h"
#include "update_handoff.h"
#include <shellapi.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>
#include "update_package.h"

namespace {
int fail(const char* condition, int line) { fprintf(stderr, "Update concurrency regression at %d: %s\n", line, condition); return line; }
#define REQUIRE_UPDATE(value) do { if (!(value)) return fail(#value, __LINE__); } while (false)
struct FixtureDirectories {
    wchar_t source[MAX_PATH] = {}, stage[MAX_PATH] = {};
    ~FixtureDirectories() {
        const wchar_t* roots[] = {source, stage};
        const wchar_t* names[] = {L"Vex.exe", L"OpenConsole.exe", L"fixture-mode.txt", L"fixture-decision.txt", L"fixture.pid"};
        for (const auto* root : roots) if (*root) {
            for (const auto* name : names) { wchar_t path[MAX_PATH]; swprintf(path, MAX_PATH, L"%ls\\%ls", root, name); DeleteFileW(path); }
            vex::removeUpdateStage(root);
        }
    }
    bool init() { return !vex::createUpdateStage(source) && !vex::createUpdateStage(stage); }
};
bool writeFixture(const wchar_t* directory, const wchar_t* name, const char* value) {
    wchar_t path[MAX_PATH]; swprintf(path, MAX_PATH, L"%ls\\%ls", directory, name);
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0; const DWORD length = DWORD(strlen(value));
    const bool result = WriteFile(file, value, length, &written, nullptr) && written == length;
    CloseHandle(file); return result;
}
bool readFixture(const wchar_t* directory, const wchar_t* name, char (&value)[40]) {
    wchar_t path[MAX_PATH]; swprintf(path, MAX_PATH, L"%ls\\%ls", directory, name);
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD length = 0;
    const bool result = ReadFile(file, value, 39, &length, nullptr) && length < 39;
    CloseHandle(file); value[length] = 0; return result;
}
struct GuardThread {
    const wchar_t* executable;
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread = nullptr;
    DWORD result = ERROR_GEN_FAILURE;
    ~GuardThread() { if (release) SetEvent(release); if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); } if (ready) CloseHandle(ready); if (release) CloseHandle(release); }
    static DWORD WINAPI run(void* pointer) {
        auto& self = *static_cast<GuardThread*>(pointer);
        vex::UpdateInstallGuard guard;
        self.result = guard.acquire(self.executable);
        SetEvent(self.ready);
        WaitForSingleObject(self.release, 10000);
        return 0;
    }
};
struct ChildProcess {
    PROCESS_INFORMATION value = {};
    ~ChildProcess() { if (value.hProcess) { TerminateProcess(value.hProcess, 0); WaitForSingleObject(value.hProcess, INFINITE); CloseHandle(value.hProcess); } if (value.hThread) CloseHandle(value.hThread); }
    bool start(const wchar_t* path) {
        wchar_t command[MAX_PATH + 32]; swprintf(command, MAX_PATH + 32, L"\"%ls\" --update-hold", path);
        STARTUPINFOW startup = {}; startup.cb = sizeof(startup);
        return CreateProcessW(path, command, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &value) != FALSE;
    }
};
int guardTests() {
    FixtureDirectories directories;
    REQUIRE_UPDATE(directories.init());
    wchar_t own[MAX_PATH], exe[MAX_PATH], alias[MAX_PATH], other[MAX_PATH];
    REQUIRE_UPDATE(GetModuleFileNameW(nullptr, own, MAX_PATH));
    swprintf(exe, MAX_PATH, L"%ls\\Vex.exe", directories.source);
    swprintf(alias, MAX_PATH, L"%ls\\OpenConsole.exe", directories.source);
    swprintf(other, MAX_PATH, L"%ls\\Vex.exe", directories.stage);
    REQUIRE_UPDATE(CopyFileW(own, exe, TRUE) && CopyFileW(own, alias, TRUE) && CopyFileW(own, other, TRUE));
    GuardThread owner{exe};
    REQUIRE_UPDATE(owner.ready && owner.release);
    owner.thread = CreateThread(nullptr, 0, GuardThread::run, &owner, 0, nullptr);
    REQUIRE_UPDATE(owner.thread && WaitForSingleObject(owner.ready, 5000) == WAIT_OBJECT_0 && !owner.result);
    vex::UpdateInstallGuard contender, separate;
    REQUIRE_UPDATE(contender.acquire(alias) == ERROR_BUSY);
    REQUIRE_UPDATE(!separate.acquire(other));
    SetEvent(owner.release);
    REQUIRE_UPDATE(WaitForSingleObject(owner.thread, 5000) == WAIT_OBJECT_0);
    REQUIRE_UPDATE(!contender.acquire(alias));
    contender.release(); separate.release();
    {
        ChildProcess child;
        REQUIRE_UPDATE(child.start(exe));
        REQUIRE_UPDATE(vex::updateInstallationInUse(exe, 0, false) == ERROR_BUSY);
        REQUIRE_UPDATE(!vex::updateInstallationInUse(exe, child.value.dwProcessId, false));
    }
    {
        ChildProcess child;
        REQUIRE_UPDATE(child.start(alias));
        REQUIRE_UPDATE(!vex::updateInstallationInUse(exe, 0, false));
        REQUIRE_UPDATE(vex::updateInstallationInUse(exe, 0, true) == ERROR_BUSY);
    }
    {
        ChildProcess child;
        REQUIRE_UPDATE(child.start(other));
        REQUIRE_UPDATE(!vex::updateInstallationInUse(exe, 0, true));
    }
    return 0;
}
struct HandoffThread {
    vex::UpdateHandoff handoff;
    const wchar_t* executable;
    const wchar_t* stage;
    HANDLE thread = nullptr;
    HANDLE decisionGate = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    DWORD result = ERROR_GEN_FAILURE;
    ~HandoffThread() { handoff.cancel(); if (decisionGate) SetEvent(decisionGate); if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); } handoff.finish(); if (decisionGate) CloseHandle(decisionGate); }
    static DWORD WINAPI run(void* pointer) {
        auto& self = *static_cast<HandoffThread*>(pointer);
        self.result = self.handoff.prepare(self.executable, self.stage);
        if (!self.result) {
            WaitForSingleObject(self.decisionGate, 5000);
            self.result = self.handoff.waitForDecision();
        }
        self.handoff.finish(); return 0;
    }
};
int handoffTests(const char* mode) {
    FixtureDirectories directories;
    REQUIRE_UPDATE(directories.init());
    wchar_t own[MAX_PATH], source[MAX_PATH], fakeHelper[MAX_PATH];
    REQUIRE_UPDATE(GetModuleFileNameW(nullptr, own, MAX_PATH));
    swprintf(source, MAX_PATH, L"%ls\\Vex.exe", directories.source);
    swprintf(fakeHelper, MAX_PATH, L"%ls\\vex-update.exe", directories.source);
    REQUIRE_UPDATE(CopyFileW(own, source, TRUE) && CopyFileW(own, fakeHelper, TRUE));
    REQUIRE_UPDATE(writeFixture(directories.stage, L"fixture-mode.txt", mode));
    HandoffThread job{{}, source, directories.stage};
    job.handoff.begin();
    job.thread = CreateThread(nullptr, 0, HandoffThread::run, &job, 0, nullptr);
    REQUIRE_UPDATE(job.thread && job.decisionGate);
    char pid[40] = {};
    const ULONGLONG deadline = GetTickCount64() + 5000;
    while (!readFixture(directories.stage, L"fixture.pid", pid) && GetTickCount64() < deadline) Sleep(5);
    REQUIRE_UPDATE(*pid);
    const DWORD childId = strtoul(pid, nullptr, 10);
    HANDLE child = OpenProcess(SYNCHRONIZE, FALSE, childId);
    struct Close { HANDLE value; ~Close() { if (value) CloseHandle(value); } } close{child};
    if (!strcmp(mode, "fail")) {
        REQUIRE_UPDATE(WaitForSingleObject(job.thread, 5000) == WAIT_OBJECT_0 && job.result == ERROR_CRC);
        REQUIRE_UPDATE(!job.handoff.prepared() && !job.handoff.commit());
    } else if (!strcmp(mode, "stall")) {
        REQUIRE_UPDATE(!job.handoff.prepared() && !job.handoff.commit());
        const ULONGLONG started = GetTickCount64();
        job.handoff.cancel();
        REQUIRE_UPDATE(WaitForSingleObject(job.thread, 1500) == WAIT_OBJECT_0 && job.result == ERROR_CANCELLED);
        REQUIRE_UPDATE(GetTickCount64() - started < 1500);
    } else {
        while (!job.handoff.prepared() && GetTickCount64() < deadline) Sleep(5);
        REQUIRE_UPDATE(job.handoff.prepared());
        if (!strcmp(mode, "cancel")) {
            job.handoff.cancel();
            SetEvent(job.decisionGate);
            REQUIRE_UPDATE(!job.handoff.commit());
            REQUIRE_UPDATE(WaitForSingleObject(job.thread, 5000) == WAIT_OBJECT_0 && job.result == ERROR_CANCELLED);
        } else if (!strcmp(mode, "crash")) {
            REQUIRE_UPDATE(child && WaitForSingleObject(child, 5000) == WAIT_OBJECT_0);
            REQUIRE_UPDATE(!job.handoff.prepared() && !job.handoff.commit());
            SetEvent(job.decisionGate);
            REQUIRE_UPDATE(WaitForSingleObject(job.thread, 5000) == WAIT_OBJECT_0 && job.result == ERROR_CRC);
        } else {
            REQUIRE_UPDATE(job.handoff.commit());
            job.handoff.cancel(); // Approval cannot be revoked by application teardown.
            if (child) REQUIRE_UPDATE(WaitForSingleObject(child, 5000) == WAIT_OBJECT_0);
            SetEvent(job.decisionGate); // Simultaneous approval/exit must preserve an already committed decision.
            REQUIRE_UPDATE(WaitForSingleObject(job.thread, 5000) == WAIT_OBJECT_0 && !job.result);
            char decision[40] = {};
            REQUIRE_UPDATE(readFixture(directories.stage, L"fixture-decision.txt", decision) && !strcmp(decision, "committed"));
        }
    }
    return 0;
}
}
int testUpdateConcurrency() {
    const int guards = guardTests();
    if (guards) return guards;
    const char* modes[] = {"fail", "stall", "cancel", "crash", "commit"};
    for (const auto* mode : modes) { const int error = handoffTests(mode); if (error) return error; }
    return 0;
}
// Local WiX integration fixture: the installed parent uses the same handoff as the shipping app.
int testUpdateInstallerParent() {
    int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 4) { if (argv) LocalFree(argv); return ERROR_INVALID_PARAMETER; }
    wchar_t executable[MAX_PATH], stage[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, executable, MAX_PATH) || wcslen(argv[2]) >= MAX_PATH) { LocalFree(argv); return ERROR_INVALID_PARAMETER; }
    wcscpy(stage, argv[2]);
    const bool cancel = !wcscmp(argv[3], L"cancel");
    LocalFree(argv);
    vex::UpdateHandoff handoff;
    handoff.begin();
    DWORD error = handoff.prepare(executable, stage);
    char result[40]; snprintf(result, sizeof(result), "%lu", error);
    writeFixture(stage, L"fixture-preflight.txt", result);
    if (!error && cancel) {
        wchar_t msi[MAX_PATH]; swprintf(msi, MAX_PATH, L"%ls\\update.msi", stage);
        HANDLE replacement = CreateFileW(msi, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        const bool protectedFile = replacement == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION;
        if (replacement != INVALID_HANDLE_VALUE) CloseHandle(replacement);
        handoff.cancel();
        if (handoff.waitForDecision() != ERROR_CANCELLED || !protectedFile) error = ERROR_INVALID_DATA;
    } else if (!error && !handoff.commit()) error = ERROR_PROCESS_ABORTED;
    handoff.finish();
    return int(error);
}
int testUpdateInstallerRestarted() {
    wchar_t directory[MAX_PATH];
    if (!GetCurrentDirectoryW(MAX_PATH, directory)) return 1;
    char version[40];
    if (!readFixture(directory, L"fixture-version.txt", version)) return 2;
    wchar_t marker[80]; swprintf(marker, 80, L"restarted-%hs.marker", version);
    return writeFixture(directory, marker, "ok") ? 0 : 3;
}

// A real child process exercises the production inherited-handle/acknowledgement protocol, not MSI installation.
int testUpdateHandoffChild() {
    int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 7) { if (argv) LocalFree(argv); return ERROR_INVALID_PARAMETER; }
    HANDLE handles[4];
    for (unsigned i = 0; i < 4; ++i) handles[i] = reinterpret_cast<HANDLE>(uintptr_t(wcstoull(argv[i + 2], nullptr, 10)));
    wchar_t stage[MAX_PATH]; wcscpy(stage, argv[6]); LocalFree(argv);
    char mode[40] = {}, pid[40]; snprintf(pid, sizeof(pid), "%lu", GetCurrentProcessId());
    if (!readFixture(stage, L"fixture-mode.txt", mode) || !writeFixture(stage, L"fixture.pid", pid)) return ERROR_INVALID_DATA;
    if (!strcmp(mode, "fail")) return ERROR_CRC;
    if (!strcmp(mode, "stall")) { WaitForSingleObject(handles[3], 10000); return ERROR_CANCELLED; }
    if (!SetEvent(handles[1])) return GetLastError();
    if (!strcmp(mode, "crash")) { Sleep(300); return ERROR_CRC; }
    const HANDLE decision[] = {handles[3], handles[2]};
    if (WaitForMultipleObjects(2, decision, FALSE, 10000) != WAIT_OBJECT_0 + 1) return ERROR_CANCELLED;
    return writeFixture(stage, L"fixture-decision.txt", "committed") ? 0 : ERROR_WRITE_FAULT;
}
