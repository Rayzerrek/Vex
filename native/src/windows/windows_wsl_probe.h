#pragma once
#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

struct WslProcessProbe;
WslProcessProbe* createWslProcessProbe(const wchar_t* command);
void destroyWslProcessProbe(WslProcessProbe* probe);
bool readWslProcessSnapshot(WslProcessProbe* probe, uint8_t* bytes, size_t capacity, size_t* length);
