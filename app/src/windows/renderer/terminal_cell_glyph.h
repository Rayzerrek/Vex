#pragma once
#include <stddef.h>
#include <stdint.h>

constexpr bool terminalDashVertical(uint32_t cp) {
    return cp == 0x2506 || cp == 0x2507 || cp == 0x250a ||
        cp == 0x250b || cp == 0x254e || cp == 0x254f;
}

// Only procedural cell graphics use integer screen edges; text keeps its
// fractional positioning and font antialiasing.
constexpr bool terminalCellGlyph(const uint8_t* text, size_t length) {
    if (length != 3 || (text[0] & 0xf0) != 0xe0 ||
        (text[1] & 0xc0) != 0x80 || (text[2] & 0xc0) != 0x80) return false;
    const uint32_t cp = ((uint32_t)(text[0] & 15) << 12) |
        ((uint32_t)(text[1] & 63) << 6) | (text[2] & 63);
    return (cp >= 0x2500 && cp <= 0x259f) ||
        (cp >= 0x2800 && cp <= 0x28ff) || (cp >= 0xe0b0 && cp <= 0xe0b7);
}
