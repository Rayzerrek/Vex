#include "terminal_font.h"
#include <wincodec.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

namespace {
HRESULT drawPieces(IDWriteFactory* factory, IDWriteTextFormat* font, ID2D1RenderTarget* target, ID2D1SolidColorBrush* brush,
    const TerminalLigatureMap& map, unsigned style, const char* text, float width, float height, bool individual) {
    const auto bytes = reinterpret_cast<const uint8_t*>(text);
    const size_t length = strlen(text);
    for (size_t offset = 0; offset < length;) {
        size_t start = length - offset, end = start;
        if (!individual) map.next(bytes + offset, length - offset, style, start, end);
        for (size_t i = 0; i < start; ++i) {
            TerminalTextLayout part;
            const HRESULT result = part.initialize(factory, font, bytes + offset + i, 1, width, height);
            if (FAILED(result)) return result;
            part.draw(target, brush, 32 + (offset + i) * width, 32);
        }
        if (end > start) {
            TerminalTextLayout part;
            const HRESULT result = part.initialize(factory, font, bytes + offset + start, end - start, (end - start) * width, height);
            if (FAILED(result)) return result;
            part.draw(target, brush, 32 + (offset + start) * width, 32);
        }
        offset += end;
    }
    return S_OK;
}
int compareLigatures(IDWriteFactory* factory, IDWriteTextFormat* font, IWICBitmap* bitmap, ID2D1RenderTarget* target,
    ID2D1SolidColorBrush* brush, const TerminalLigatureMap& map, unsigned style, float width, float height, const wchar_t* family,
    bool& sawLigature) {
    const char* samples[] = { "a != b", "x === y", "x -> y", "x => y", "x <= y", "fi fl ffi", "www == www", "/* a */", "<==> ==>>", "a->b", "a!=b", "a===b", "a<=b", "a...b", "a::b" };
    const char* syntheticSamples[] = { "!=", "===", "->", "=>", "<=", "fi", "ffi", "/*", "<==>", "->", "!=", "===", "<=", "...", "::" };
    RendererComPtr<IDWriteTextLayout> measure;
    DWRITE_TEXT_METRICS measureMetrics = {};
    if (FAILED(factory->CreateTextLayout(L"M M M M", 7, font, 1000, 1000, &measure)) || FAILED(measure->GetMetrics(&measureMetrics))) return 24;
    // Synthetic faces can have nonuniform space advances. Compare their shaped operators
    // separately; a proportional native line is not a reference for the terminal grid.
    const bool uniform = fabsf(measureMetrics.width / 7 - width) < 0.001f;
    uint8_t reference[256 * 256];
    for (unsigned sample = 0; sample < sizeof(samples) / sizeof(samples[0]); ++sample) {
        const char* text = uniform ? samples[sample] : syntheticSamples[sample];
        TerminalTextLayout whole;
        HRESULT result = whole.initialize(factory, font, reinterpret_cast<const uint8_t*>(text), strlen(text), strlen(text) * width, height);
        if (FAILED(result)) return 20;
        for (unsigned mode = 0; mode < 3; ++mode) {
            target->SetDpi(96, 96);
            target->BeginDraw();
            target->Clear(D2D1::ColorF(0, 0.0f));
            if (!mode) whole.draw(target, brush, 32, 32);
            else result = drawPieces(factory, font, target, brush, map, style, text, width, height, mode == 2);
            const HRESULT end = target->EndDraw();
            if (FAILED(result) || FAILED(end)) return 21;
            RendererComPtr<IWICBitmapLock> lock;
            WICRect rect = { 0, 0, 256, 256 };
            result = bitmap->Lock(&rect, WICBitmapLockRead, &lock);
            UINT stride = 0, bytes = 0; BYTE* pixels = nullptr;
            if (SUCCEEDED(result)) result = lock->GetStride(&stride);
            if (SUCCEEDED(result)) result = lock->GetDataPointer(&bytes, &pixels);
            if (FAILED(result)) return 22;
            unsigned differences = 0, covered = 0;
            for (unsigned y = 0; y < 256; ++y) for (unsigned x = 0; x < 256; ++x) {
                const uint8_t alpha = pixels[y * stride + x * 4 + 3];
                const size_t index = y * 256 + x;
                if (!mode) reference[index] = alpha;
                else {
                    if (reference[index] || alpha) ++covered;
                    if (abs((int)reference[index] - alpha) > 8) ++differences;
                }
            }
            if (mode == 1 && differences > covered / 100 + 4) {
                fprintf(stderr, "ligature segmentation differs: %ls style=%u text=%s pixels=%u/%u context=%u/%u/%u\n",
                    family, style, text, differences, covered, map.before[style], map.input[style], map.after[style]);
                RendererComPtr<IDWriteTextLayout> measured;
                DWRITE_TEXT_METRICS metrics = {};
                factory->CreateTextLayout(L"MMMMMM", 6, font, 1000, 1000, &measured);
                measured->GetMetrics(&metrics);
                fprintf(stderr, "cell=%.4f actual=%.4f\n", width, metrics.width / 6);
                return 23;
            }
            if (mode == 2 && differences > 20) sawLigature = true;
        }
    }
    return 0;
}
}

