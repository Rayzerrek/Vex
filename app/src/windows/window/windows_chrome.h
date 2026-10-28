#pragma once
#include "windows_platform.h"

class UiRenderer;
struct IDWriteTextFormat;
struct IDWriteFactory;

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
    float tabWidth;
    uint32_t tabCount;
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
// True only until a chrome transition settles; the window schedules refresh-paced wakes.
bool windowChromeAnimationPending();
bool windowChromeEffectsPending();
void offerWindowChromeShortcut(const wchar_t* text, VexPaneRect bounds);
void drawWindowChromeTooltip();
double windowChromeTooltipDeadline();
bool windowChromeTooltipPointer(float x, float y);
bool dismissWindowChromeTooltip();
