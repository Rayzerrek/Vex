#include "update_package.h"
#include "update_download.h"
#include <shellapi.h>
#include <sddl.h>
#include <stdio.h>
#include <wchar.h>

namespace {
int failure(const char* message, int line) {
    fprintf(stderr, "Update regression failed at line %d: %s\n", line, message);
    return line;
}
#define CHECK_UPDATE(condition) do { if (!(condition)) return failure(#condition, __LINE__); } while (false)
int manifestTests() {
    vex::UpdateVersion current, next;
    CHECK_UPDATE(vex::parseUpdateVersion("2.9.9", 5, current));
    CHECK_UPDATE(vex::parseUpdateVersion("2.10.0", 6, next));
    CHECK_UPDATE(next.newerThan(current));
    CHECK_UPDATE(!current.newerThan(next));
    CHECK_UPDATE(!current.newerThan(current));
    const char* invalid[] = {"", "v2.0.1", "02.0.1", "2.0.1-rc1", "2.0", "2.0.1.0", "256.0.0", "0.256.0", "0.0.65536", "2.0.-1", "999999999999999999999999.1.0", "2.0.1\n"};
    for (const auto* value : invalid) CHECK_UPDATE(!vex::parseUpdateVersion(value, strlen(value), next));
    CHECK_UPDATE(vex::parseUpdateVersion("255.255.65535", 13, next));
    CHECK_UPDATE(next.parts[0] == 255 && next.parts[1] == 255 && next.parts[2] == 65535);
    constexpr char valid[] = "VEX-UPDATE-1\n2.10.0\n1048576\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n";
    vex::UpdateManifest manifest;
    CHECK_UPDATE(vex::parseUpdateManifest(valid, sizeof(valid) - 1, manifest));
    CHECK_UPDATE(manifest.size == 1048576 && manifest.sha256[0] == 0x01 && manifest.sha256[31] == 0xef);
    for (size_t length = 0; length < sizeof(valid) - 1; ++length) CHECK_UPDATE(!vex::parseUpdateManifest(valid, length, manifest));
    char mutated[sizeof(valid) + 1];
    memcpy(mutated, valid, sizeof(valid)); mutated[sizeof(valid) - 1] = '\n';
    CHECK_UPDATE(!vex::parseUpdateManifest(mutated, sizeof(valid), manifest));
    const char* bad[] = {
        "VEX-UPDATE-2\n2.0.2\n1048576\n", "VEX-UPDATE-1\r\n2.0.2\r\n1048576\r\n",
        "VEX-UPDATE-1\n2.0.2\n0001048576\n", "VEX-UPDATE-1\n2.0.2\n1048575\n",
        "VEX-UPDATE-1\n2.0.2\n536870913\n", "VEX-UPDATE-1\n2.0.2\n-1\n",
        "VEX-UPDATE-1\n2.0.2\n18446744073709551616\n", "VEX-UPDATE-1\n../../bad\n1048576\n"
    };
    for (const auto* prefix : bad) {
        char value[200]; snprintf(value, sizeof(value), "%s0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n", prefix);
        CHECK_UPDATE(!vex::parseUpdateManifest(value, strlen(value), manifest));
    }
    memcpy(mutated, valid, sizeof(valid)); mutated[sizeof(valid) - 4] = 'G';
    CHECK_UPDATE(!vex::parseUpdateManifest(mutated, sizeof(valid) - 1, manifest));
    memcpy(mutated, valid, sizeof(valid)); mutated[sizeof(valid) - 4] = 0;
    CHECK_UPDATE(!vex::parseUpdateManifest(mutated, sizeof(valid) - 1, manifest));
    return 0;
}
int stageTests() {
    wchar_t stage[MAX_PATH];
    CHECK_UPDATE(!vex::createUpdateStage(stage));
    const wchar_t* name = wcsrchr(stage, L'\\');
    CHECK_UPDATE(name && vex::updateStageNameValid(name + 1));
    CHECK_UPDATE(!vex::updateStageNameValid(L"Vex-Update-backup"));
    CHECK_UPDATE(!vex::updateStageNameValid(L"Vex-Update-0123456789abcdef0123456789abcdef-extra"));
    CHECK_UPDATE(!vex::updateStageNameValid(L"Vex-Update-0123456789abcdef0123456789abcdeg"));
    struct Cleanup { const wchar_t* path; ~Cleanup() { vex::removeUpdateStage(path); } } cleanup{stage};
    DWORD needed = 0;
    GetFileSecurityW(stage, DACL_SECURITY_INFORMATION, nullptr, 0, &needed);
    CHECK_UPDATE(needed > 0 && needed <= 4096);
    alignas(void*) uint8_t descriptor[4096];
    CHECK_UPDATE(GetFileSecurityW(stage, DACL_SECURITY_INFORMATION, descriptor, sizeof(descriptor), &needed));
    SECURITY_DESCRIPTOR_CONTROL control = 0; DWORD revision = 0;
    CHECK_UPDATE(GetSecurityDescriptorControl(descriptor, &control, &revision));
    CHECK_UPDATE(control & SE_DACL_PROTECTED);
    wchar_t file[MAX_PATH]; swprintf(file, MAX_PATH, L"%ls\\update.part", stage);
    HANDLE output = CreateFileW(file, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    CHECK_UPDATE(output != INVALID_HANDLE_VALUE);
    uint8_t zeros[65536] = {}; DWORD written = 0;
    bool success = true;
    for (unsigned i = 0; i < 16; ++i) if (!WriteFile(output, zeros, sizeof(zeros), &written, nullptr) || written != sizeof(zeros)) success = false;
    CloseHandle(output);
    CHECK_UPDATE(success);
    // SHA-256 of one MiB of zero bytes. Matching a hash alone must not allow a non-MSI package.
    constexpr char feed[] = "VEX-UPDATE-1\n2.0.2\n1048576\n30e14955ebf1352266dc2ff8067e68104607e750abb9d3b36582b8af909fcb58\n";
    vex::UpdateManifest manifest;
    CHECK_UPDATE(vex::parseUpdateManifest(feed, sizeof(feed) - 1, manifest));
    const DWORD invalidMsi = vex::verifyUpdatePackage(file, manifest);
    CHECK_UPDATE(invalidMsi != ERROR_SUCCESS && invalidMsi != ERROR_CRC);
    manifest.sha256[0] ^= 1;
    CHECK_UPDATE(vex::verifyUpdatePackage(file, manifest) == ERROR_CRC);
    manifest.size++;
    CHECK_UPDATE(vex::verifyUpdatePackage(file, manifest) == ERROR_INVALID_DATA);
    return 0;
}
}
int testUpdateSystem() {
    wchar_t executable[MAX_PATH];
    CHECK_UPDATE(GetModuleFileNameW(nullptr, executable, MAX_PATH));
    vex::UpdateInstallation installation;
    CHECK_UPDATE(!vex::findUpdateInstallation(executable, installation));
    vex::UpdateDownload cancelled;
    cancelled.cancel();
    char bytes[160]; DWORD length = 0;
    CHECK_UPDATE(cancelled.readManifest(bytes, length) == ERROR_CANCELLED);
    const int result = manifestTests();
    return result ? result : stageTests();
}
// Optional local integration check against a real release MSI and its feed; never installs a product.
int testUpdatePackage() {
    int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 4) { if (argv) LocalFree(argv); return 1; }
    HANDLE file = CreateFileW(argv[3], GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) { LocalFree(argv); return 2; }
    char bytes[161]; DWORD length = 0;
    const BOOL read = ReadFile(file, bytes, sizeof(bytes), &length, nullptr); CloseHandle(file);
    vex::UpdateManifest manifest;
    DWORD error = !read || !vex::parseUpdateManifest(bytes, length, manifest) ? ERROR_INVALID_DATA : vex::verifyUpdatePackage(argv[2], manifest);
    if (error) fprintf(stderr, "Update package verification failed: %lu\n", error);
    LocalFree(argv);
    return int(error);
}
int testUpdateInstallation() {
    int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 3) { if (argv) LocalFree(argv); return 1; }
    vex::UpdateInstallation installation;
    const bool found = vex::findUpdateInstallation(argv[2], installation);
    if (found) wprintf(L"Installed Vex found: %ls (%ls)\n", installation.directory, installation.machine ? L"machine" : L"user");
    LocalFree(argv);
    CHECK_UPDATE(found);
    return 0;
}
int testUpdateDownload() {
    int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 3) { if (argv) LocalFree(argv); return 1; }
    HANDLE file = CreateFileW(argv[2], GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    LocalFree(argv);
    if (file == INVALID_HANDLE_VALUE) return 2;
    char bytes[161]; DWORD length = 0;
    const BOOL read = ReadFile(file, bytes, sizeof(bytes), &length, nullptr); CloseHandle(file);
    vex::UpdateManifest manifest;
    CHECK_UPDATE(read && vex::parseUpdateManifest(bytes, length, manifest));
    wchar_t stage[MAX_PATH]; CHECK_UPDATE(!vex::createUpdateStage(stage));
    struct Cleanup { const wchar_t* path; ~Cleanup() { vex::removeUpdateStage(path); } } cleanup{stage};
    wchar_t path[MAX_PATH]; swprintf(path, MAX_PATH, L"%ls\\update.part", stage);
    HANDLE output = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    CHECK_UPDATE(output != INVALID_HANDLE_VALUE);
    vex::UpdateDownload download;
    unsigned percent = 0;
    const auto progress = [](void* context, unsigned value) { *static_cast<unsigned*>(context) = value; };
    const DWORD error = download.downloadPackage(manifest, output, progress, &percent);
    CloseHandle(output);
    if (error) fprintf(stderr, "Update download failed: %lu\n", error);
    CHECK_UPDATE(!error && percent == 100);
    CHECK_UPDATE(!vex::verifyUpdatePackage(path, manifest));
    output = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    CHECK_UPDATE(output != INVALID_HANDLE_VALUE);
    const uint64_t verifiedSize = manifest.size;
    manifest.size = 1048576;
    const DWORD oversize = download.downloadPackage(manifest, output, nullptr, nullptr);
    CloseHandle(output);
    CHECK_UPDATE(oversize == ERROR_FILE_TOO_LARGE);
    // Abort a real HTTPS transfer from its progress callback, not just before opening it.
    output = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    CHECK_UPDATE(output != INVALID_HANDLE_VALUE);
    manifest.size = verifiedSize;
    const auto cancel = [](void* context, unsigned) { static_cast<vex::UpdateDownload*>(context)->cancel(); };
    const DWORD interrupted = download.downloadPackage(manifest, output, cancel, &download);
    CloseHandle(output);
    CHECK_UPDATE(interrupted == ERROR_CANCELLED);
    puts("HTTPS download, GitHub redirects, MSI verification, oversize rejection and active cancellation passed.");
    return 0;
}