int testTerminalFonts() {
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(apartment)) return 1;
    int failure = 0;
    {
        RendererComPtr<IDWriteFactory> factory;
        RendererComPtr<ID2D1Factory> d2d;
        RendererComPtr<IWICImagingFactory> imaging;
        RendererComPtr<IWICBitmap> bitmap;
        RendererComPtr<ID2D1RenderTarget> target;
        RendererComPtr<ID2D1SolidColorBrush> brush;
        HRESULT result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory));
        if (SUCCEEDED(result)) result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &d2d);
        if (SUCCEEDED(result)) result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&imaging));
        if (SUCCEEDED(result)) result = imaging->CreateBitmap(256, 256, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, &bitmap);
        if (SUCCEEDED(result)) result = d2d->CreateWicBitmapRenderTarget(bitmap.Get(), D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED)), &target);
        if (SUCCEEDED(result)) result = target->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), &brush);
        if (FAILED(result)) failure = 2;
        if (!failure) target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        bool sawOverhang = false, sawLigature = false, hadLigatureFont = false;
        RendererComPtr<IDWriteFontCollection> collection;
        if (!failure && FAILED(factory->GetSystemFontCollection(&collection))) failure = 12;
        const wchar_t* families[] = { L"Consolas", L"Courier New", L"Segoe MDL2 Assets", L"Cascadia Mono", L"Cascadia Code",
            L"Ioskeley Mono", L"IoskeleyMono Nerd Font", L"JetBrains Mono" };
        const char* samples[] = { "j", "f", "W", "\xc4\x85", "e\xcc\x81\xcc\x82", "\xee\x9c\x80", "\xf3\xb0\x80\x81" };
        for (const auto family : families) {
            if (failure) break;
            UINT32 familyIndex = 0; BOOL exists = FALSE;
            if (FAILED(collection->FindFamilyName(family, &familyIndex, &exists))) { failure = 13; break; }
            if (!exists) continue;
            IDWriteTextFormat* fonts[4] = {};
            float width = 0, height = 0;
            result = createTerminalFonts(factory.Get(), family, 24, fonts, width, height);
            if (FAILED(result)) { failure = 3; break; }
            TerminalLigatureMap map;
            if (FAILED(map.initialize(factory.Get(), fonts))) failure = 14;
            const bool programmingFont = wcscmp(family, L"JetBrains Mono") == 0 || wcscmp(family, L"Cascadia Code") == 0 ||
                wcscmp(family, L"IoskeleyMono Nerd Font") == 0;
            hadLigatureFont |= programmingFont;
            float baseline = 0;
            for (int style = 0; style < 4 && !failure; ++style) {
                DWRITE_LINE_SPACING_METHOD method;
                float lineHeight = 0, lineBaseline = 0;
                result = fonts[style]->GetLineSpacing(&method, &lineHeight, &lineBaseline);
                if (!style) baseline = lineBaseline;
                if (FAILED(result) || method != DWRITE_LINE_SPACING_METHOD_UNIFORM || lineHeight != height || lineBaseline != baseline) { failure = 4; break; }
                if (programmingFont) {
                    failure = compareLigatures(factory.Get(), fonts[style], bitmap.Get(), target.Get(), brush.Get(), map, style, width, height, family, sawLigature);
                    if (failure) break;
                }
                for (const char* text : samples) {
                    if (failure) break;
                    TerminalTextLayout layout;
                    // A narrow terminal advance exercises oversized BMP and supplementary PUA icons.
                    const bool icon = (unsigned char)text[0] == 0xee || (unsigned char)text[0] == 0xf3;
                    const float advance = icon ? width * 0.5f : width;
                    result = layout.initialize(factory.Get(), fonts[style], reinterpret_cast<const uint8_t*>(text), strlen(text), advance, height);
                    if (FAILED(result)) { failure = 5; break; }
                    const auto bounds = layout.bounds();
                    if (icon && (bounds.left < -0.001f || bounds.right > advance + 0.001f)) { failure = 6; break; }
                    if (!icon && (bounds.left < 0 || bounds.right > advance)) sawOverhang = true;
                    const float scales[] = { 1, 1.25f, 1.5f, 2 };
                    for (float scale : scales) {
                        target->SetDpi(96 * scale, 96 * scale);
                        target->BeginDraw();
                        target->Clear(D2D1::ColorF(0, 0.0f));
                        layout.draw(target.Get(), brush.Get(), 32, 32);
                        result = target->EndDraw();
                        if (FAILED(result)) { failure = 7; break; }
                        RendererComPtr<IWICBitmapLock> lock;
                        WICRect rect = { 0, 0, 256, 256 };
                        result = bitmap->Lock(&rect, WICBitmapLockRead, &lock);
                        UINT stride = 0, size = 0; BYTE* pixels = nullptr;
                        if (SUCCEEDED(result)) result = lock->GetStride(&stride);
                        if (SUCCEEDED(result)) result = lock->GetDataPointer(&size, &pixels);
                        if (FAILED(result)) { failure = 8; break; }
                        UINT covered = 0;
                        for (UINT y = 0; y < 256 && !failure; ++y) for (UINT x = 0; x < 256; ++x) {
                            if (!pixels[y * stride + x * 4 + 3]) continue;
                            ++covered;
                            if (x + 1 < floorf((32 + bounds.left) * scale) - 1 || x > ceilf((32 + bounds.right) * scale) + 1 ||
                                y + 1 < floorf((32 + bounds.top) * scale) - 1 || y > ceilf((32 + bounds.bottom) * scale) + 1) { failure = 9; break; }
                        }
                        if (!covered) failure = 10;
                        if (failure) break;
                    }
                }
            }
            for (auto font : fonts) font->Release();
        }
        if (!failure && !sawOverhang) failure = 11;
        if (!failure && hadLigatureFont && !sawLigature) failure = 15;
    }
    CoUninitialize();
    if (failure) fprintf(stderr, "terminal font regression: %d\n", failure);
    return failure;
}
