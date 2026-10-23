#pragma once
#include <stdint.h>

// Pi's pixel mark, shared by the chrome and GDI+ dialog renderers.
// Coordinates use the same 24-unit square as the monochrome icon catalog.
struct PiIconBlock { float left, top, right, bottom; uint32_t color; };
static constexpr PiIconBlock piIconBlocks[] = {
    { 1, 1, 17.5f, 6.5f, 0xEE9080 },
    { 12, 6.5f, 17.5f, 12, 0xEE9080 },
    { 1, 6.5f, 6.5f, 23, 0x5098B8 },
    { 6.5f, 12, 12, 17.5f, 0x5098B8 },
    { 17.5f, 12, 23, 23, 0xF2C35B },
};
