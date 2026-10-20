#pragma once

// Window-thread chrome motion uses monotonic milliseconds and never changes terminal layout.
class ChromeMotion {
    float from = 0, destination = 0;
    double started = 0;
    bool initialized = false;
public:
    static constexpr double durationMs = 160;

    constexpr float sample(double now) const {
        const double elapsed = now - started;
        if (elapsed >= durationMs) return destination;
        if (elapsed <= 0) return from;
        const float remaining = 1 - (float)(elapsed / durationMs);
        return destination + (from - destination) * remaining * remaining * remaining;
    }
    constexpr float update(float target, double now, bool animate) {
        if (!initialized || !animate) {
            initialized = true; from = destination = target; started = now;
        } else if (target != destination) {
            from = sample(now); destination = target; started = now;
        }
        return sample(now);
    }
    constexpr bool pending(double now) const {
        return initialized && from != destination && now < started + durationMs;
    }
};
