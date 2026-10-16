#include "ui_renderer.h"
#include <math.h>
#include <string.h>

namespace {
constexpr UINT maxTextUnits = 4096;
struct TextMaskKey {
    IDWriteTextFormat* font;
    float width, height, tileX, tileY;
    UINT units;
};

HRESULT paintUiText(ID2D1RenderTarget* target, ID2D1SolidColorBrush* brush, const uint8_t* bytes, size_t length, uint32_t, float x, float y, float, float) {
    if (length < sizeof(TextMaskKey)) return E_INVALIDARG;
    TextMaskKey key;
    memcpy(&key, bytes, sizeof(key));
    if (!key.font || key.units > maxTextUnits || length != sizeof(key) + key.units * sizeof(wchar_t)) return E_INVALIDARG;
    wchar_t text[maxTextUnits];
    memcpy(text, bytes + sizeof(key), key.units * sizeof(wchar_t));
    brush->SetColor(D2D1::ColorF(1, 1, 1, 1));
    target->DrawText(text, key.units, key.font,
        D2D1::RectF(x - key.tileX, y - key.tileY, x - key.tileX + key.width, y - key.tileY + key.height),
        brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    return S_OK;
}
}

void UiRenderer::record(HRESULT value) {
    if (FAILED(value) && SUCCEEDED(error)) error = value;
}

uint32_t UiRenderer::colorRgb(D2D1_COLOR_F color) {
    const auto channel = [](float value) -> uint32_t { return (uint32_t)lroundf(fminf(1, fmaxf(0, value)) * 255); };
    return (channel(color.r) << 16) | (channel(color.g) << 8) | channel(color.b);
}

uint8_t UiRenderer::colorAlpha(D2D1_COLOR_F color) {
    return (uint8_t)lroundf(fminf(1, fmaxf(0, color.a)) * 255);
}

void UiRenderer::resetFrame(UINT width, UINT height, float dpi) {
    pixelWidth = width; pixelHeight = height; scale = dpi / 96.0f;
    clipDepth = 0; clips[0] = { 0, 0, (LONG)width, (LONG)height }; error = S_OK;
}

D2D1_SIZE_F UiRenderer::GetSize() const { return D2D1::SizeF(pixelWidth / scale, pixelHeight / scale); }

void UiRenderer::Clear(D2D1_COLOR_F color) {
    record(batches.drawSolid(0, 0, pixelWidth / scale, pixelHeight / scale, colorRgb(color), colorAlpha(color)));
}

void UiRenderer::FillRectangle(D2D1_RECT_F rect, const UiBrush* brush) {
    record(batches.drawSolid(rect.left, rect.top, rect.right, rect.bottom, colorRgb(brush->color), colorAlpha(brush->color)));
}

void UiRenderer::FillRoundedRectangle(D2D1_ROUNDED_RECT rect, const UiBrush* brush) {
    const float r = fminf(rect.radiusX, rect.radiusY);
    if (r <= 0.0f) {
        record(batches.drawSolid(rect.rect.left, rect.rect.top, rect.rect.right, rect.rect.bottom, colorRgb(brush->color), colorAlpha(brush->color)));
        return;
    }
    record(batches.drawRounded(rect.rect.left, rect.rect.top, rect.rect.right, rect.rect.bottom, r, 0, colorRgb(brush->color), colorAlpha(brush->color)));
}

void UiRenderer::DrawRoundedRectangle(D2D1_ROUNDED_RECT rect, const UiBrush* brush, float stroke) {
    record(batches.drawRounded(rect.rect.left, rect.rect.top, rect.rect.right, rect.rect.bottom, fminf(rect.radiusX, rect.radiusY), stroke, colorRgb(brush->color), colorAlpha(brush->color)));
}

void UiRenderer::DrawLine(D2D1_POINT_2F from, D2D1_POINT_2F to, const UiBrush* brush, float stroke, bool roundCaps) {
    record(batches.drawLine(from.x, from.y, to.x, to.y, stroke, roundCaps, colorRgb(brush->color), colorAlpha(brush->color)));
}

void UiRenderer::FillEllipse(D2D1_ELLIPSE ellipse, const UiBrush* brush) {
    record(batches.drawEllipse(ellipse.point.x, ellipse.point.y, ellipse.radiusX, ellipse.radiusY, 0, colorRgb(brush->color), colorAlpha(brush->color)));
}

void UiRenderer::DrawEllipse(D2D1_ELLIPSE ellipse, const UiBrush* brush, float stroke) {
    record(batches.drawEllipse(ellipse.point.x, ellipse.point.y, ellipse.radiusX, ellipse.radiusY, stroke, colorRgb(brush->color), colorAlpha(brush->color)));
}

HRESULT UiRenderer::restoreClip() { return batches.setClip(clips[clipDepth]); }

void UiRenderer::PushAxisAlignedClip(D2D1_RECT_F rect, D2D1_ANTIALIAS_MODE) {
    if (clipDepth + 1 >= 16 || !isfinite(rect.left) || !isfinite(rect.top) || !isfinite(rect.right) || !isfinite(rect.bottom)) { record(E_INVALIDARG); return; }
    const auto parent = clips[clipDepth];
    const auto bound = [](float value, LONG minimum, LONG maximum) -> LONG {
        return (LONG)fmaxf((float)minimum, fminf((float)maximum, value));
    };
    D3D12_RECT clip = {
        bound(floorf(rect.left * scale), parent.left, parent.right),
        bound(floorf(rect.top * scale), parent.top, parent.bottom),
        bound(ceilf(rect.right * scale), parent.left, parent.right),
        bound(ceilf(rect.bottom * scale), parent.top, parent.bottom),
    };
    if (clip.right < clip.left) clip.right = clip.left;
    if (clip.bottom < clip.top) clip.bottom = clip.top;
    clips[++clipDepth] = clip;
    record(restoreClip());
}

void UiRenderer::PopAxisAlignedClip() {
    if (!clipDepth) { record(E_UNEXPECTED); return; }
    clipDepth--;
    record(restoreClip());
}

void UiRenderer::DrawText(const wchar_t* text, UINT32 length, IDWriteTextFormat* font, D2D1_RECT_F rect, const UiBrush* brush, D2D1_DRAW_TEXT_OPTIONS) {
    if (FAILED(error) || !length) return;
    if (!text || !font || length > maxTextUnits || !isfinite(rect.left) || !isfinite(rect.top) || !isfinite(rect.right) || !isfinite(rect.bottom)) { record(E_INVALIDARG); return; }
    const float width = rect.right - rect.left, height = rect.bottom - rect.top;
    if (width <= 0 || height <= 0) return;
    PushAxisAlignedClip(rect, D2D1_ANTIALIAS_MODE_ALIASED);
    if (FAILED(error)) return;
    uint8_t bytes[sizeof(TextMaskKey) + maxTextUnits * sizeof(wchar_t)];
    TextMaskKey key;
    memset(&key, 0, sizeof(key));
    key.font = font; key.width = width; key.height = height; key.units = length;
    memcpy(bytes + sizeof(key), text, length * sizeof(wchar_t));
    // Each tile fits the shared bounded raster scratch even at high DPI; layout/trimming uses the full label.
    const float tileSize = 510.0f / scale;
    const auto clip = clips[clipDepth];
    for (float y = 0; y < height && SUCCEEDED(error); y += tileSize) {
        if ((rect.top + y) * scale > clip.bottom || (rect.top + y + tileSize) * scale < clip.top) continue;
        for (float x = 0; x < width && SUCCEEDED(error); x += tileSize) {
            if ((rect.left + x) * scale > clip.right || (rect.left + x + tileSize) * scale < clip.left) continue;
            key.tileX = x; key.tileY = y;
            memcpy(bytes, &key, sizeof(key));
            const float tileWidth = fminf(tileSize, width - x), tileHeight = fminf(tileSize, height - y);
            // Adjacent tiles share the same integer boundary: their one-pixel raster padding must not blend twice.
            D3D12_RECT tileClip = {
                x == 0 ? clip.left : (LONG)fmaxf((float)clip.left, ceilf((rect.left + x) * scale)),
                y == 0 ? clip.top : (LONG)fmaxf((float)clip.top, ceilf((rect.top + y) * scale)),
                (LONG)fminf((float)clip.right, ceilf((rect.left + x + tileWidth) * scale)),
                (LONG)fminf((float)clip.bottom, ceilf((rect.top + y + tileHeight) * scale)),
            };
            if (tileClip.left >= tileClip.right || tileClip.top >= tileClip.bottom) continue;
            record(batches.setClip(tileClip));
            record(batches.drawGlyph(rect.left + x, rect.top + y, tileWidth, tileHeight,
                bytes, sizeof(key) + length * sizeof(wchar_t), 0, colorRgb(brush->color), paintUiText, colorAlpha(brush->color)));
        }
    }
    PopAxisAlignedClip();
}
