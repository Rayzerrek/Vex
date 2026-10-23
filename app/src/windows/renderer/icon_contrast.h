#pragma once
#include <stdint.h>
#include <math.h>

inline double iconLuminance(uint32_t rgb) {
    const auto linear = [](uint32_t channel) {
        const double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * linear((rgb >> 16) & 255) + 0.7152 * linear((rgb >> 8) & 255) + 0.0722 * linear(rgb & 255);
}

inline double iconContrastRatio(uint32_t foreground, uint32_t background) {
    const double a = iconLuminance(foreground), b = iconLuminance(background);
    return (fmax(a, b) + 0.05) / (fmin(a, b) + 0.05);
}

// Preserve brand colors when readable; otherwise move toward the contrasting
// endpoint just far enough to meet the 3:1 non-text contrast threshold.
inline uint32_t contrastingIconColor(uint32_t foreground, uint32_t background) {
    if (iconContrastRatio(foreground, background) >= 3.0) return foreground;
    const uint32_t endpoint = iconContrastRatio(0, background) >= iconContrastRatio(0xffffff, background) ? 0 : 255;
    for (unsigned step = 1; step <= 32; ++step) {
        const auto channel = [&](unsigned shift) {
            return (((foreground >> shift) & 255) * (32 - step) + endpoint * step) / 32;
        };
        const uint32_t candidate = (channel(16) << 16) | (channel(8) << 8) | channel(0);
        if (iconContrastRatio(candidate, background) >= 3.0) return candidate;
    }
    return endpoint ? 0xffffff : 0;
}
