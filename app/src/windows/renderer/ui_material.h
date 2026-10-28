#pragma once
#include "ui_renderer.h"
#include "drawing_color.h"
#include "windows_platform.h"

inline uint32_t materialMix(uint32_t a, uint32_t b, float amount) {
    const auto channel = [amount](uint32_t x, uint32_t y) -> uint32_t {
        return (uint32_t)((float)x + ((float)y - (float)x) * amount + 0.5f);
    };
    return (channel((a >> 16) & 255, (b >> 16) & 255) << 16)
        | (channel((a >> 8) & 255, (b >> 8) & 255) << 8)
        | channel(a & 255, b & 255);
}

// A quiet, opaque material keeps terminal text from showing through controls.
// It uses three batched primitives, with no backdrop capture or extra render pass
// on either Windows 10 or 11. Alpha belongs only to an existing enter/exit fade.
inline void fillUiMaterial(UiRenderer& target, UiBrush& brush, D2D1_ROUNDED_RECT bounds,
    VexChromePalette palette, uint32_t base, float alpha = 1.0f) {
    const uint32_t top = materialMix(base, 0xffffff, palette.is_dark ? 0.045f : 0.42f);
    const uint32_t bottom = materialMix(base, palette.accent, palette.is_dark ? 0.018f : 0.012f);
    target.FillRoundedGradient(bounds, rgb(top, alpha), rgb(bottom, alpha));
    brush.SetColor(rgb(materialMix(palette.border, 0xffffff, palette.is_dark ? 0.10f : 0.32f), alpha * 0.78f));
    target.DrawRoundedRectangle(bounds, &brush, 1);
    const float inset = bounds.radiusX + 1;
    if (bounds.rect.right - bounds.rect.left > inset * 2) {
        brush.SetColor(rgb(0xffffff, alpha * (palette.is_dark ? 0.12f : 0.65f)));
        target.DrawLine(D2D1::Point2F(bounds.rect.left + inset, bounds.rect.top + 1),
            D2D1::Point2F(bounds.rect.right - inset, bounds.rect.top + 1), &brush, 1);
    }
}
