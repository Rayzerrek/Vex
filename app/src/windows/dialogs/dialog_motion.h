#pragma once

// Opacity only: retargeting a reveal into dismissal must not move or relayout controls.
struct DialogMotion {
    float from = 220.0f / 255.0f, target = 1;
    double started = 0, duration = 120;

    constexpr float sample(double now) const {
        if (now <= started) return from;
        if (now >= started + duration) return target;
        const float remaining = 1 - (float)((now - started) / duration);
        return target + (from - target) * remaining * remaining * remaining;
    }
    constexpr void start(float destination, double now, bool closing) {
        from = closing ? sample(now) : 220.0f / 255.0f;
        target = destination;
        started = now;
        duration = closing ? 90 : 120;
    }
    constexpr bool pending(double now) const {
        return from != target && now < started + duration;
    }
};
