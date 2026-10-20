#pragma once

#include <stdint.h>

// Geometry is independent of Win32 so the overlay never needs a native scrollbar gutter.
struct OverlayScrollbar {
    float height = 0, thumbHeight = 0, thumbTop = 0;
    int maximum = 0;
    static constexpr OverlayScrollbar compute(int count, int visible, int first, float height, float minimum) {
        if (visible <= 0 || count <= visible || height <= 0) return {};
        const float proportional = height * visible / count;
        const float thumb = proportional < minimum ? (minimum < height ? minimum : height) : proportional;
        const int maximum = count - visible;
        const int top = first < 0 ? 0 : first > maximum ? maximum : first;
        return { height, thumb, (height - thumb) * top / maximum, maximum };
    }
    constexpr int target(float y, float grab) const {
        const float travel = height - thumbHeight;
        if (travel <= 0 || maximum <= 0) return 0;
        const float offset = y - grab;
        return offset <= 0 ? 0 : offset >= travel ? maximum : (int)(offset * maximum / travel + 0.5f);
    }
};

struct OverlayScrollbarVisibility {
    uint64_t hideAt = 0;
    bool revealed = false;
    constexpr void reveal(uint64_t now) { hideAt = now + 900; revealed = true; }
    constexpr float opacity(uint64_t now, bool active, bool animations) const {
        if (active) return 1;
        if (!revealed) return 0;
        if (now < hideAt) return 1;
        if (!animations || now >= hideAt + 220) return 0;
        const float t = (float)(hideAt + 220 - now) / 220;
        return t * t * (3 - 2 * t);
    }
};
