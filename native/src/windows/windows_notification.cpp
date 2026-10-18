#include "windows_notification.h"
#include <windows.h>
#include <math.h>

HWND vex_native_window();
namespace {
wchar_t notificationText[768] = {};
UINT32 notificationLength = 0;
D2D1_RECT_F notificationBounds = {};
D2D1_COLOR_F notificationColor(uint32_t rgb) { return D2D1::ColorF(rgb); }
}

extern "C" void vex_dialog_warning(VexDialogText message) {
    if (!message.bytes || !message.length || message.length > 4096) return;
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        reinterpret_cast<const char*>(message.bytes), (int)message.length, notificationText, 767);
    if (!length) return;
    notificationText[length] = 0;
    notificationLength = (UINT32)length;
    notificationBounds = {};
    HWND window = vex_native_window();
    // One expiry wakeup, not an animation or a periodic file/configuration poll.
    SetTimer(window, 4, 30000, nullptr);
    vex_window_invalidate();
}

void clearTerminalNotification() {
    notificationLength = 0;
    notificationBounds = {};
    KillTimer(vex_native_window(), 4);
    vex_window_invalidate();
}

bool dismissTerminalNotificationAt(float x, float y) {
    if (!notificationLength || notificationBounds.right <= notificationBounds.left || x < notificationBounds.left || x > notificationBounds.right ||
        y < notificationBounds.top || y > notificationBounds.bottom) return false;
    clearTerminalNotification();
    return true;
}

void drawTerminalNotification(UiRenderer& target, UiBrush& brush, IDWriteTextFormat* heading,
                              IDWriteTextFormat* font, VexChromePalette palette) {
    notificationBounds = {};
    if (!notificationLength) return;
    const auto size = target.GetSize();
    const float width = fminf(520, size.width - 32), height = fminf(196, size.height - 80);
    if (width < 140 || height < 96) return;
    notificationBounds = D2D1::RectF(size.width - width - 16, size.height - height - 16, size.width - 16, size.height - 16);
    const auto bounds = notificationBounds;
    brush.SetColor(notificationColor(palette.pane_bg));
    target.FillRoundedRectangle(D2D1::RoundedRect(bounds, 12, 12), &brush);
    brush.SetColor(notificationColor(palette.border));
    target.DrawRoundedRectangle(D2D1::RoundedRect(bounds, 12, 12), &brush, 1);
    brush.SetColor(notificationColor(palette.accent));
    target.FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.left + 16, bounds.top + 18, bounds.left + 19, bounds.bottom - 18), 1.5f, 1.5f), &brush);
    brush.SetColor(notificationColor(palette.text_primary));
    target.DrawText(L"Configuration", 13, heading, D2D1::RectF(bounds.left + 32, bounds.top + 14, bounds.right - 38, bounds.top + 38), &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    brush.SetColor(notificationColor(palette.text_muted));
    target.DrawText(L"×", 1, font, D2D1::RectF(bounds.right - 30, bounds.top + 14, bounds.right - 12, bounds.top + 38), &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    target.DrawText(notificationText, notificationLength, font, D2D1::RectF(bounds.left + 32, bounds.top + 46, bounds.right - 20, bounds.bottom - 30), &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    target.DrawText(L"Click to dismiss · terminals keep running", 40, font, D2D1::RectF(bounds.left + 32, bounds.bottom - 26, bounds.right - 16, bounds.bottom - 6), &brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
}
