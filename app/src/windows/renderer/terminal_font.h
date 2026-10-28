#pragma once
#include "windows_com.h"
#include <dwrite.h>
#include <d2d1.h>
#include <stdint.h>

HRESULT createTerminalFonts(IDWriteFactory* factory, const wchar_t* family, float size,
    IDWriteTextFormat* (&fonts)[4], float& cellWidth, float& cellHeight);

// ASCII substitution inputs from the font's enabled OpenType lookups.
// Candidate spans share the ordinary bounded glyph atlas; plain text retains its single-cell cache.
struct TerminalLigatureMap {
    bool characters[4][128] = {};
    HRESULT initialize(IDWriteFactory* factory, IDWriteTextFormat* const (&fonts)[4]);
    uint16_t before[4] = {}, input[4] = { 1, 1, 1, 1 }, after[4] = {};
    void next(const uint8_t* text, size_t length, uint32_t style, size_t& start, size_t& end) const;
};

// Ink bounds are separate from the terminal advance: italics and accents may overhang a cell.
class TerminalTextLayout {
public:
    HRESULT initialize(IDWriteFactory* factory, IDWriteTextFormat* font, const uint8_t* utf8,
        size_t length, float width, float height);
    D2D1_RECT_F bounds() const { return ink; }
    void draw(ID2D1RenderTarget* target, ID2D1SolidColorBrush* brush, float x, float y) const;
    void reset() { layout.Reset(); }
private:
    RendererComPtr<IDWriteTextLayout> layout;
    D2D1_RECT_F ink = {};
    float factor = 1, stretchX = 1, offsetX = 0, offsetY = 0;
};
