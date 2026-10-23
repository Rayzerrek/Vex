#include "tab_rename_layout.h"

int testTabRenameLayout() {
    IDWriteFactory* factory = nullptr;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory)))) return 1;
    IDWriteTextFormat* font = nullptr;
    const HRESULT result = factory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12.5f, L"en-us", &font);
    if (FAILED(result)) { factory->Release(); return 2; }
    int failure = 0;
    {
        TabRenameLayout cache;
        const wchar_t text[] = L"api \xd83d\xde80 server";
        const UINT32 units = (UINT32)wcslen(text);
        IDWriteTextLayout* first = cache.get(factory, font, text, units);
        if (!first) failure = 3;
        else {
            // Keep the old layout alive: recreating it cannot accidentally pass through address reuse.
            first->AddRef();
            for (int i = 0; i < 1000; ++i) {
                if (cache.get(factory, font, text, units) != first) { failure = 4; break; }
            }
            FLOAT x = 0, y = 0;
            DWRITE_HIT_TEST_METRICS hit = {};
            if (FAILED(first->HitTestTextPosition(6, FALSE, &x, &y, &hit)) || x <= 0) failure = 5;
            IDWriteTextLayout* changed = cache.get(factory, font, L"new name", 8);
            if (!changed || changed == first) failure = 6;
            if (changed) {
                changed->AddRef();
                IDWriteTextFormat* large = nullptr;
                if (FAILED(factory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 20, L"en-us", &large))) failure = 7;
                else {
                    IDWriteTextLayout* resized = cache.get(factory, large, L"new name", 8);
                    DWRITE_TEXT_METRICS before = {}, after = {};
                    if (!resized || resized == changed || FAILED(changed->GetMetrics(&before)) || FAILED(resized->GetMetrics(&after)) || after.width <= before.width) failure = 8;
                    large->Release();
                }
                changed->Release();
            }
            cache.clear();
            IDWriteTextLayout* reopened = cache.get(factory, font, text, units);
            if (!reopened || reopened == first) failure = 9;
            first->Release();
        }
        if (cache.get(factory, font, L"", 0) || cache.get(factory, font, text, 257)) failure = 10;
    }
    font->Release();
    factory->Release();
    return failure;
}
