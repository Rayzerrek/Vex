#pragma once
#include <stdint.h>
#include <d2d1.h>

// Palette colors are 0xRRGGBB; alpha is a separate paint value, not a packed channel.
inline D2D1_COLOR_F rgb(uint32_t color, float alpha = 1.0f) {
    return D2D1::ColorF((color >> 16) / 255.0f, ((color >> 8) & 255) / 255.0f, (color & 255) / 255.0f, alpha);
}
