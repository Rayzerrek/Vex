#pragma once
#include <windows.h>

// Both print and diagnostic capture read back the actual rendered scene.
using WindowCaptureRender = bool (*)(void* pixels, UINT stride);
void drawCapturedWindow(HWND window, HDC dc, WindowCaptureRender render);
bool saveWindowCaptureBmp(HWND window, WindowCaptureRender render);
