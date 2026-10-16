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
    VexChromePalette palette;
    float tabWidth;
    uint32_t tabCount;
    int hoveredTab;
    bool hoveredClose;
};
struct WindowChromeDiagnostics {
    uint64_t activeTabIconKey, focusedPaneIconKey;
    size_t activeTabIconCount;
};
void beginWindowChromeFrame(const WindowChromeFrame& frame);
WindowChromeDiagnostics windowChromeDiagnostics();
