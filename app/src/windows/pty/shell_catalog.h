#pragma once
#include "windows_platform.h"
#include <windows.h>
#include <string.h>

inline void extractLowerStem(const wchar_t* path, wchar_t (&stem)[64]) {
    const wchar_t* start = path;
    for (const wchar_t* p = path; *p; ++p) {
        if (*p == L'\\' || *p == L'/') start = p + 1;
    }
    size_t len = 0;
    while (start[len] && len < 63) {
        wchar_t c = start[len];
        stem[len] = (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + 32) : c;
        ++len;
    }
    stem[len] = 0;
    if (len > 4 && (wcscmp(stem + len - 4, L".exe") == 0 || wcscmp(stem + len - 4, L".cmd") == 0 ||
                    wcscmp(stem + len - 4, L".bat") == 0 || wcscmp(stem + len - 4, L".com") == 0)) {
        stem[len - 4] = 0;
    }
}

// Owned file identities let nested shells be recognized without live profile sessions.
struct ShellCatalog {
    struct Image { wchar_t stem[64]; BY_HANDLE_FILE_INFORMATION info; };
    Image images[64] = {};
    size_t count = 0;

    int set(const VexDialogText* programs, size_t programCount) {
        if (programCount > 64 || (programCount && !programs)) return ERROR_INVALID_PARAMETER;
        ShellCatalog updated;
        for (size_t i = 0; i < programCount; ++i) {
            const auto& program = programs[i];
            if (!program.bytes || !program.length || program.length > 32767) return ERROR_INVALID_PARAMETER;
            wchar_t path[32768];
            const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                reinterpret_cast<const char*>(program.bytes), static_cast<int>(program.length), path, 32767);
            if (!length) return ERROR_INVALID_PARAMETER;
            path[length] = 0;
            HANDLE image = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, 0, nullptr);
            if (image == INVALID_HANDLE_VALUE) continue;
            const bool queried = GetFileInformationByHandle(image, &updated.images[updated.count].info) != FALSE;
            CloseHandle(image);
            if (!queried) continue;
            extractLowerStem(path, updated.images[updated.count++].stem);
        }
        // Publish only complete catalogs; all access belongs to the window thread.
        *this = updated;
        return 0;
    }
    bool hasStem(const wchar_t* stem) const {
        for (size_t i = 0; i < count; ++i) if (wcscmp(images[i].stem, stem) == 0) return true;
        return false;
    }
    bool matches(const BY_HANDLE_FILE_INFORMATION& info) const {
        for (size_t i = 0; i < count; ++i) {
            const auto& configured = images[i].info;
            if (configured.dwVolumeSerialNumber == info.dwVolumeSerialNumber &&
                configured.nFileIndexHigh == info.nFileIndexHigh && configured.nFileIndexLow == info.nFileIndexLow) return true;
        }
        return false;
    }
};
