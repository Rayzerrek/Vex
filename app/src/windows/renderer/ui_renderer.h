#pragma once
#include "terminal_renderer.h"
#include <dwrite.h>

// Value-only UI paint; never owns a device brush or a translated drawing context.
struct UiBrush {
    D2D1_COLOR_F color = { 1, 1, 1, 1 };
    void SetColor(D2D1_COLOR_F value) { color = value; }
};

// Window-thread native UI batches. Only text-cache misses use a software raster target.
class UiRenderer final {
public:
    TerminalRenderer batches;
    void resetFrame(UINT width, UINT height, float dpi);
    HRESULT result() const { return error; }
    D2D1_SIZE_F GetSize() const;
    void Clear(D2D1_COLOR_F color);
    void FillRectangle(D2D1_RECT_F rect, const UiBrush* brush);
    void FillRoundedRectangle(D2D1_ROUNDED_RECT rect, const UiBrush* brush);
    void DrawRoundedRectangle(D2D1_ROUNDED_RECT rect, const UiBrush* brush, float stroke);
    void DrawLine(D2D1_POINT_2F from, D2D1_POINT_2F to, const UiBrush* brush, float stroke, bool roundCaps = false);
    void FillEllipse(D2D1_ELLIPSE ellipse, const UiBrush* brush);
    void DrawEllipse(D2D1_ELLIPSE ellipse, const UiBrush* brush, float stroke);
    // Brand paths are cached as DPI-specific masks; unknown marks use explicit monograms.
    void DrawAppIcon(uint8_t glyph, IDWriteTextFormat* badgeFont, D2D1_RECT_F rect, const UiBrush* brush);
    void DrawText(const wchar_t* text, UINT32 length, IDWriteTextFormat* font, D2D1_RECT_F rect, const UiBrush* brush, D2D1_DRAW_TEXT_OPTIONS options);
    void PushAxisAlignedClip(D2D1_RECT_F rect, D2D1_ANTIALIAS_MODE mode);
    void PopAxisAlignedClip();
    HRESULT restoreClip();
private:
    UINT pixelWidth = 0, pixelHeight = 0, clipDepth = 0;
    float scale = 1;
    D3D12_RECT clips[16] = {};
    HRESULT error = S_OK;
    void record(HRESULT result);
    static uint32_t colorRgb(D2D1_COLOR_F color);
    static uint8_t colorAlpha(D2D1_COLOR_F color);
};
