#pragma once
#include <windows.h>

inline bool windowCanRender(HWND window, bool preparingFirstFrame, bool cloaked = false) {
    if (!IsWindow(window) || IsIconic(window)) return false;
    // Startup prepares its surface while hidden/cloaked to avoid exposing an empty frame.
    if (preparingFirstFrame) return true;
    return IsWindowVisible(window) && !cloaked;
}
