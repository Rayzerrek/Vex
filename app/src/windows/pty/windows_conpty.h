#pragma once
#include <windows.h>

// Use one matched API/host pair; inbox ConPTY on older Windows drops ED3.
struct VexConptyApi {
    HMODULE module;
    HRESULT (WINAPI* create)(COORD, HANDLE, HANDLE, DWORD, HPCON*);
    HRESULT (WINAPI* resize)(HPCON, COORD);
    void (WINAPI* close)(HPCON);

    DWORD load();
    void unload();
};
