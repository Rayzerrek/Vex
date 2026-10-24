#pragma once
#include "notification_surface.h"

// One transient notification slot; updates use the same surface in the anchored slot below it.
void drawTerminalNotification(UiRenderer& target, UiBrush& brush, IDWriteFactory* factory, IDWriteTextFormat* heading,
    IDWriteTextFormat* font, VexChromePalette palette, float bottomInset, double now);
NotificationHit terminalNotificationPointer(float x, float y, bool click);
bool terminalNotificationKey(uint32_t key, uint32_t modifiers);
void dismissTerminalNotification();
void clearTerminalNotification();
bool terminalNotificationVisible();
bool terminalNotificationAnimationPending();
// Self-test only, gated by the window adapter. Creates ordinary production notifications.
void testTerminalNotification(uint32_t kind);
