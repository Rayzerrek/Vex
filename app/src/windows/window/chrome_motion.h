#pragma once

// Window-thread chrome motion uses monotonic milliseconds and never changes terminal layout.
class ChromeMotion {
    float from = 0, destination = 0, slope = 3;
    double started = 0;
    bool initialized = false;
public:
    static constexpr double durationMs = 160;

    constexpr float sample(double now) const {
        const double elapsed = now - started;
        if (elapsed >= durationMs) return destination;
        if (elapsed <= 0) return from;
        const float t = (float)(elapsed / durationMs);
        const float remaining = 1 - t;
        const float progress = t * t * (3 - 2 * t) + slope * t * remaining * remaining;
        return from + (destination - from) * progress;
    }
    constexpr float velocity(double now) const {
        if (now <= started || !pending(now)) return 0;
        const float t = (float)((now - started) / durationMs);
        return (destination - from) / (float)durationMs * (6 * t * (1 - t) + slope * (1 - 4 * t + 3 * t * t));
    }
    constexpr float update(float target, double now, bool animate) {
        if (!initialized || !animate) {
            initialized = true; from = destination = target; started = now;
        } else if (target != destination) {
            const float current = sample(now), speed = velocity(now);
            const float distance = target - current;
            // Carry speed through same-direction retargets. Clamp the Hermite slope
            // so rapidly changing destinations cannot overshoot their hit targets.
            const float carry = distance != 0 ? speed * (float)durationMs / distance : 0;
            slope = pending(now) ? (carry < 0 ? 0 : carry > 3 ? 3 : carry) : 3;
            from = current; destination = target; started = now;
        }
        return sample(now);
    }
    constexpr bool pending(double now) const {
        return initialized && from != destination && now < started + durationMs;
    }
};
