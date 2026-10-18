#include "terminal_drawing.h"
#include "drawing_color.h"
#include "windows_renderer.h"
#include <math.h>

namespace {
WindowsRenderer* renderer;
ID2D1Factory* drawingFactory;
IDWriteTextFormat* terminalFonts[4];
ID2D1RenderTarget* glyphTarget;
ID2D1SolidColorBrush* glyphBrush;
float dpiScale, cellWidth, cellHeight, gridLeft, gridTop;
uint32_t paneBackground;
}
void beginTerminalDrawingFrame(const TerminalDrawingFrame& frame) {
    renderer = frame.renderer;
    drawingFactory = frame.factory;
    for (int i = 0; i < 4; ++i) terminalFonts[i] = frame.fonts[i];
    dpiScale = frame.dpiScale; cellWidth = frame.cellWidth; cellHeight = frame.cellHeight;
    paneBackground = frame.paneBackground;
}
void beginTerminalPane(VexPaneRect bounds) {
    gridLeft = bounds.x + 6; gridTop = bounds.y + 30;
    const float scale = dpiScale;
    const D3D12_RECT clip = {
        (LONG)fmaxf(0, roundf(gridLeft * scale)), (LONG)fmaxf(0, roundf(gridTop * scale)),
        (LONG)fminf((float)renderer->width(), fmaxf(gridLeft, bounds.x + bounds.width - 6) * scale),
        (LONG)fminf((float)renderer->height(), fmaxf(gridTop, bounds.y + bounds.height - 6) * scale)
    };
    renderer->beginTerminalPass(clip);
}

static float snapDip(float v) {
    return dpiScale > 0.0f ? roundf(v * dpiScale) / dpiScale : roundf(v);
}

static void rectBounds(float left, float top, float right, float bottom, uint32_t color) {
    if (right <= left || bottom <= top) return;
    glyphBrush->SetColor(rgb(color));
    glyphTarget->FillRectangle(D2D1::RectF(left, top, right, bottom), glyphBrush);
}

static void drawBoxArms(float left, float top, float right, float bottom, float cx, float cy, float lightT, float heavyT, float gap, uint8_t l, uint8_t r, uint8_t u, uint8_t d, uint32_t color) {
    const float halfL = lightT * 0.5f;
    const float halfH = heavyT * 0.5f;
    const float doubleSpan = lightT * 2.0f + gap;
    const float halfSpan = doubleSpan * 0.5f;
    const float halfGap = gap * 0.5f;
    const bool vDouble = (u == 3 || d == 3);
    const bool hDouble = (l == 3 || r == 3);

    if (l == 1 || l == 2) {
        const float ht = l == 2 ? halfH : halfL;
        const float endX = vDouble ? ((u == 3 && d == 3 && r != 3) ? cx - halfGap : cx + halfSpan) : (r == 0 ? cx + (u == 2 || d == 2 ? halfH : halfL) : cx);
        rectBounds(left, snapDip(cy - ht), snapDip(endX), snapDip(cy - ht) + (l == 2 ? heavyT : lightT), color);
    }
    if (r == 1 || r == 2) {
        const float ht = r == 2 ? halfH : halfL;
        const float startX = vDouble ? ((u == 3 && d == 3 && l != 3) ? cx + halfGap : cx - halfSpan) : (l == 0 ? cx - (u == 2 || d == 2 ? halfH : halfL) : cx);
        rectBounds(snapDip(startX), snapDip(cy - ht), right, snapDip(cy - ht) + (r == 2 ? heavyT : lightT), color);
    }
    if (u == 1 || u == 2) {
        const float ht = u == 2 ? halfH : halfL;
        const float endY = hDouble ? ((l == 3 && r == 3 && d != 3) ? cy - halfGap : cy + halfSpan) : (d == 0 ? cy + (l == 2 || r == 2 ? halfH : halfL) : cy);
        rectBounds(snapDip(cx - ht), top, snapDip(cx - ht) + (u == 2 ? heavyT : lightT), snapDip(endY), color);
    }
    if (d == 1 || d == 2) {
        const float ht = d == 2 ? halfH : halfL;
        const float startY = hDouble ? ((l == 3 && r == 3 && u != 3) ? cy + halfGap : cy - halfSpan) : (u == 0 ? cy - (l == 2 || r == 2 ? halfH : halfL) : cy);
        rectBounds(snapDip(cx - ht), snapDip(startY), snapDip(cx - ht) + (d == 2 ? heavyT : lightT), bottom, color);
    }

    const float topY = snapDip(cy - halfSpan);
    const float botY = snapDip(cy + halfGap);
    const float leftX = snapDip(cx - halfSpan);
    const float rightX = snapDip(cx + halfGap);

    if (l == 3) {
        const float topEnd = u == 3 ? leftX + lightT : (vDouble || u != 0 ? cx + halfSpan : cx);
        const float botEnd = d == 3 ? leftX + lightT : (vDouble || d != 0 ? cx + halfSpan : cx);
        rectBounds(left, topY, snapDip(topEnd), topY + lightT, color);
        rectBounds(left, botY, snapDip(botEnd), botY + lightT, color);
    }
    if (r == 3) {
        const float topStart = u == 3 ? rightX : (vDouble || u != 0 ? cx - halfSpan : cx);
        const float botStart = d == 3 ? rightX : (vDouble || d != 0 ? cx - halfSpan : cx);
        rectBounds(snapDip(topStart), topY, right, topY + lightT, color);
        rectBounds(snapDip(botStart), botY, right, botY + lightT, color);
    }
    if (u == 3) {
        const float leftEnd = l == 3 ? topY + lightT : (hDouble || l != 0 ? cy + halfSpan : cy);
        const float rightEnd = r == 3 ? topY + lightT : (hDouble || r != 0 ? cy + halfSpan : cy);
        rectBounds(leftX, top, leftX + lightT, snapDip(leftEnd), color);
        rectBounds(rightX, top, rightX + lightT, snapDip(rightEnd), color);
    }
    if (d == 3) {
        const float leftStart = l == 3 ? botY : (hDouble || l != 0 ? cy - halfSpan : cy);
        const float rightStart = r == 3 ? botY : (hDouble || r != 0 ? cy - halfSpan : cy);
        rectBounds(leftX, snapDip(leftStart), leftX + lightT, bottom, color);
        rectBounds(rightX, snapDip(rightStart), rightX + lightT, bottom, color);
    }
}

