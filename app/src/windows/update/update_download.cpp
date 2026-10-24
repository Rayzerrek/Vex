#include "update_download.h"
#include <wchar.h>
#ifndef VEX_VERSION
#error VEX_VERSION must come from app/VERSION
#endif

namespace vex {
namespace {
struct InternetHandle {
    HINTERNET value = nullptr;
    ~InternetHandle() { if (value) WinHttpCloseHandle(value); }
};
}
bool UpdateDownload::cancelled() {
    AcquireSRWLockShared(&lock);
    const bool result = stopped;
    ReleaseSRWLockShared(&lock);
    return result;
}
void UpdateDownload::cancel() {
    AcquireSRWLockExclusive(&lock);
    stopped = true;
    if (activeRequest) { WinHttpCloseHandle(activeRequest); activeRequest = nullptr; }
    ReleaseSRWLockExclusive(&lock);
}
DWORD UpdateDownload::readManifest(char (&bytes)[160], DWORD& length) {
    return transfer(L"/Rayzerrek/Vex/releases/latest/download/vex-update.txt", bytes, length, INVALID_HANDLE_VALUE, sizeof(bytes), nullptr, nullptr);
}
DWORD UpdateDownload::downloadPackage(const UpdateManifest& manifest, HANDLE file, Progress progress, void* context) {
    wchar_t resource[180];
    swprintf(resource, 180, L"/Rayzerrek/Vex/releases/download/v%u.%u.%u/Vex-Native-%u.%u.%u-win-x64.msi",
        manifest.version.parts[0], manifest.version.parts[1], manifest.version.parts[2],
        manifest.version.parts[0], manifest.version.parts[1], manifest.version.parts[2]);
    DWORD unused = 0;
    return transfer(resource, nullptr, unused, file, manifest.size, progress, context);
}
// No insecure TLS flags, credentials, remote command strings, or HTTPS-to-HTTP redirects.
DWORD UpdateDownload::transfer(const wchar_t* resource, char* manifestBytes, DWORD& manifestLength, HANDLE file,
                              uint64_t limit, Progress progress, void* context) {
    manifestLength = 0;
    if (cancelled()) return ERROR_CANCELLED;
    const wchar_t* host = L"github.com";
    INTERNET_PORT port = INTERNET_DEFAULT_HTTPS_PORT;
    DWORD access = WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, flags = WINHTTP_FLAG_SECURE;
#ifdef VEX_UPDATE_TEST
    if (testPort) { host = L"127.0.0.1"; port = testPort; access = WINHTTP_ACCESS_TYPE_NO_PROXY; flags = 0; }
#endif
    InternetHandle session{WinHttpOpen(L"Vex/" VEX_VERSION, access, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0)};
    if (!session.value) return GetLastError();
    if (!WinHttpSetTimeouts(session.value, 5000, 5000, 5000, 10000)) return GetLastError();
    InternetHandle connection{WinHttpConnect(session.value, host, port, 0)};
    if (!connection.value) return GetLastError();
    HINTERNET request = WinHttpOpenRequest(connection.value, L"GET", resource, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!request) return GetLastError();
    AcquireSRWLockExclusive(&lock);
    if (stopped) { ReleaseSRWLockExclusive(&lock); WinHttpCloseHandle(request); return ERROR_CANCELLED; }
    activeRequest = request;
    ReleaseSRWLockExclusive(&lock);
    DWORD policy = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    DWORD disable = WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_AUTHENTICATION;
    DWORD error = ERROR_SUCCESS;
    const ULONGLONG deadline = GetTickCount64() + 10 * 60 * 1000;
    if (!WinHttpSetOption(request, WINHTTP_OPTION_REDIRECT_POLICY, &policy, sizeof(policy)) ||
        !WinHttpSetOption(request, WINHTTP_OPTION_DISABLE_FEATURE, &disable, sizeof(disable)) ||
        !WinHttpSendRequest(request, L"Cache-Control: no-cache\r\n", DWORD(-1), WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(request, nullptr)) error = GetLastError();
    DWORD status = 0, statusLength = sizeof(status);
    if (!error && !WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &statusLength, WINHTTP_NO_HEADER_INDEX)) error = GetLastError();
    if (!error && status != 200) error = status == 404 ? ERROR_FILE_NOT_FOUND : ERROR_WINHTTP_INVALID_SERVER_RESPONSE;
    uint64_t declared = 0;
    bool hasDeclaredLength = false;
    if (!error) {
        wchar_t header[32] = {}; DWORD bytes = sizeof(header);
        if (WinHttpQueryHeaders(request, WINHTTP_QUERY_CONTENT_LENGTH, WINHTTP_HEADER_NAME_BY_INDEX, header, &bytes, WINHTTP_NO_HEADER_INDEX)) {
            hasDeclaredLength = true;
            if (!header[0]) error = ERROR_WINHTTP_INVALID_SERVER_RESPONSE;
            for (const wchar_t* at = header; !error && *at; ++at) {
                if (*at < L'0' || *at > L'9' || declared > (UINT64_MAX - uint64_t(*at - L'0')) / 10) error = ERROR_WINHTTP_INVALID_SERVER_RESPONSE;
                else declared = declared * 10 + uint64_t(*at - L'0');
            }
            if (!error && declared > limit) error = ERROR_FILE_TOO_LARGE;
            else if (!error && !manifestBytes && declared != limit) error = ERROR_HANDLE_EOF;
        } else {
            const DWORD reason = GetLastError();
            if (reason != ERROR_WINHTTP_HEADER_NOT_FOUND) error = reason;
        }
    }
    uint64_t total = 0;
    unsigned lastPercent = 0;
    uint8_t bytes[65536];
    while (!error) {
        if (cancelled()) { error = ERROR_CANCELLED; break; }
        if (GetTickCount64() >= deadline) { error = ERROR_TIMEOUT; break; }
        DWORD length = 0;
        if (!WinHttpReadData(request, bytes, sizeof(bytes), &length)) { error = GetLastError(); break; }
        if (!length) break;
        if (length > limit - total) { error = ERROR_FILE_TOO_LARGE; break; }
        if (manifestBytes) memcpy(manifestBytes + total, bytes, length);
        else {
            DWORD written = 0;
            if (!WriteFile(file, bytes, length, &written, nullptr)) { error = GetLastError(); break; }
            if (written != length) { error = ERROR_WRITE_FAULT; break; }
        }
        total += length;
        if (progress) {
            const unsigned percent = unsigned(total * 100 / limit);
            if (percent != lastPercent) { lastPercent = percent; progress(context, percent); }
        }
    }
    if (manifestBytes) manifestLength = DWORD(total);
    if (!error && ((hasDeclaredLength && total != declared) || (!manifestBytes && total != limit))) error = ERROR_HANDLE_EOF;
    AcquireSRWLockExclusive(&lock);
    if (activeRequest == request) { activeRequest = nullptr; WinHttpCloseHandle(request); }
    ReleaseSRWLockExclusive(&lock);
    return cancelled() ? ERROR_CANCELLED : error;
}
}
