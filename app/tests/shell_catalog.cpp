#include "shell_catalog.h"
#include <stdio.h>

#define REQUIRE(condition) do { if (!(condition)) { fprintf(stderr, "Shell catalog assertion at line %d: %s\n", __LINE__, #condition); return 1; } } while (0)

namespace {
struct Files {
    wchar_t root[MAX_PATH] = {}, subdirectory[MAX_PATH] = {}, first[MAX_PATH] = {}, second[MAX_PATH] = {};
    ~Files() { DeleteFileW(first); DeleteFileW(second); RemoveDirectoryW(subdirectory); RemoveDirectoryW(root); }
};
bool createImage(const wchar_t* path, BY_HANDLE_FILE_INFORMATION& info) {
    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    const bool result = GetFileInformationByHandle(file, &info) != FALSE;
    CloseHandle(file);
    return result;
}
VexDialogText text(const char* path) { return {reinterpret_cast<const uint8_t*>(path), strlen(path)}; }
}

int testShellCatalog() {
    Files files;
    wchar_t temporary[MAX_PATH];
    REQUIRE(GetTempPathW(MAX_PATH, temporary));
    REQUIRE(GetTempFileNameW(temporary, L"vsc", 0, files.root));
    REQUIRE(DeleteFileW(files.root) && CreateDirectoryW(files.root, nullptr));
    REQUIRE(swprintf_s(files.subdirectory, L"%s\\other", files.root) > 0);
    REQUIRE(CreateDirectoryW(files.subdirectory, nullptr));
    REQUIRE(swprintf_s(files.first, L"%s\\my-custom-shell.exe", files.root) > 0);
    REQUIRE(swprintf_s(files.second, L"%s\\my-custom-shell.exe", files.subdirectory) > 0);
    BY_HANDLE_FILE_INFORMATION first = {}, second = {};
    REQUIRE(createImage(files.first, first) && createImage(files.second, second));
    char firstPath[MAX_PATH * 4], secondPath[MAX_PATH * 4];
    REQUIRE(WideCharToMultiByte(CP_UTF8, 0, files.first, -1, firstPath, sizeof(firstPath), nullptr, nullptr));
    REQUIRE(WideCharToMultiByte(CP_UTF8, 0, files.second, -1, secondPath, sizeof(secondPath), nullptr, nullptr));

    ShellCatalog catalog;
    REQUIRE(!catalog.hasStem(L"my-custom-shell") && !catalog.matches(first));
    auto program = text(firstPath);
    REQUIRE(catalog.set(&program, 1) == 0);
    // Any saved executable can be a shell, even without a running profile session.
    REQUIRE(catalog.hasStem(L"my-custom-shell") && catalog.matches(first));
    REQUIRE(!catalog.matches(second));
    VexDialogText invalid = {};
    REQUIRE(catalog.set(&invalid, 1) == ERROR_INVALID_PARAMETER);
    REQUIRE(catalog.matches(first));
    program = text(secondPath);
    REQUIRE(catalog.set(&program, 1) == 0);
    REQUIRE(catalog.matches(second) && !catalog.matches(first));
    REQUIRE(catalog.set(nullptr, 0) == 0);
    REQUIRE(!catalog.hasStem(L"my-custom-shell") && !catalog.matches(second));
    wchar_t stem[64];
    extractLowerStem(L"C:\\arbitrary\\My-Custom-Shell.EXE", stem);
    REQUIRE(wcscmp(stem, L"my-custom-shell") == 0);
    return 0;
}
