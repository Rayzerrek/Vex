#include "windows_platform.h"
#include <windows.h>
#include <userenv.h>
#include <limits.h>
#include <string.h>

HWND vex_native_window();

extern "C" int vex_machine_name(uint8_t* bytes, size_t capacity, size_t* length) {
    if (!bytes || !length || capacity > INT_MAX) return ERROR_INVALID_PARAMETER;
    wchar_t name[MAX_COMPUTERNAME_LENGTH + 1]; DWORD units = MAX_COMPUTERNAME_LENGTH + 1;
    if (!GetComputerNameW(name, &units)) return (int)GetLastError();
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, (int)units, (char*)bytes, (int)capacity, nullptr, nullptr);
    if (!count) return (int)GetLastError();
    *length = (size_t)count;
    return 0;
}

extern "C" int vex_system_directory(uint8_t* bytes, size_t capacity, size_t* length) {
    if (!bytes || !length || capacity > INT_MAX) return ERROR_INVALID_PARAMETER;
    wchar_t path[MAX_PATH]; UINT units = GetSystemDirectoryW(path, MAX_PATH);
    if (!units) return (int)GetLastError();
    if (units >= MAX_PATH) return ERROR_INSUFFICIENT_BUFFER;
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path, (int)units, (char*)bytes, (int)capacity, nullptr, nullptr);
    if (!count) return (int)GetLastError();
    *length = (size_t)count;
    return 0;
}

// Installers update the persisted environment, not the environment of an already running Vex.
extern "C" int vex_shell_path(uint8_t* bytes, size_t capacity, size_t* length) {
    if (!bytes || !length || capacity > INT_MAX) return ERROR_INVALID_PARAMETER;
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &token)) return (int)GetLastError();
    void* block = nullptr;
    const BOOL created = CreateEnvironmentBlock(&block, token, FALSE);
    const DWORD error = created ? ERROR_SUCCESS : GetLastError();
    CloseHandle(token);
    if (!created) return (int)error;
    int result = ERROR_ENVVAR_NOT_FOUND;
    for (const wchar_t* entry = static_cast<const wchar_t*>(block); *entry; entry += wcslen(entry) + 1) {
        if (_wcsnicmp(entry, L"PATH=", 5) != 0) continue;
        const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, entry + 5, (int)wcslen(entry + 5), (char*)bytes, (int)capacity, nullptr, nullptr);
        result = count ? ERROR_SUCCESS : (int)GetLastError();
        if (count) *length = (size_t)count;
        break;
    }
    DestroyEnvironmentBlock(block);
    return result;
}

extern "C" int vex_clipboard_write(const uint8_t* bytes, size_t length) {
    if (!bytes || !length || length > 768 * 1024 - 4) return ERROR_INVALID_PARAMETER;
    int units = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)bytes, (int)length, nullptr, 0);
    if (units <= 0) return (int)GetLastError();
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, ((size_t)units + 1) * sizeof(wchar_t));
    if (!memory) return ERROR_OUTOFMEMORY;
    auto wide = static_cast<wchar_t*>(GlobalLock(memory));
    if (!wide) { DWORD error = GetLastError(); GlobalFree(memory); return (int)(error ? error : ERROR_OUTOFMEMORY); }
    int written = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)bytes, (int)length, wide, units);
    wide[units] = 0;
    GlobalUnlock(memory);
    if (written != units) { GlobalFree(memory); return ERROR_NO_UNICODE_TRANSLATION; }
    const HWND window = vex_native_window();
    if (!OpenClipboard(IsWindow(window) ? window : nullptr)) { GlobalFree(memory); return 0; }
    if (!EmptyClipboard() || !SetClipboardData(CF_UNICODETEXT, memory)) GlobalFree(memory);
    CloseClipboard();
    return 0;
}

extern "C" void* vex_terminal_symbol(const char* name) {
    static HMODULE library;
    if (!library) {
        wchar_t path[32768]; DWORD length = GetModuleFileNameW(nullptr, path, 32768);
        if (!length || length >= 32768) return nullptr;
        wchar_t* slash = wcsrchr(path, L'\\');
        if (!slash || (slash - path) + 16 >= 32768) return nullptr;
        wcscpy(slash + 1, L"ghostty-vt.dll");
        library = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    }
    return library ? reinterpret_cast<void*>(GetProcAddress(library, name)) : nullptr;
}
