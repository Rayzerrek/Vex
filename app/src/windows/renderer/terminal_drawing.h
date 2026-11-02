#pragma once
#include "windows_platform.h"

class WindowsRenderer;
struct ID2D1Factory;
struct IDWriteTextFormat;
struct IDWriteFactory;
struct TerminalLigatureMap;

// Window-thread-only borrowed renderer/fonts; cache invalidation remains the window owner's responsibility.
struct TerminalDrawingFrame {
    WindowsRenderer* renderer;
    ID2D1Factory* factory;
    IDWriteFactory* textFactory;
    IDWriteTextFormat* fonts[4];
    const TerminalLigatureMap* ligatures;
    float dpiScale, cellWidth, cellHeight;
    uint32_t paneBackground;
};
void beginTerminalDrawingFrame(const TerminalDrawingFrame& frame);
void clearTerminalDrawingFontCache();
/* edgeInset keeps the border and focus ring the chrome drew; 0 disables edge extension. */
void beginTerminalPane(VexPaneRect bounds, VexPaneRect grid, bool moving = false, float contentTop = 0, float edgeInset = 0);
