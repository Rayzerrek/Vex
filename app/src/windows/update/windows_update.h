#pragma once
#include "windows_platform.h"
#include "notification_surface.h"
#include <windows.h>

// Updater owns its worker and all snapshots; only window-thread calls touch UI state.
void initializeWindowsUpdates(HWND window, bool selftest);
void shutdownWindowsUpdates();
// True only on the transition to a preflight-acknowledged helper, before the live-process close review.
bool refreshWindowsUpdate();
void checkWindowsUpdatesAutomatically();
NotificationHit windowsUpdatePointer(float x, float y, bool click);
bool windowsUpdateKey(uint32_t key, uint32_t modifiers);
// Called only behind the window adapter's VEX_NATIVE_SELFTEST gate; never enables downloads/installations.
uint32_t testWindowsUpdate(uint32_t command);
float drawWindowsUpdate(UiRenderer& target, UiBrush& brush, IDWriteFactory* factory, IDWriteTextFormat* font, VexChromePalette palette, double now);
bool windowsUpdateAnimationPending();
// Dedicated posted message/timers avoid polling or keeping an idle animation clock alive.
inline constexpr UINT windowsUpdateMessage = WM_APP + 11;
inline constexpr UINT_PTR windowsUpdateTimer = 7;
