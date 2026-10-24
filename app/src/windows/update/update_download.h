#pragma once
#include "update_manifest.h"
#include <windows.h>
#include <winhttp.h>

namespace vex {
// One worker owns transfers; the window thread may cancel the active request during shutdown.
class UpdateDownload final {
public:
    UpdateDownload() = default;
#ifdef VEX_UPDATE_TEST
    // Fixture-only loopback HTTP transport; absent from both shipping executables.
    explicit UpdateDownload(INTERNET_PORT loopbackPort) : testPort(loopbackPort) {}
#endif
    UpdateDownload(const UpdateDownload&) = delete;
    UpdateDownload& operator=(const UpdateDownload&) = delete;
    using Progress = void (*)(void*, unsigned);
    void cancel();
    DWORD readManifest(char (&bytes)[160], DWORD& length);
    DWORD downloadPackage(const UpdateManifest& manifest, HANDLE file, Progress progress, void* context);
private:
#ifdef VEX_UPDATE_TEST
    INTERNET_PORT testPort = 0;
#endif
    SRWLOCK lock = SRWLOCK_INIT;
    HINTERNET activeRequest = nullptr;
    bool stopped = false;
    bool cancelled();
    DWORD transfer(const wchar_t* resource, char* bytes, DWORD& length, HANDLE file, uint64_t limit, Progress progress, void* context);
};
}
