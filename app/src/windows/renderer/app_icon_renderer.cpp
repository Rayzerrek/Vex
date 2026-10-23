#include "ui_renderer.h"
#include "app_icon_geometry.generated.h"
#include "drawing_color.h"
#include "icon_contrast.h"
#include "pi_icon.h"
#include <string.h>

namespace {
// A distinct binary prefix keeps icon masks separate from text in the shared atlas.
struct AppIconMaskKey { uint8_t tag[4]; uint8_t glyph; };

HRESULT paintAppIcon(ID2D1RenderTarget* target, ID2D1SolidColorBrush* brush, const uint8_t* bytes, size_t length, uint32_t, float x, float y, float width, float height) {
    if (length != sizeof(AppIconMaskKey)) return E_INVALIDARG;
    AppIconMaskKey key;
    memcpy(&key, bytes, sizeof(key));
    if (key.glyph >= sizeof(appIconGeometries) / sizeof(appIconGeometries[0])) return E_INVALIDARG;
    const auto& icon = appIconGeometries[key.glyph];
    RendererComPtr<ID2D1Factory> factory;
    target->GetFactory(&factory);
    RendererComPtr<ID2D1PathGeometry> geometry;
    HRESULT result = factory->CreatePathGeometry(&geometry);
    if (FAILED(result)) return result;
    RendererComPtr<ID2D1GeometrySink> sink;
    result = geometry->Open(&sink);
    if (FAILED(result)) return result;
    sink->SetFillMode(icon.fill);
    const auto point = [&](D2D1_POINT_2F p) { return D2D1::Point2F(x + p.x * width / 24.0f, y + p.y * height / 24.0f); };
    for (uint32_t i = 0; i < icon.figureCount; ++i) {
        const auto& figure = icon.figures[i];
        sink->BeginFigure(point(icon.points[figure.offset]), D2D1_FIGURE_BEGIN_FILLED);
        for (uint32_t j = 1; j < figure.count; ++j) sink->AddLine(point(icon.points[figure.offset + j]));
        sink->EndFigure(figure.closed ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
    }
    result = sink->Close();
    if (FAILED(result)) return result;
    brush->SetColor(D2D1::ColorF(1, 1, 1, 1));
    target->FillGeometry(geometry.Get(), brush);
    return S_OK;
}
}

void UiRenderer::DrawAppIcon(uint8_t glyph, IDWriteTextFormat* badgeFont, D2D1_RECT_F rect, const UiBrush* brush, uint32_t background) {
    if (glyph == static_cast<uint8_t>(AppIconGlyph::pi)) {
        const float sx = (rect.right - rect.left) / 24.0f, sy = (rect.bottom - rect.top) / 24.0f;
        UiBrush ink;
        for (const auto& block : piIconBlocks) {
            ink.SetColor(rgb(contrastingIconColor(block.color, background), colorAlpha(brush->color)));
            FillRectangle(D2D1::RectF(rect.left + block.left * sx, rect.top + block.top * sy,
                rect.left + block.right * sx, rect.top + block.bottom * sy), &ink);
        }
        return;
    }
    if (glyph >= sizeof(appIconGeometries) / sizeof(appIconGeometries[0])) { record(E_INVALIDARG); return; }
    const auto& icon = appIconGeometries[glyph];
    if (icon.figureCount == 0) {
        if (!badgeFont) { record(E_INVALIDARG); return; }
        wchar_t text[2] = {};
        const UINT count = (UINT)strlen(icon.badge);
        for (UINT i = 0; i < count; ++i) text[i] = (wchar_t)icon.badge[i];
        DrawText(text, count, badgeFont, rect, brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
        return;
    }
    const AppIconMaskKey key = { { 'I', 'C', 'O', 'N' }, glyph };
    record(batches.drawGlyph(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
        reinterpret_cast<const uint8_t*>(&key), sizeof(key), 0, colorRgb(brush->color), paintAppIcon, colorAlpha(brush->color)));
}
