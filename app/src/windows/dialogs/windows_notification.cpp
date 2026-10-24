#include "windows_notification.h"
#include <windows.h>
#include <wchar.h>
#include <string.h>

HWND vex_native_window();
namespace {
wchar_t notificationTitle[128] = {}, notificationText[4097] = {};
NotificationTone notificationTone = NotificationTone::error;
bool notificationVisible = false;
NotificationSurface surface;
}
extern "C" void vex_window_notice(VexNoticeRequest request) {
    HWND window = vex_native_window();
    if (!window || request.kind > VEX_NOTICE_ERROR || !request.title.bytes || !request.title.length || request.title.length > 512 ||
        !request.message.bytes || !request.message.length || request.message.length > 4096) return;
    // Decode into temporary owners first: malformed input must not partially replace a visible message.
    wchar_t title[128], message[4097];
    const int titleLength = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        reinterpret_cast<const char*>(request.title.bytes), (int)request.title.length, title, 127);
    const int messageLength = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        reinterpret_cast<const char*>(request.message.bytes), (int)request.message.length, message, 4096);
    if (!titleLength || !messageLength || wmemchr(title, 0, titleLength) || wmemchr(message, 0, messageLength)) return;
    title[titleLength] = 0; message[messageLength] = 0;
    wcscpy(notificationTitle, title); wcscpy(notificationText, message);
    notificationTone = static_cast<NotificationTone>(request.kind);
    notificationVisible = true;
    surface.reveal();
    SetTimer(window, 4, 30000, nullptr);
    vex_window_invalidate();
}
bool terminalNotificationVisible() { return notificationVisible; }
bool terminalNotificationAnimationPending() { return surface.animationPending(); }
void clearTerminalNotification() {
    notificationVisible = false; notificationTitle[0] = 0; notificationText[0] = 0;
    surface.reset();
    KillTimer(vex_native_window(), 4);
    vex_window_invalidate();
}
void dismissTerminalNotification() {
    if (!notificationVisible) return;
    notificationVisible = false;
    KillTimer(vex_native_window(), 4);
    vex_window_invalidate();
}
NotificationHit terminalNotificationPointer(float x, float y, bool click) {
    const auto hit = surface.pointer(x, y, notificationVisible);
    if (click && hit == NotificationHit::close) dismissTerminalNotification();
    return hit;
}
bool terminalNotificationKey(uint32_t key, uint32_t modifiers) {
    if (!notificationVisible || key != VK_OEM_PERIOD || modifiers != 3) return false;
    dismissTerminalNotification(); return true;
}
void drawTerminalNotification(UiRenderer& target, UiBrush& brush, IDWriteFactory* factory, IDWriteTextFormat* heading,
    IDWriteTextFormat* font, VexChromePalette palette, float bottomInset, double now) {
    surface.draw(target, brush, factory, heading, font, palette,
        NotificationContent{notificationTone, notificationTitle, notificationText}, notificationVisible, bottomInset,
        now, vex_window_animations_enabled());
}
void testTerminalNotification(uint32_t kind) {
    if (kind > 6) return;
    if (kind == 6) {
        constexpr uint8_t title[] = "Must not replace the previous notification", message[] = {0xff};
        vex_window_notice(VexNoticeRequest{VEX_NOTICE_ERROR, {title, sizeof(title) - 1}, {message, sizeof(message)}});
        return;
    }
    if (kind == 5) {
        constexpr uint8_t title[] = "Long notification · Zażółć gęślą jaźń";
        uint8_t message[4096];
        for (size_t i = 0; i < sizeof(message); ++i) message[i] = i % 8 == 7 ? ' ' : 'W';
        vex_window_notice(VexNoticeRequest{VEX_NOTICE_WARNING, {title, sizeof(title) - 1}, {message, sizeof(message)}});
        return;
    }
    if (kind == 4) {
        constexpr uint8_t title[] = "Notification test", message[] = "Short message.";
        vex_window_notice(VexNoticeRequest{VEX_NOTICE_INFO, {title, sizeof(title) - 1}, {message, sizeof(message) - 1}});
        return;
    }
    const char* titles[] = {"Update preview", "Session finished", "Configuration needs attention", "Action could not be completed"};
    const char* messages[] = {
        "This is a preview. No update will be installed and your terminals keep running.",
        "Exit code 0. Output is preserved. Ctrl+Shift+T opens a new terminal.",
        "Your configuration could not be loaded. Previous settings are still active and your Lua files have not changed.",
        "Could not prepare the updater. Vex and your terminals were not closed. Please try again."
    };
    vex_window_notice(VexNoticeRequest{kind,
        VexDialogText{reinterpret_cast<const uint8_t*>(titles[kind]), strlen(titles[kind])},
        VexDialogText{reinterpret_cast<const uint8_t*>(messages[kind]), strlen(messages[kind])}});
}
