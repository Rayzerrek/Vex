#include <winsock2.h>
#include "update_download.h"
#include "update_package.h"
#include <stdio.h>
#include <wchar.h>

namespace {
int failed(const char* condition, int line) { fprintf(stderr, "Update transport regression at %d: %s\n", line, condition); return line; }
#define CHECK_TRANSFER(value) do { if (!(value)) return failed(#value, __LINE__); } while (false)
enum class Response { valid, missing, unavailable, oversizedHeader, oversizedBody, truncated, chunked, writeDenied, stalledHeaders, stalledBody };
struct LoopbackServer {
    Response response;
    SOCKET listener = INVALID_SOCKET;
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr), connected = CreateEventW(nullptr, TRUE, FALSE, nullptr), headers = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread = nullptr;
    INTERNET_PORT port = 0;
    ~LoopbackServer() {
        if (stop) SetEvent(stop);
        if (listener != INVALID_SOCKET) closesocket(listener);
        if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
        if (stop) CloseHandle(stop); if (connected) CloseHandle(connected); if (headers) CloseHandle(headers);
    }
    bool start() {
        if (!stop || !connected || !headers) return false;
        listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listener == INVALID_SOCKET) return false;
        sockaddr_in address = {}; address.sin_family = AF_INET; address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) || listen(listener, 1)) return false;
        int size = sizeof(address);
        if (getsockname(listener, reinterpret_cast<sockaddr*>(&address), &size)) return false;
        port = ntohs(address.sin_port);
        thread = CreateThread(nullptr, 0, run, this, 0, nullptr);
        return thread != nullptr;
    }
    static bool sendAll(SOCKET client, const char* bytes, int length) {
        while (length) { const int written = send(client, bytes, length, 0); if (written <= 0) return false; bytes += written; length -= written; }
        return true;
    }
    static DWORD WINAPI run(void* context) {
        auto& self = *static_cast<LoopbackServer*>(context);
        SOCKET client = accept(self.listener, nullptr, nullptr);
        if (client == INVALID_SOCKET) return 0;
        struct Close { SOCKET value; ~Close() { closesocket(value); } } close{client};
        const DWORD timeout = 1000;
        setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
        char request[4096];
        if (recv(client, request, sizeof(request), 0) <= 0) return 0;
        SetEvent(self.connected);
        if (self.response == Response::stalledHeaders) { WaitForSingleObject(self.stop, 10000); return 0; }
        const char* header = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 6\r\n\r\n";
        switch (self.response) {
            case Response::missing: header = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"; break;
            case Response::unavailable: header = "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"; break;
            case Response::oversizedHeader: header = "HTTP/1.1 200 OK\r\nContent-Length: 161\r\nConnection: close\r\n\r\n"; break;
            case Response::oversizedBody: header = "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"; break;
            case Response::truncated: header = "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n"; break;
            case Response::chunked: header = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"; break;
            case Response::writeDenied: header = "HTTP/1.1 200 OK\r\nContent-Length: 1048576\r\nConnection: close\r\n\r\n"; break;
            case Response::stalledBody: header = "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n"; break;
            default: break;
        }
        if (!sendAll(client, header, int(strlen(header)))) return 0;
        SetEvent(self.headers);
        if (self.response == Response::oversizedHeader || self.response == Response::stalledBody) { WaitForSingleObject(self.stop, 10000); return 0; }
        if (self.response == Response::oversizedBody) { char bytes[161] = {}; sendAll(client, bytes, sizeof(bytes)); }
        else if (self.response == Response::chunked) sendAll(client, "3\r\nabc\r\n3\r\ndef\r\n0\r\n\r\n", 21);
        else if (self.response == Response::writeDenied) { char bytes[65536] = {}; sendAll(client, bytes, sizeof(bytes)); WaitForSingleObject(self.stop, 1000); }
        else if (self.response != Response::missing && self.response != Response::unavailable) sendAll(client, "abcdef", 6);
        return 0;
    }
};
struct TransferThread {
    vex::UpdateDownload download;
    HANDLE file = INVALID_HANDLE_VALUE, thread = nullptr;
    DWORD error = ERROR_GEN_FAILURE, length = 0;
    char bytes[160] = {};
    explicit TransferThread(INTERNET_PORT port) : download(port) {}
    ~TransferThread() { download.cancel(); if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); } }
    bool start() { thread = CreateThread(nullptr, 0, run, this, 0, nullptr); return thread != nullptr; }
    static DWORD WINAPI run(void* context) {
        auto& self = *static_cast<TransferThread*>(context);
        if (self.file == INVALID_HANDLE_VALUE) self.error = self.download.readManifest(self.bytes, self.length);
        else {
            vex::UpdateManifest manifest; manifest.size = 1048576;
            self.error = self.download.downloadPackage(manifest, self.file, nullptr, nullptr);
        }
        return 0;
    }
};
int responseTest(Response response, DWORD expected) {
    LoopbackServer server{response}; CHECK_TRANSFER(server.start());
    TransferThread client(server.port); CHECK_TRANSFER(client.start());
    CHECK_TRANSFER(WaitForSingleObject(server.headers, 5000) == WAIT_OBJECT_0);
    // A known oversized response must fail before the server sends or times out its body.
    const DWORD wait = WaitForSingleObject(client.thread, response == Response::oversizedHeader ? 1000 : 5000);
    if (wait != WAIT_OBJECT_0) { client.download.cancel(); SetEvent(server.stop); }
    CHECK_TRANSFER(wait == WAIT_OBJECT_0);
    if (response == Response::truncated) CHECK_TRANSFER(client.error != ERROR_SUCCESS);
    else CHECK_TRANSFER(client.error == expected);
    if (!expected) CHECK_TRANSFER(client.length == 6 && !memcmp(client.bytes, "abcdef", 6));
    return 0;
}
int cancellationTest(Response response) {
    LoopbackServer server{response}; CHECK_TRANSFER(server.start());
    TransferThread client(server.port); CHECK_TRANSFER(client.start());
    CHECK_TRANSFER(WaitForSingleObject(response == Response::stalledHeaders ? server.connected : server.headers, 5000) == WAIT_OBJECT_0);
    Sleep(20);
    const ULONGLONG start = GetTickCount64();
    client.download.cancel();
    CHECK_TRANSFER(WaitForSingleObject(client.thread, 1000) == WAIT_OBJECT_0 && client.error != ERROR_SUCCESS);
    CHECK_TRANSFER(GetTickCount64() - start < 1000);
    return 0;
}
int writeFailureTest() {
    wchar_t stage[MAX_PATH]; CHECK_TRANSFER(!vex::createUpdateStage(stage));
    struct Cleanup { const wchar_t* path; ~Cleanup() { vex::removeUpdateStage(path); } } cleanup{stage};
    wchar_t path[MAX_PATH]; swprintf(path, MAX_PATH, L"%ls\\update.part", stage);
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    CHECK_TRANSFER(file != INVALID_HANDLE_VALUE);
    struct Close { HANDLE file; ~Close() { CloseHandle(file); } } close{file};
    LoopbackServer server{Response::writeDenied}; CHECK_TRANSFER(server.start());
    TransferThread client(server.port); client.file = file; CHECK_TRANSFER(client.start());
    CHECK_TRANSFER(WaitForSingleObject(client.thread, 5000) == WAIT_OBJECT_0);
    CHECK_TRANSFER(client.error == ERROR_ACCESS_DENIED);
    return 0;
}
}
int testUpdateDownloadEdges() {
    WSADATA data;
    CHECK_TRANSFER(!WSAStartup(MAKEWORD(2, 2), &data));
    struct Cleanup { ~Cleanup() { WSACleanup(); } } cleanup;
    const struct { Response response; DWORD expected; } cases[] = {
        {Response::valid, ERROR_SUCCESS}, {Response::chunked, ERROR_SUCCESS},
        {Response::missing, ERROR_FILE_NOT_FOUND}, {Response::unavailable, ERROR_WINHTTP_INVALID_SERVER_RESPONSE},
        {Response::truncated, ERROR_HANDLE_EOF}, {Response::oversizedBody, ERROR_FILE_TOO_LARGE},
        {Response::oversizedHeader, ERROR_FILE_TOO_LARGE},
    };
    for (const auto& test : cases) { const int result = responseTest(test.response, test.expected); if (result) return result; }
    const Response stalled[] = {Response::stalledHeaders, Response::stalledBody};
    for (const auto response : stalled) { const int result = cancellationTest(response); if (result) return result; }
    return writeFailureTest();
}