#define VEX_ARMS(l, r, u, d) ((uint8_t)((l) | ((r) << 2) | ((u) << 4) | ((d) << 6)))
static const uint8_t boxArmsTable[128] = {
    VEX_ARMS(1,1,0,0), VEX_ARMS(2,2,0,0), VEX_ARMS(0,0,1,1), VEX_ARMS(0,0,2,2), 0, 0, 0, 0, 0, 0, 0, 0, VEX_ARMS(0,1,0,1), VEX_ARMS(0,2,0,1), VEX_ARMS(0,1,0,2), VEX_ARMS(0,2,0,2),
    VEX_ARMS(1,0,0,1), VEX_ARMS(2,0,0,1), VEX_ARMS(1,0,0,2), VEX_ARMS(2,0,0,2), VEX_ARMS(0,1,1,0), VEX_ARMS(0,2,1,0), VEX_ARMS(0,1,2,0), VEX_ARMS(0,2,2,0), VEX_ARMS(1,0,1,0), VEX_ARMS(2,0,1,0), VEX_ARMS(1,0,2,0), VEX_ARMS(2,0,2,0), VEX_ARMS(0,1,1,1), VEX_ARMS(0,2,1,1), VEX_ARMS(0,1,2,1), VEX_ARMS(0,1,1,2),
    VEX_ARMS(0,1,2,2), VEX_ARMS(0,2,2,1), VEX_ARMS(0,2,1,2), VEX_ARMS(0,2,2,2), VEX_ARMS(1,0,1,1), VEX_ARMS(2,0,1,1), VEX_ARMS(1,0,2,1), VEX_ARMS(1,0,1,2), VEX_ARMS(1,0,2,2), VEX_ARMS(2,0,2,1), VEX_ARMS(2,0,1,2), VEX_ARMS(2,0,2,2), VEX_ARMS(1,1,0,1), VEX_ARMS(2,1,0,1), VEX_ARMS(1,2,0,1), VEX_ARMS(2,2,0,1),
    VEX_ARMS(1,1,0,2), VEX_ARMS(2,1,0,2), VEX_ARMS(1,2,0,2), VEX_ARMS(2,2,0,2), VEX_ARMS(1,1,1,0), VEX_ARMS(2,1,1,0), VEX_ARMS(1,2,1,0), VEX_ARMS(2,2,1,0), VEX_ARMS(1,1,2,0), VEX_ARMS(2,1,2,0), VEX_ARMS(1,2,2,0), VEX_ARMS(2,2,2,0), VEX_ARMS(1,1,1,1), VEX_ARMS(2,1,1,1), VEX_ARMS(1,2,1,1), VEX_ARMS(2,2,1,1),
    VEX_ARMS(1,1,2,1), VEX_ARMS(1,1,1,2), VEX_ARMS(1,1,2,2), VEX_ARMS(2,1,2,1), VEX_ARMS(1,2,2,1), VEX_ARMS(2,1,1,2), VEX_ARMS(1,2,1,2), VEX_ARMS(2,2,2,1), VEX_ARMS(2,2,1,2), VEX_ARMS(2,1,2,2), VEX_ARMS(1,2,2,2), VEX_ARMS(2,2,2,2), 0, 0, 0, 0,
    VEX_ARMS(3,3,0,0), VEX_ARMS(0,0,3,3), VEX_ARMS(0,3,0,1), VEX_ARMS(0,1,0,3), VEX_ARMS(0,3,0,3), VEX_ARMS(3,0,0,1), VEX_ARMS(1,0,0,3), VEX_ARMS(3,0,0,3), VEX_ARMS(0,3,1,0), VEX_ARMS(0,1,3,0), VEX_ARMS(0,3,3,0), VEX_ARMS(3,0,1,0), VEX_ARMS(1,0,3,0), VEX_ARMS(3,0,3,0), VEX_ARMS(0,3,1,1), VEX_ARMS(0,1,3,3),
    VEX_ARMS(0,3,3,3), VEX_ARMS(3,0,1,1), VEX_ARMS(1,0,3,3), VEX_ARMS(3,0,3,3), VEX_ARMS(3,3,0,1), VEX_ARMS(1,1,0,3), VEX_ARMS(3,3,0,3), VEX_ARMS(3,3,1,0), VEX_ARMS(1,1,3,0), VEX_ARMS(3,3,3,0), VEX_ARMS(3,3,1,1), VEX_ARMS(1,1,3,3), VEX_ARMS(3,3,3,3), 0, 0, 0,
    0, 0, 0, 0, VEX_ARMS(1,0,0,0), VEX_ARMS(0,0,1,0), VEX_ARMS(0,1,0,0), VEX_ARMS(0,0,0,1), VEX_ARMS(2,0,0,0), VEX_ARMS(0,0,2,0), VEX_ARMS(0,2,0,0), VEX_ARMS(0,0,0,2), VEX_ARMS(1,2,0,0), VEX_ARMS(0,0,1,2), VEX_ARMS(2,1,0,0), VEX_ARMS(0,0,2,1)
};
#undef VEX_ARMS

