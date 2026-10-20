#pragma once
#include "windows_platform.h"
#include <windows.h>

// One owned dialog at a time; background-tab close popovers leave the owner enabled.
// Controls own copied text; the terminal retains its normal IO/message pump.
void initializeTerminalDialogs(HWND owner, VexWindowCallbacks callbacks);
bool routeTerminalDialogMessage(MSG& message);
void destroyTerminalDialogs();
void repositionTerminalDialogs();
HWND terminalDialogWindow();
class UiRenderer;
struct UiBrush;
void drawTerminalDialogBackdrop(UiRenderer& target, UiBrush& brush, float scale);
