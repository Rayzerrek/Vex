#pragma once
#include "windows_platform.h"
#include "ui_renderer.h"

// Notification data is copied once; rendering shares the application's cached UI text.
void drawTerminalNotification(UiRenderer& target, UiBrush& brush, IDWriteTextFormat* heading,
                              IDWriteTextFormat* font, VexChromePalette palette);
bool dismissTerminalNotificationAt(float x, float y);
void clearTerminalNotification();