static bool drawCustomGlyph(uint32_t cp, float x, float y, float w, float h, uint32_t foreground) {
    const float left = snapDip(x);
    const float top = snapDip(y);
    const float right = snapDip(x + w);
    const float bottom = snapDip(y + h);
    const float cw = right - left;
    const float ch = bottom - top;
    if (cw <= 0.0f || ch <= 0.0f) return false;
    const float px = dpiScale > 0.0f ? (1.0f / dpiScale) : 1.0f;
    const float lightT = fmaxf(px, roundf(dpiScale) * px);
    const float heavyT = fmaxf(lightT + px, roundf(2.0f * dpiScale) * px);

    if (cp >= 0x2580 && cp <= 0x259F) {
        if (cp == 0x2580) {
            rectBounds(left, top, right, snapDip(top + ch * 0.5f), foreground);
        } else if (cp >= 0x2581 && cp <= 0x2587) {
            const float frac = (float)(cp - 0x2580) / 8.0f;
            rectBounds(left, snapDip(bottom - ch * frac), right, bottom, foreground);
        } else if (cp == 0x2588) {
            rectBounds(left, top, right, bottom, foreground);
        } else if (cp >= 0x2589 && cp <= 0x258F) {
            const float frac = (float)(0x2590 - cp) / 8.0f;
            rectBounds(left, top, snapDip(left + cw * frac), bottom, foreground);
        } else if (cp == 0x2590) {
            rectBounds(snapDip(left + cw * 0.5f), top, right, bottom, foreground);
        } else if (cp >= 0x2591 && cp <= 0x2593) {
            const float alpha = cp == 0x2591 ? 0.25f : (cp == 0x2592 ? 0.50f : 0.75f);
            glyphBrush->SetColor(D2D1::ColorF((foreground >> 16) / 255.0f, ((foreground >> 8) & 255) / 255.0f, (foreground & 255) / 255.0f, alpha));
            glyphTarget->FillRectangle(D2D1::RectF(left, top, right, bottom), glyphBrush);
        } else if (cp == 0x2594) {
            rectBounds(left, top, right, snapDip(top + ch * 0.125f), foreground);
        } else if (cp == 0x2595) {
            rectBounds(snapDip(right - cw * 0.125f), top, right, bottom, foreground);
        } else {
            static const uint8_t quadMask[10] = { 4, 8, 1, 13, 9, 7, 11, 2, 6, 14 };
            const uint8_t m = quadMask[cp - 0x2596];
            const float midX = snapDip(left + cw * 0.5f);
            const float midY = snapDip(top + ch * 0.5f);
            if (m & 1) rectBounds(left, top, midX, midY, foreground);
            if (m & 2) rectBounds(midX, top, right, midY, foreground);
            if (m & 4) rectBounds(left, midY, midX, bottom, foreground);
            if (m & 8) rectBounds(midX, midY, right, bottom, foreground);
        }
        return true;
    }

    if (cp >= 0x2500 && cp <= 0x257F) {
        const float cx = snapDip(left + cw * 0.5f);
        const float cy = snapDip(top + ch * 0.5f);
        if ((cp >= 0x2504 && cp <= 0x250B) || (cp >= 0x254C && cp <= 0x254F)) {
            const bool vertical = (cp >= 0x2506 && cp <= 0x250B) || cp == 0x254E || cp == 0x254F;
            const bool heavy = (cp & 1) != 0;
            const int count = (cp >= 0x254C) ? 2 : ((cp == 0x2504 || cp == 0x2505 || cp == 0x2506 || cp == 0x2507) ? 3 : 4);
            const float t = heavy ? heavyT : lightT;
            const float span = vertical ? ch : cw;
            const float step = span / (float)count;
            const float dashLen = fmaxf(px, step * 0.65f);
            for (int i = 0; i < count; i++) {
                const float start = (float)i * step + (step - dashLen) * 0.5f;
                if (vertical) {
                    const float rx = snapDip(cx - t * 0.5f);
                    const float ry = snapDip(top + start);
                    rectBounds(rx, ry, rx + t, snapDip(ry + dashLen), foreground);
                } else {
                    const float rx = snapDip(left + start);
                    const float ry = snapDip(cy - t * 0.5f);
                    rectBounds(rx, ry, snapDip(rx + dashLen), ry + t, foreground);
                }
            }
            return true;
        }
        if (cp >= 0x256D && cp <= 0x2570) {
            RendererComPtr<ID2D1PathGeometry> path;
            RendererComPtr<ID2D1GeometrySink> sink;
            if (drawingFactory && SUCCEEDED(drawingFactory->CreatePathGeometry(&path)) && path && SUCCEEDED(path->Open(&sink)) && sink) {
                const float r = fminf(cw, ch) * 0.45f;
                if (cp == 0x256D) {
                    sink->BeginFigure(D2D1::Point2F(right, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx + r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy + r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, bottom));
                } else if (cp == 0x256E) {
                    sink->BeginFigure(D2D1::Point2F(left, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx - r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy + r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, bottom));
                } else if (cp == 0x256F) {
                    sink->BeginFigure(D2D1::Point2F(left, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx - r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy - r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, top));
                } else {
                    sink->BeginFigure(D2D1::Point2F(right, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx + r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy - r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, top));
                }
                sink->EndFigure(D2D1_FIGURE_END_OPEN);
                sink->Close();
                glyphBrush->SetColor(rgb(foreground));
                glyphTarget->DrawGeometry(path.Get(), glyphBrush, lightT);
            }
            return true;
        }
        if (cp >= 0x2571 && cp <= 0x2573) {
            glyphBrush->SetColor(rgb(foreground));
            if (cp == 0x2571 || cp == 0x2573) glyphTarget->DrawLine(D2D1::Point2F(right, top), D2D1::Point2F(left, bottom), glyphBrush, lightT);
            if (cp == 0x2572 || cp == 0x2573) glyphTarget->DrawLine(D2D1::Point2F(left, top), D2D1::Point2F(right, bottom), glyphBrush, lightT);
            return true;
        }
        const uint8_t packed = boxArmsTable[cp - 0x2500];
        if (packed != 0) {
            drawBoxArms(left, top, right, bottom, cx, cy, lightT, heavyT, lightT, packed & 3, (packed >> 2) & 3, (packed >> 4) & 3, (packed >> 6) & 3, foreground);
            return true;
        }
        return false;
    }

    if (cp >= 0xE0B0 && cp <= 0xE0B7) {
        const float cy = top + ch * 0.5f;
        glyphBrush->SetColor(rgb(foreground));
        if (cp == 0xE0B1) {
            glyphTarget->DrawLine(D2D1::Point2F(left, top), D2D1::Point2F(right, cy), glyphBrush, lightT);
            glyphTarget->DrawLine(D2D1::Point2F(right, cy), D2D1::Point2F(left, bottom), glyphBrush, lightT);
            return true;
        }
        if (cp == 0xE0B3) {
            glyphTarget->DrawLine(D2D1::Point2F(right, top), D2D1::Point2F(left, cy), glyphBrush, lightT);
            glyphTarget->DrawLine(D2D1::Point2F(left, cy), D2D1::Point2F(right, bottom), glyphBrush, lightT);
            return true;
        }
        RendererComPtr<ID2D1PathGeometry> path;
        RendererComPtr<ID2D1GeometrySink> sink;
        if (drawingFactory && SUCCEEDED(drawingFactory->CreatePathGeometry(&path)) && path && SUCCEEDED(path->Open(&sink)) && sink) {
            const bool filled = (cp == 0xE0B0 || cp == 0xE0B2 || cp == 0xE0B4 || cp == 0xE0B6);
            if (cp == 0xE0B0) {
                sink->BeginFigure(D2D1::Point2F(left, top), D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(D2D1::Point2F(right, cy));
                sink->AddLine(D2D1::Point2F(left, bottom));
                sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            } else if (cp == 0xE0B2) {
                sink->BeginFigure(D2D1::Point2F(right, top), D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(D2D1::Point2F(left, cy));
                sink->AddLine(D2D1::Point2F(right, bottom));
                sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            } else if (cp == 0xE0B4 || cp == 0xE0B5) {
                sink->BeginFigure(D2D1::Point2F(left, top), filled ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
                sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(left, bottom), D2D1::SizeF(cw, ch * 0.5f), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                sink->EndFigure(filled ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            } else {
                sink->BeginFigure(D2D1::Point2F(right, top), filled ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
                sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right, bottom), D2D1::SizeF(cw, ch * 0.5f), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                sink->EndFigure(filled ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            }
            sink->Close();
            if (filled) glyphTarget->FillGeometry(path.Get(), glyphBrush);
            else glyphTarget->DrawGeometry(path.Get(), glyphBrush, lightT);
        }
        return true;
    }

    if (cp >= 0x2800 && cp <= 0x28FF) {
        const uint8_t mask = (uint8_t)(cp - 0x2800);
        if (mask == 0) return true;
        const float dotW = fmaxf(px, snapDip(cw * 0.22f));
        const float dotH = fmaxf(px, snapDip(ch * 0.14f));
        const float colX[2] = { snapDip(left + cw * 0.28f - dotW * 0.5f), snapDip(left + cw * 0.72f - dotW * 0.5f) };
        const float rowY[4] = {
            snapDip(top + ch * 0.15f - dotH * 0.5f),
            snapDip(top + ch * 0.38f - dotH * 0.5f),
            snapDip(top + ch * 0.62f - dotH * 0.5f),
            snapDip(top + ch * 0.85f - dotH * 0.5f),
        };
        static const uint8_t bitCol[8] = { 0, 0, 0, 1, 1, 1, 0, 1 };
        static const uint8_t bitRow[8] = { 0, 1, 2, 0, 1, 2, 3, 3 };
        for (int b = 0; b < 8; b++) {
            if (mask & (1 << b)) {
                rectBounds(colX[bitCol[b]], rowY[bitRow[b]], colX[bitCol[b]] + dotW, rowY[bitRow[b]] + dotH, foreground);
            }
        }
        return true;
    }

    return false;
}

static HRESULT paintAtlasGlyph(ID2D1RenderTarget* raster, ID2D1SolidColorBrush* rasterBrush, const uint8_t* utf8, size_t length, uint32_t flags, float x, float y, float width, float height) {
    glyphTarget = raster; glyphBrush = rasterBrush;
    bool custom = false;
    if (length == 3 && (utf8[0] & 0xf0) == 0xe0) {
        const uint32_t cp = ((uint32_t)(utf8[0] & 15) << 12) | ((uint32_t)(utf8[1] & 63) << 6) | (utf8[2] & 63);
        custom = drawCustomGlyph(cp, x, y, width, height, 0xffffff);
    }
    glyphTarget = nullptr; glyphBrush = nullptr;
    if (custom) return S_OK;
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)utf8, (int)length, nullptr, 0);
    if (count <= 0) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t scratch[4096];
    auto text = count <= 4096 ? scratch : static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, (size_t)count * sizeof(wchar_t)));
    if (!text) return E_OUTOFMEMORY;
    HRESULT result = S_OK;
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)utf8, (int)length, text, count)) result = HRESULT_FROM_WIN32(GetLastError());
    else {
        rasterBrush->SetColor(D2D1::ColorF(1, 1, 1, 1));
        raster->DrawText(text, count, terminalFonts[flags & 3], D2D1::RectF(x, y, x + width, y + height), rasterBrush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }
    if (text != scratch) HeapFree(GetProcessHeap(), 0, text);
    return result;
}
extern "C" int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags) {
    const float x = gridLeft + column * cellWidth, y = gridTop + row * cellHeight, width = cells * cellWidth;
    HRESULT result = S_OK;
    if (background != paneBackground) result = renderer->terminal.drawSolid(snapDip(x), snapDip(y), snapDip(x + width), snapDip(y + cellHeight), background);
    if (!length || (flags & 16) || FAILED(result)) return (int)renderer->terminalResult(result);
    if (!utf8 || length > 65536) return (int)renderer->terminalResult(E_INVALIDARG);
    size_t trimmed = length;
    while (trimmed && utf8[trimmed - 1] == ' ') trimmed--;
    bool ascii = length == cells;
    for (size_t i = 0; ascii && i < trimmed; i++) ascii = utf8[i] >= 32 && utf8[i] <= 126;
    if (ascii) {
        for (size_t i = 0; i < trimmed && SUCCEEDED(result); i++) {
            if (utf8[i] != ' ') result = renderer->terminal.drawGlyph(x + (float)i * cellWidth, y, cellWidth, cellHeight, utf8 + i, 1, flags, foreground, paintAtlasGlyph);
        }
    } else if (trimmed) result = renderer->terminal.drawGlyph(x, y, width, cellHeight, utf8, trimmed, flags, foreground, paintAtlasGlyph);
    if (SUCCEEDED(result) && (flags & 4)) result = renderer->terminal.drawSolid(x, y + cellHeight - 2, x + width, y + cellHeight - 1, foreground);
    if (SUCCEEDED(result) && (flags & 8)) result = renderer->terminal.drawSolid(x, y + cellHeight * 0.55f, x + width, y + cellHeight * 0.55f + 1, foreground);
    return (int)renderer->terminalResult(result);
}
extern "C" void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color) {
    const float x = gridLeft + column * cellWidth, y = gridTop + row * cellHeight;
    HRESULT result;
    if (style == 0) result = renderer->terminal.drawSolid(x, y, x + 1.5f, y + cellHeight, color);
    else if (style == 1) result = renderer->terminal.drawSolid(snapDip(x), snapDip(y), snapDip(x + cellWidth), snapDip(y + cellHeight), color);
    else if (style == 2) result = renderer->terminal.drawSolid(x, y + cellHeight - 2, x + cellWidth, y + cellHeight, color);
    else {
        result = renderer->terminal.drawSolid(x, y, x + cellWidth, y + 1, color);
        if (SUCCEEDED(result)) result = renderer->terminal.drawSolid(x, y + cellHeight - 1, x + cellWidth, y + cellHeight, color);
        if (SUCCEEDED(result)) result = renderer->terminal.drawSolid(x, y + 1, x + 1, y + cellHeight - 1, color);
        if (SUCCEEDED(result)) result = renderer->terminal.drawSolid(x + cellWidth - 1, y + 1, x + cellWidth, y + cellHeight - 1, color);
    }
    renderer->terminalResult(result);
}
extern "C" int vex_terminal_geometry_begin(VexTerminalGeometryKey key, bool* reused) {
    if (!reused) return (int)renderer->terminalResult(E_INVALIDARG);
    const uint32_t scalars[] = {
        key.pane_id, key.columns, key.rows, key.background, key.foreground, key.cursor,
        key.selection_background, key.accent, key.is_dark,
        key.link_row, key.link_start, key.link_end
    };
    const float geometry[] = { gridLeft, gridTop, cellWidth, cellHeight };
    return (int)renderer->terminalResult(renderer->terminal.beginGeometry(key.generation, scalars, geometry, *reused));
}
extern "C" void vex_terminal_geometry_end(bool complete) { renderer->terminal.endGeometry(complete); }
extern "C" void vex_end_pane() { renderer->endTerminalPass(); }
