#pragma once
#include <stddef.h>
#include <stdint.h>
#include <wchar.h>
#include <windows.h>

struct WslProcessProbe;
WslProcessProbe* createWslProcessProbe(const wchar_t* command, HWND window, uint32_t token);
void stopWslProcessProbe(WslProcessProbe* probe);
void noteWslProcessInput(WslProcessProbe* probe);
void destroyWslProcessProbe(WslProcessProbe* probe);
bool readWslProcessSnapshot(WslProcessProbe* probe, uint8_t* bytes, size_t capacity, size_t* length);
