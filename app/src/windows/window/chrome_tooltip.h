#pragma once
#include <stdint.h>

// A single delayed wake, then a short reveal. A settled tooltip never requests frames.
struct ChromeTooltip {
    uint64_t key = 0;
    double due = 0, warmUntil = 0;
    bool instant = false;
    static constexpr double delayMs = 450, revealMs = 80, warmMs = 200;

    constexpr void update(uint64_t next, double now) {
        if (key && now >= due) warmUntil = now + warmMs;
        if (next == key) return;
        key = next;
        instant = next && now < warmUntil;
        due = next ? now + (instant ? 0 : delayMs) : 0;
    }
    constexpr bool visible(double now) const { return key && now >= due; }
    constexpr double deadline(double now) const { return key && now < due ? due : 0; }
    constexpr float opacity(double now, bool animate) const {
        if (!visible(now)) return 0;
        if (!animate || instant || now >= due + revealMs) return 1;
        const float remaining = 1 - (float)((now - due) / revealMs);
        return 1 - remaining * remaining * remaining;
    }
    constexpr bool animating(double now, bool animate) const {
        return animate && !instant && visible(now) && now < due + revealMs;
    }
    constexpr void reset() { key = 0; due = warmUntil = 0; instant = false; }
};
