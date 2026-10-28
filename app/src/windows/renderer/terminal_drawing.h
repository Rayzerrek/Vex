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
    VexUiMetrics uiMetrics;
};
void beginTerminalDrawingFrame(const TerminalDrawingFrame& frame);
void clearTerminalDrawingFontCache();
void beginTerminalPane(VexPaneRect bounds, bool moving = false);
