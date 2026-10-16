#pragma once
#include "windows_platform.h"

class WindowsRenderer;
struct ID2D1Factory;
struct IDWriteTextFormat;

// Window-thread-only borrowed renderer/fonts; cache invalidation remains the window owner's responsibility.
struct TerminalDrawingFrame {
    WindowsRenderer* renderer;
    ID2D1Factory* factory;
    IDWriteTextFormat* fonts[4];
    float dpiScale, cellWidth, cellHeight;
    uint32_t paneBackground;
};
void beginTerminalDrawingFrame(const TerminalDrawingFrame& frame);
void beginTerminalPane(VexPaneRect bounds);
