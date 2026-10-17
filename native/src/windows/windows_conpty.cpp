#include "windows_conpty.h"
#include <wchar.h>

DWORD VexConptyApi::load() {
    wchar_t path[32768];
    const DWORD length = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    if (!length) return GetLastError();
    if (length >= ARRAYSIZE(path)) return ERROR_INSUFFICIENT_BUFFER;
    wchar_t* filename = wcsrchr(path, L'\\');
    if (!filename) return ERROR_BAD_PATHNAME;
    ++filename;
    if ((size_t)(filename - path) + ARRAYSIZE(L"OpenConsole.exe") > ARRAYSIZE(path)) return ERROR_INSUFFICIENT_BUFFER;
    wcscpy(filename, L"OpenConsole.exe");
    const DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) return GetLastError();
    if (attributes & FILE_ATTRIBUTE_DIRECTORY) return ERROR_FILE_NOT_FOUND;
    // The NuGet DLL otherwise silently falls back to the broken system host.
    wcscpy(filename, L"conpty.dll");
    module = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return GetLastError();
    create = reinterpret_cast<decltype(create)>(reinterpret_cast<void*>(GetProcAddress(module, "ConptyCreatePseudoConsole")));
    resize = reinterpret_cast<decltype(resize)>(reinterpret_cast<void*>(GetProcAddress(module, "ConptyResizePseudoConsole")));
    close = reinterpret_cast<decltype(close)>(reinterpret_cast<void*>(GetProcAddress(module, "ConptyClosePseudoConsole")));
    if (!create || !resize || !close) {
        unload();
        return ERROR_PROC_NOT_FOUND;
    }
    return ERROR_SUCCESS;
}

void VexConptyApi::unload() {
    if (module) FreeLibrary(module);
    *this = {};
}
