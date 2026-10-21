#pragma once

// A shared pixel offset drives both list rows and thumb; native LISTBOX scrolling is row-quantized.
struct PixelScroll {
    float from = 0, destination = 0, maximum = 0;
    double started = 0;
    static constexpr double duration = 100;

    constexpr float clamp(float value) const { return value < 0 ? 0 : value > maximum ? maximum : value; }
    constexpr float sample(double now) const {
        if (now <= started) return from;
        if (now >= started + duration) return destination;
        const float remaining = 1 - (float)((now - started) / duration);
        return destination + (from - destination) * remaining * remaining * remaining;
    }
    constexpr bool pending(double now) const { return from != destination && now < started + duration; }
    constexpr void range(float limit, double now) {
        const float next = limit > 0 ? limit : 0;
        if (next == maximum) return;
        const float position = sample(now);
        maximum = next;
        from = clamp(position); destination = clamp(destination); started = now;
    }
    constexpr void moveTo(float value, double now, bool animate) {
        const float next = clamp(value);
        if (!animate) { from = destination = next; started = now; }
        else if (next != destination) { from = sample(now); destination = next; started = now; }
    }
    constexpr void reveal(float top, float bottom, float viewport, double now, bool animate) {
        if (top < destination) moveTo(top, now, animate);
        else if (bottom > destination + viewport) moveTo(bottom - viewport, now, animate);
    }
};
