#pragma once
#include <stdint.h>
#include "icon_contrast.h"

// A faceted hexagon stays legible at tab size without the tiny JS lettering.
// Share the 24-unit geometry between the atlas and GDI+ dialog renderers.
struct NodeIconPoint { float x, y; };
struct NodeIconFace { NodeIconPoint points[4]; uint32_t color; };
static constexpr NodeIconFace nodeIconFaces[] = {
    { { { 12, 1 }, { 21.5f, 6.5f }, { 12, 12 }, { 2.5f, 6.5f } }, 0xECEEF2 },
    { { { 2.5f, 6.5f }, { 12, 12 }, { 12, 23 }, { 2.5f, 17.5f } }, 0xB4BAC4 },
    { { { 12, 12 }, { 21.5f, 6.5f }, { 21.5f, 17.5f }, { 12, 23 } }, 0x7C8492 },
};

inline uint32_t nodeIconColor(uint8_t face, uint32_t background) {
    const uint32_t endpoint = iconContrastRatio(0, background) >= iconContrastRatio(0xffffff, background) ? 0 : 255;
    const uint32_t anchor = nodeIconFaces[endpoint ? 2 : 0].color;
    for (unsigned step = 0; step <= 32; ++step) {
        const auto mix = [&](uint32_t rgb) {
            const auto channel = [&](unsigned shift) { return (((rgb >> shift) & 255) * (32 - step) + endpoint * step) / 32; };
            return (channel(16) << 16) | (channel(8) << 8) | channel(0);
        };
        // Move all faces together so contrast correction preserves the lighting.
        if (iconContrastRatio(mix(anchor), background) >= 3.0) return mix(nodeIconFaces[face].color);
    }
    return endpoint ? 0xffffff : 0;
}
