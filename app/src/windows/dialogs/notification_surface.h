#pragma once
#include "windows_platform.h"
#include "ui_renderer.h"
#include "chrome_motion.h"

// Updates, errors and session completion share this surface, including geometry and hit testing.
enum class NotificationTone { info = VEX_NOTICE_INFO, success = VEX_NOTICE_SUCCESS, warning = VEX_NOTICE_WARNING, error = VEX_NOTICE_ERROR, progress = 4 };
enum class NotificationHit { none, body, action, close };
struct NotificationContent {
    NotificationTone tone = NotificationTone::info;
    const wchar_t* title = L"";
    const wchar_t* message = L"";
    const wchar_t* action = L"";
    int progress = -1;
};
struct NotificationReveal {
    ChromeMotion opacity;
    constexpr NotificationReveal() { opacity.update(0, 0, false); }
    constexpr float update(bool visible, double now, bool animate) { return opacity.update(visible ? 1.0f : 0.0f, now, animate); }
    constexpr bool pending(double now) const { return opacity.pending(now); }
};
struct NotificationGeometry {
    D2D1_RECT_F bounds = {}, title = {}, message = {}, action = {}, close = {};
    // Content sizes are measured only when text/font changes; coordinates remain in DIPs.
    static NotificationGeometry place(float viewportWidth, float viewportHeight, float bottomInset,
        float titleWidth, float messageWidth, float messageHeight, float actionWidth, bool hasMessage, float reveal);
    NotificationHit hit(float x, float y) const;
};

class NotificationSurface final {
public:
    // All strings are borrowed for this draw only; caches own their bounded copies.
    float draw(UiRenderer& target, UiBrush& brush, IDWriteFactory* factory, IDWriteTextFormat* heading,
        IDWriteTextFormat* body, VexChromePalette palette, NotificationContent content,
        bool visible, float bottomInset, double now, bool animate);
    NotificationHit pointer(float x, float y, bool interactive);
    void reveal() { restartReveal = true; }
    void reset();
    bool animationPending() const { return pending; }
private:
    struct TextMeasure {
        wchar_t text[4097] = {};
        IDWriteTextFormat* font = nullptr;
        float width = 0, height = 0, wrappedWidth = -1;
        bool set(IDWriteFactory* factory, IDWriteTextFormat* format, const wchar_t* value);
        bool wrap(IDWriteFactory* factory, float width);
    } title, message, action;
    NotificationGeometry geometry;
    NotificationReveal visibility;
    ChromeMotion closeHover, actionHover;
    NotificationTone tone = NotificationTone::info;
    NotificationHit hovered = NotificationHit::none;
    NotificationHit pointerHit(bool interactive) const;
    float alpha = 0, pointerX = -1, pointerY = -1;
    int progress = -1;
    bool restartReveal = false, pending = false;
};
