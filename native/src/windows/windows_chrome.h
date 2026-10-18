#pragma once
#include "windows_platform.h"

class UiRenderer;
struct IDWriteTextFormat;

// Borrowed window-thread paint values; formats and target must outlive the entire frame.
struct WindowChromeFrame {
    UiRenderer* target;
    IDWriteTextFormat* normalFont;
    IDWriteTextFormat* boldFont;
    IDWriteTextFormat* badgeFont;
    IDWriteTextFormat* paneTitleFont;
    VexChromePalette palette;
    float tabWidth;
    uint32_t tabCount;
    int hoveredTab;
    bool hoveredClose;
    double timeMs;
    bool motionEnabled;
};
struct WindowChromeDiagnostics {
    uint64_t activeTabIconKey, focusedPaneIconKey;
    size_t activeTabIconCount;
    float tabSelectionX;
};
void beginWindowChromeFrame(const WindowChromeFrame& frame);
WindowChromeDiagnostics windowChromeDiagnostics();
// True only until a chrome transition settles; the window schedules refresh-paced wakes.
bool windowChromeAnimationPending();
