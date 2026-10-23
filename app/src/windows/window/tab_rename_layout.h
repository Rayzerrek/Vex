#pragma once
#include <dwrite.h>
#include <string.h>

// One rename editor is active at a time. Keep its layout across cursor moves and animation frames.
class TabRenameLayout final {
public:
    TabRenameLayout() = default;
    TabRenameLayout(const TabRenameLayout&) = delete;
    TabRenameLayout& operator=(const TabRenameLayout&) = delete;
    ~TabRenameLayout() { clear(); }

    // Returns a borrowed layout; text and font changes invalidate the single cached entry.
    IDWriteTextLayout* get(IDWriteFactory* factory, IDWriteTextFormat* font, const wchar_t* text, UINT32 units) {
        if (!factory || !font || !text || !units || units > 256) return nullptr;
        if (layout && font == cachedFont && units == cachedUnits && !memcmp(text, cachedText, units * sizeof(wchar_t))) return layout;
        clear();
        IDWriteTextLayout* next = nullptr;
        if (FAILED(factory->CreateTextLayout(text, units, font, 4096.0f, 24.0f, &next))) return nullptr;
        if (FAILED(next->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP))) { next->Release(); return nullptr; }
        layout = next;
        cachedFont = font;
        // Retaining the font prevents a new format from reusing the cache key's address.
        cachedFont->AddRef();
        cachedUnits = units;
        memcpy(cachedText, text, units * sizeof(wchar_t));
        return layout;
    }

    void clear() {
        if (layout) { layout->Release(); layout = nullptr; }
        if (cachedFont) { cachedFont->Release(); cachedFont = nullptr; }
        cachedUnits = 0;
    }
private:
    IDWriteTextLayout* layout = nullptr;
    IDWriteTextFormat* cachedFont = nullptr;
    UINT32 cachedUnits = 0;
    wchar_t cachedText[256] = {};
};
