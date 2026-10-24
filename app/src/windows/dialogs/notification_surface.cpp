#include "notification_surface.h"
#include <math.h>
#include <wchar.h>

bool NotificationSurface::TextMeasure::set(IDWriteFactory* factory, IDWriteTextFormat* format, const wchar_t* value) {
    if (font == format && !wcscmp(text, value)) return true;
    const size_t length = wcslen(value);
    if (length > 4096) return false;
    IDWriteTextLayout* layout = nullptr;
    HRESULT result = factory->CreateTextLayout(value, UINT32(length), format, 10000, 10000, &layout);
    if (SUCCEEDED(result)) result = layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    DWRITE_TEXT_METRICS metrics = {};
    if (SUCCEEDED(result)) result = layout->GetMetrics(&metrics);
    if (layout) layout->Release();
    if (FAILED(result)) return false;
    wcscpy(text, value); font = format;
    width = ceilf(metrics.widthIncludingTrailingWhitespace); height = ceilf(metrics.height); wrappedWidth = -1;
    return true;
}
bool NotificationSurface::TextMeasure::wrap(IDWriteFactory* factory, float available) {
    if (wrappedWidth == available) return true;
    IDWriteTextLayout* layout = nullptr;
    HRESULT result = factory->CreateTextLayout(text, UINT32(wcslen(text)), font, available, 10000, &layout);
    if (SUCCEEDED(result)) result = layout->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
    DWRITE_TEXT_METRICS metrics = {};
    if (SUCCEEDED(result)) result = layout->GetMetrics(&metrics);
    if (layout) layout->Release();
    if (FAILED(result)) return false;
    wrappedWidth = available; height = ceilf(metrics.height);
    return true;
}
void NotificationSurface::reset() {
    geometry = {}; visibility = {}; closeHover = {}; actionHover = {};
    alpha = 0; pointerX = pointerY = -1; hovered = NotificationHit::none; restartReveal = false; pending = false;
}
NotificationHit NotificationSurface::pointerHit(bool interactive) const {
    const auto hit = alpha > 0.05f ? geometry.hit(pointerX, pointerY) : NotificationHit::none;
    return !interactive && hit != NotificationHit::none ? NotificationHit::body : hit;
}
NotificationHit NotificationSurface::pointer(float x, float y, bool interactive) {
    pointerX = x; pointerY = y;
    const auto hit = pointerHit(interactive);
    if (hovered != hit) {
        const bool oldControl = hovered == NotificationHit::close || hovered == NotificationHit::action;
        const bool newControl = hit == NotificationHit::close || hit == NotificationHit::action;
        hovered = hit;
        if (oldControl || newControl) vex_window_invalidate();
    }
    return hit;
}
float NotificationSurface::draw(UiRenderer& target, UiBrush& brush, IDWriteFactory* factory, IDWriteTextFormat* heading,
    IDWriteTextFormat* body, VexChromePalette palette, NotificationContent content, bool visible, float bottomInset, double now, bool animate) {
    if (restartReveal) { visibility = {}; restartReveal = false; }
    alpha = visibility.update(visible, now, animate);
    pending = visibility.pending(now);
    if (!visible && alpha <= 0) {
        geometry = {};
        closeHover.update(0, now, false); actionHover.update(0, now, false);
        if (hovered != NotificationHit::none) { hovered = NotificationHit::none; vex_window_refresh_hover_cursor(); }
        return 0;
    }
    if (visible) {
        if (!title.set(factory, heading, content.title) || !message.set(factory, body, content.message) || !action.set(factory, heading, content.action)) { geometry = {}; return 0; }
        tone = content.tone; progress = content.progress;
    }
    const auto size = target.GetSize();
    const bool hasMessage = message.text[0] != 0;
    geometry = NotificationGeometry::place(size.width, size.height, bottomInset, title.width, message.width, 0, action.width, hasMessage, alpha);
    if (geometry.bounds.right <= geometry.bounds.left) return 0;
    if (hasMessage) {
        if (!message.wrap(factory, geometry.message.right - geometry.message.left)) { geometry = {}; return 0; }
        geometry = NotificationGeometry::place(size.width, size.height, bottomInset, title.width, message.width,
            fminf(message.height, body->GetFontSize() * 1.4f * 6), action.width, true, alpha);
    }
    const auto hit = pointerHit(visible);
    if (hovered != hit) { hovered = hit; vex_window_refresh_hover_cursor(); }
    const float closePhase = closeHover.update(visible && hovered == NotificationHit::close ? 1.0f : 0.0f, now, animate);
    const float actionPhase = actionHover.update(visible && hovered == NotificationHit::action ? 1.0f : 0.0f, now, animate);
    pending = pending || closeHover.pending(now) || actionHover.pending(now);
    const auto bounds = geometry.bounds;
    const float occupied = bounds.bottom - bounds.top + 8;
    if (alpha <= 0) return occupied;
    uint32_t accent = palette.accent;
    switch (tone) {
        case NotificationTone::success: accent = palette.is_dark ? 0x63d99a : 0x187248; break;
        case NotificationTone::warning: accent = palette.is_dark ? 0xe5bb7a : 0x8c5d1b; break;
        case NotificationTone::error: accent = palette.is_dark ? 0xf0919b : 0xad334b; break;
        default: break;
    }
    const float radius = hasMessage ? 14 : 18;
    brush.SetColor(D2D1::ColorF(0x000000, alpha * (palette.is_dark ? 0.16f : 0.06f)));
    auto shadow = bounds; shadow.top += 2; shadow.bottom += 2;
    target.FillRoundedRectangle(D2D1::RoundedRect(shadow, radius, radius), &brush);
    brush.SetColor(D2D1::ColorF(palette.pane_bg, alpha * 0.90f));
    target.FillRoundedRectangle(D2D1::RoundedRect(bounds, radius, radius), &brush);
    brush.SetColor(D2D1::ColorF(palette.border, alpha * 0.85f));
    target.DrawRoundedRectangle(D2D1::RoundedRect(bounds, radius, radius), &brush, 1);
    const auto icon = D2D1::Point2F(bounds.left + 20, bounds.top + 22);
    brush.SetColor(D2D1::ColorF(accent, alpha));
    target.DrawEllipse(D2D1::Ellipse(icon, 8, 8), &brush, 1.4f);
    if (tone == NotificationTone::success) {
        target.DrawLine(D2D1::Point2F(icon.x - 3, icon.y), D2D1::Point2F(icon.x - 1, icon.y + 2), &brush, 1.5f, true);
        target.DrawLine(D2D1::Point2F(icon.x - 1, icon.y + 2), D2D1::Point2F(icon.x + 4, icon.y - 3), &brush, 1.5f, true);
    } else if (tone == NotificationTone::progress) {
        target.DrawLine(D2D1::Point2F(icon.x, icon.y - 3), D2D1::Point2F(icon.x, icon.y + 3), &brush, 1.4f, true);
        target.DrawLine(D2D1::Point2F(icon.x - 2, icon.y + 1), D2D1::Point2F(icon.x, icon.y + 3), &brush, 1.4f, true);
        target.DrawLine(D2D1::Point2F(icon.x + 2, icon.y + 1), D2D1::Point2F(icon.x, icon.y + 3), &brush, 1.4f, true);
    } else {
        const bool info = tone == NotificationTone::info;
        target.DrawLine(D2D1::Point2F(icon.x, icon.y + (info ? -1 : -4)), D2D1::Point2F(icon.x, icon.y + (info ? 4 : 1)), &brush, 1.4f, true);
        target.FillEllipse(D2D1::Ellipse(D2D1::Point2F(icon.x, icon.y + (info ? -4 : 4)), 0.8f, 0.8f), &brush);
    }
    brush.SetColor(D2D1::ColorF(accent, alpha));
    target.DrawText(title.text, UINT32(wcslen(title.text)), heading, geometry.title, &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    if (hasMessage) {
        brush.SetColor(D2D1::ColorF(palette.text_primary, alpha * 0.88f));
        target.DrawText(message.text, UINT32(wcslen(message.text)), body, geometry.message, &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }
    if (action.text[0]) {
        brush.SetColor(D2D1::ColorF(accent, alpha * (0.12f + actionPhase * 0.11f)));
        target.FillRoundedRectangle(D2D1::RoundedRect(geometry.action, 14, 14), &brush);
        brush.SetColor(D2D1::ColorF(accent, alpha));
        auto label = geometry.action; label.left += 12; label.right -= 6; label.top += 7;
        target.DrawText(action.text, UINT32(wcslen(action.text)), heading, label, &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }
    if (closePhase > 0) {
        brush.SetColor(D2D1::ColorF(palette.inactive_tab_hover_bg, alpha * closePhase));
        target.FillRoundedRectangle(D2D1::RoundedRect(geometry.close, 8, 8), &brush);
    }
    const auto center = D2D1::Point2F((geometry.close.left + geometry.close.right) * 0.5f, (geometry.close.top + geometry.close.bottom) * 0.5f);
    brush.SetColor(D2D1::ColorF(closePhase > 0.5f ? palette.text_primary : palette.text_muted, alpha));
    target.DrawLine(D2D1::Point2F(center.x - 3.5f, center.y - 3.5f), D2D1::Point2F(center.x + 3.5f, center.y + 3.5f), &brush, 1.4f, true);
    target.DrawLine(D2D1::Point2F(center.x + 3.5f, center.y - 3.5f), D2D1::Point2F(center.x - 3.5f, center.y + 3.5f), &brush, 1.4f, true);
    if (progress > 0) {
        brush.SetColor(D2D1::ColorF(accent, alpha));
        target.FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.left + 38, bounds.bottom - 5,
            bounds.left + 38 + (bounds.right - bounds.left - 54) * fminf(100, (float)progress) / 100, bounds.bottom - 3), 1, 1), &brush);
    }
    return occupied;
}
