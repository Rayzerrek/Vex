#pragma once
#include "windows_platform.h"

class UiRenderer;
struct IDWriteTextFormat;
struct IDWriteFactory;

inline bool chromeContains(VexPaneRect rect, float x, float y) {
    return x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height;
}
inline VexPaneRect chromeTabRow(const VexTabStripLayout& strip, uint32_t index) {
    if (index >= strip.count || (!strip.horizontal && strip.sidebar_width == 0)) return {};
    auto row = strip.first_row;
    if (strip.horizontal) row.x += index * strip.row_pitch;
    else row.y += index * strip.row_pitch;
    return row;
}

// Borrowed window-thread paint values; formats and target must outlive the entire frame.
struct WindowChromeFrame {
    UiRenderer* target;
    IDWriteFactory* textFactory;
    IDWriteTextFormat* normalFont;
    IDWriteTextFormat* boldFont;
    IDWriteTextFormat* badgeFont;
    IDWriteTextFormat* paneTitleFont;
    IDWriteTextFormat* tooltipFont;
    VexChromePalette palette;
    VexUiMetrics uiMetrics;
    const VexTabStripLayout* tabs;
    const VexTabStripLayout* movingSidebar;
    float sidebarEdge, clientWidth, clientHeight;
    int hoveredTab;
    bool hoveredClose;
    double timeMs;
    bool motionEnabled;
    float pointerX, pointerY;
    bool tooltipsEnabled;
};
struct WindowChromeDiagnostics {
    uint64_t activeTabIconKey, focusedPaneIconKey;
    size_t activeTabIconCount;
    float tabSelectionX;
};
void beginWindowChromeFrame(const WindowChromeFrame& frame);
WindowChromeDiagnostics windowChromeDiagnostics();
VexPaneRect interactiveChromeTabRow(const VexTabStripLayout& layout, uint32_t index, float width);
// True only until a chrome transition settles; the window schedules refresh-paced wakes.
bool windowChromeAnimationPending();
bool windowChromeEffectsPending();
void offerWindowChromeShortcut(const wchar_t* text, VexPaneRect bounds);
void drawWindowChromeTooltip();
double windowChromeTooltipDeadline();
bool windowChromeTooltipPointer(float x, float y);
bool dismissWindowChromeTooltip();
