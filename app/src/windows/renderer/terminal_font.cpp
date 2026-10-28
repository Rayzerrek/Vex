#include "terminal_font.h"
#include <math.h>

HRESULT createTerminalFonts(IDWriteFactory* factory, const wchar_t* family, float size,
    IDWriteTextFormat* (&fonts)[4], float& cellWidth, float& cellHeight) {
    if (!factory || !family || !isfinite(size) || size <= 0) return E_INVALIDARG;
    IDWriteTextFormat* created[4] = {};
    float width = 0, ascent = 0, descent = 0;
    HRESULT result = S_OK;
    for (int i = 0; i < 4 && SUCCEEDED(result); ++i) {
        result = factory->CreateTextFormat(family, nullptr, (i & 1) ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            (i & 2) ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"en-us", &created[i]);
        if (SUCCEEDED(result)) result = created[i]->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        RendererComPtr<IDWriteTextLayout> measure;
        if (SUCCEEDED(result)) result = factory->CreateTextLayout(L"M", 1, created[i], 200, 200, &measure);
        DWRITE_TEXT_METRICS metrics = {};
        DWRITE_LINE_METRICS line = {};
        UINT32 count = 0;
        if (SUCCEEDED(result)) result = measure->GetMetrics(&metrics);
        if (SUCCEEDED(result)) result = measure->GetLineMetrics(&line, 1, &count);
        if (SUCCEEDED(result)) {
            if (i == 0) width = metrics.width;
            ascent = fmaxf(ascent, line.baseline);
            descent = fmaxf(descent, line.height - line.baseline);
        }
    }
    // A shared baseline must accommodate every style, including fonts with taller bold/italic faces.
    const float baseline = ceilf(ascent), height = baseline + ceilf(descent);
    if (SUCCEEDED(result) && (!isfinite(width) || !isfinite(height) || width <= 0 || height <= 0)) result = E_INVALIDARG;
    for (int i = 0; i < 4 && SUCCEEDED(result); ++i)
        result = created[i]->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, height, baseline);
    if (FAILED(result)) {
        for (auto font : created) if (font) font->Release();
        return result;
    }
    for (int i = 0; i < 4; ++i) fonts[i] = created[i];
    cellWidth = width; cellHeight = height;
    return S_OK;
}

HRESULT TerminalTextLayout::initialize(IDWriteFactory* factory, IDWriteTextFormat* font, const uint8_t* utf8,
    size_t length, float width, float height) {
    if (!factory || !font || !utf8 || !length || length > 65536 || !isfinite(width) || !isfinite(height) || width <= 0 || height <= 0)
        return E_INVALIDARG;
    layout.Reset();
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(utf8), (int)length, nullptr, 0);
    if (count <= 0) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t scratch[4096];
    auto text = count <= 4096 ? scratch : static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, (size_t)count * sizeof(wchar_t)));
    if (!text) return E_OUTOFMEMORY;
    HRESULT result = S_OK;
    uint32_t first = 0;
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(utf8), (int)length, text, count)) result = HRESULT_FROM_WIN32(GetLastError());
    else {
        first = text[0];
        if (count > 1 && first >= 0xd800 && first <= 0xdbff && text[1] >= 0xdc00 && text[1] <= 0xdfff)
            first = 0x10000 + ((first - 0xd800) << 10) + text[1] - 0xdc00;
        result = factory->CreateTextLayout(text, count, font, width, height, &layout);
    }
    if (text != scratch) HeapFree(GetProcessHeap(), 0, text);
    if (SUCCEEDED(result)) {
        RendererComPtr<IDWriteTypography> typography;
        result = factory->CreateTypography(&typography);
        const DWRITE_FONT_FEATURE features[] = {
            { DWRITE_FONT_FEATURE_TAG_STANDARD_LIGATURES, 1 },
            { DWRITE_FONT_FEATURE_TAG_CONTEXTUAL_LIGATURES, 1 },
            { DWRITE_FONT_FEATURE_TAG_CONTEXTUAL_ALTERNATES, 1 },
            { DWRITE_FONT_FEATURE_TAG_KERNING, 0 },
        };
        for (const auto& feature : features) if (SUCCEEDED(result)) result = typography->AddFontFeature(feature);
        if (SUCCEEDED(result)) result = layout->SetTypography(typography.Get(), { 0, (UINT32)count });
    }
    DWRITE_OVERHANG_METRICS overhang = {};
    if (SUCCEEDED(result)) result = layout->GetOverhangMetrics(&overhang);
    if (FAILED(result)) return result;
    ink = D2D1::RectF(-overhang.left, -overhang.top, width + overhang.right, height + overhang.bottom);
    factor = stretchX = 1; offsetX = offsetY = 0;
    bool ascii = true;
    for (size_t i = 0; i < length && ascii; ++i) ascii = utf8[i] >= 32 && utf8[i] <= 126;
    if (ascii) {
        DWRITE_TEXT_METRICS metrics = {};
        result = layout->GetMetrics(&metrics);
        if (FAILED(result)) return result;
        // Synthetic bold and some italic faces have a wider advance than the regular grid.
        // Ligatures must still occupy exactly the cells assigned by the emulator.
        const float advance = metrics.widthIncludingTrailingWhitespace;
        if (advance > 0 && (length > 1 || advance > width)) stretchX = width / advance;
        ink.left *= stretchX; ink.right *= stretchX;
    }
    const bool privateUse = (first >= 0xe000 && first <= 0xf8ff) || (first >= 0xf0000 && first <= 0xffffd) ||
        (first >= 0x100000 && first <= 0x10fffd);
    const float inkWidth = ink.right - ink.left, inkHeight = ink.bottom - ink.top;
    if (privateUse && inkWidth > 0 && inkHeight > 0) {
        // Nerd Fonts' proportional variants may contain a two-cell icon at a one-cell codepoint.
        // Fit its ink rather than cutting it off or changing the emulator's cursor advance.
        factor = fminf(1, fminf(width / inkWidth, height / inkHeight));
        offsetX = (width - inkWidth * factor) * 0.5f - ink.left * factor;
        offsetY = factor < 1 ? (height - inkHeight * factor) * 0.5f - ink.top * factor : 0;
        ink = D2D1::RectF(ink.left * factor + offsetX, ink.top * factor + offsetY,
            ink.right * factor + offsetX, ink.bottom * factor + offsetY);
    }
    return S_OK;
}

void TerminalTextLayout::draw(ID2D1RenderTarget* target, ID2D1SolidColorBrush* brush, float x, float y) const {
    D2D1_MATRIX_3X2_F previous;
    target->GetTransform(&previous);
    target->SetTransform(D2D1::Matrix3x2F::Scale(factor * stretchX, factor) * D2D1::Matrix3x2F::Translation(x + offsetX, y + offsetY) * previous);
    target->DrawTextLayout(D2D1::Point2F(0, 0), layout.Get(), brush, D2D1_DRAW_TEXT_OPTIONS_NO_SNAP);
    target->SetTransform(previous);
}

namespace {
struct SubstitutionCoverage {
    const uint8_t* data;
    size_t size;
    const UINT16* asciiGlyphs;
    bool* characters;
    uint16_t& before;
    uint16_t& input;
    uint16_t& after;
    bool valid = true;
    bool contains(size_t offset, size_t length) { if (offset > size || length > size - offset) valid = false; return valid; }
    UINT16 u16(size_t offset) { return contains(offset, 2) ? (UINT16)((data[offset] << 8) | data[offset + 1]) : 0; }
    UINT32 u32(size_t offset) { return ((UINT32)u16(offset) << 16) | u16(offset + 2); }
    void all() { for (unsigned c = 32; c < 127; ++c) characters[c] = true; input = 65535; }
    void glyph(UINT16 value) {
        for (unsigned c = 32; c < 127; ++c) if (asciiGlyphs[c - 32] == value) characters[c] = true;
    }
    UINT16 glyphClass(size_t offset, UINT16 value) {
        const UINT16 format = u16(offset);
        if (format == 1) {
            const UINT16 first = u16(offset + 2), count = u16(offset + 4);
            if (value >= first && (unsigned)value - first < count) return u16(offset + 6 + (value - first) * 2);
        } else if (format == 2) {
            const UINT16 count = u16(offset + 2);
            if (!contains(offset + 4, (size_t)count * 6)) return 0;
            for (unsigned i = 0; i < count; ++i) {
                const size_t range = offset + 4 + i * 6;
                if (value >= u16(range) && value <= u16(range + 2)) return u16(range + 4);
            }
        } else valid = false;
        return 0;
    }
    void inputs(size_t cursor, UINT16 count, size_t classes = 0) {
        if (!contains(cursor, (size_t)count * 2)) return;
        for (unsigned i = 0; i < count; ++i) {
            const UINT16 value = u16(cursor + i * 2);
            if (!classes) glyph(value);
            else for (unsigned c = 32; c < 127; ++c) if (glyphClass(classes, asciiGlyphs[c - 32]) == value) characters[c] = true;
        }
    }
    void coverage(size_t offset) {
        const UINT16 format = u16(offset), count = u16(offset + 2);
        if (format == 1) {
            if (!contains(offset + 4, (size_t)count * 2)) return;
            for (unsigned i = 0; i < count; ++i) {
                const UINT16 glyph = u16(offset + 4 + i * 2);
                for (unsigned c = 32; c < 127; ++c) if (asciiGlyphs[c - 32] == glyph) characters[c] = true;
            }
        } else if (format == 2) {
            if (!contains(offset + 4, (size_t)count * 6)) return;
            for (unsigned i = 0; i < count; ++i) {
                const UINT16 first = u16(offset + 4 + i * 6), last = u16(offset + 6 + i * 6);
                for (unsigned c = 32; c < 127; ++c) if (asciiGlyphs[c - 32] >= first && asciiGlyphs[c - 32] <= last) characters[c] = true;
            }
        } else valid = false;
    }
    size_t skipArray(size_t cursor, uint16_t& maximum, unsigned omitted = 0) {
        const UINT16 count = u16(cursor);
        if (count < omitted) { valid = false; return cursor; }
        if (count > maximum) maximum = count;
        contains(cursor + 2, (size_t)(count - omitted) * 2);
        return cursor + 2 + (count - omitted) * 2;
    }
    void contextRules(size_t offset, UINT16 type, UINT16 format) {
        if (format == 3) {
            if (type == 5) {
                const UINT16 count = u16(offset + 2);
                if (count > input) input = count;
                if (!count || !contains(offset + 6, (size_t)count * 2)) { valid = false; return; }
                for (unsigned i = 0; i < count; ++i) coverage(offset + u16(offset + 6 + i * 2));
            } else {
                size_t cursor = skipArray(offset + 2, before);
                const UINT16 count = u16(cursor);
                if (!count || !contains(cursor + 2, (size_t)count * 2)) { valid = false; return; }
                for (unsigned i = 0; i < count; ++i) coverage(offset + u16(cursor + 2 + i * 2));
                cursor = skipArray(cursor, input);
                skipArray(cursor, after);
            }
            return;
        }
        if (format != 1 && format != 2) { valid = false; return; }
        coverage(offset + u16(offset + 2));
        const size_t sets = offset + (type == 5 ? (format == 1 ? 4 : 6) : (format == 1 ? 4 : 10));
        const UINT16 count = u16(sets);
        if (!contains(sets + 2, (size_t)count * 2)) return;
        for (unsigned i = 0; i < count && valid; ++i) {
            const UINT16 relative = u16(sets + 2 + i * 2);
            if (!relative) continue;
            const size_t set = offset + relative;
            const UINT16 rules = u16(set);
            if (!contains(set + 2, (size_t)rules * 2)) return;
            for (unsigned j = 0; j < rules && valid; ++j) {
                size_t rule = set + u16(set + 2 + j * 2);
                if (type == 6) {
                    rule = skipArray(rule, before);
                    const UINT16 glyphs = u16(rule);
                    if (!glyphs) { valid = false; return; }
                    inputs(rule + 2, glyphs - 1, format == 2 ? offset + u16(offset + 6) : 0);
                    rule = skipArray(rule, input, 1);
                    skipArray(rule, after);
                } else {
                    const UINT16 glyphs = u16(rule);
                    if (glyphs > input) input = glyphs;
                    if (!glyphs) { valid = false; return; }
                    inputs(rule + 4, glyphs - 1, format == 2 ? offset + u16(offset + 4) : 0);
                }
            }
        }
    }
    void subtable(UINT16 type, size_t offset, unsigned depth = 0) {
        if (depth > 8) { valid = false; return; }
        const UINT16 format = u16(offset);
        if (type == 7 && format == 1) {
            const UINT16 extensionType = u16(offset + 2);
            subtable(extensionType, offset + u32(offset + 4), depth + 1);
        } else if (type >= 1 && type <= 4) {
            coverage(offset + u16(offset + 2));
            if (type == 4 && format == 1) {
                const UINT16 count = u16(offset + 4);
                if (!contains(offset + 6, (size_t)count * 2)) return;
                for (unsigned i = 0; i < count && valid; ++i) {
                    const size_t set = offset + u16(offset + 6 + i * 2);
                    const UINT16 ligatures = u16(set);
                    if (!contains(set + 2, (size_t)ligatures * 2)) return;
                    for (unsigned j = 0; j < ligatures; ++j) {
                        const size_t ligature = set + u16(set + 2 + j * 2);
                        const UINT16 components = u16(ligature + 2);
                        if (components > input) input = components;
                        if (!components || !contains(ligature + 4, (size_t)(components - 1) * 2)) { valid = false; return; }
                        for (unsigned k = 1; k < components; ++k) {
                            const UINT16 glyph = u16(ligature + 2 + k * 2);
                            for (unsigned c = 32; c < 127; ++c) if (asciiGlyphs[c - 32] == glyph) characters[c] = true;
                        }
                    }
                }
            }
        } else if (type == 5 || type == 6) contextRules(offset, type, format);
        else if (type == 8 && format == 1) {
            coverage(offset + u16(offset + 2));
            const size_t cursor = skipArray(offset + 4, before);
            skipArray(cursor, after);
        } else valid = false;
    }
    void read() {
        const size_t features = u16(6), lookups = u16(8);
        const UINT16 featureCount = u16(features), lookupCount = u16(lookups);
        if (!contains(features + 2, (size_t)featureCount * 6) || !contains(lookups + 2, (size_t)lookupCount * 2)) { all(); return; }
        for (unsigned i = 0; i < featureCount && valid; ++i) {
            const size_t record = features + 2 + i * 6;
            const UINT32 tag = u32(record);
            if (tag != 0x6c696761 && tag != 0x636c6967 && tag != 0x63616c74) continue; // liga, clig, calt
            const size_t feature = features + u16(record + 4);
            const UINT16 count = u16(feature + 2);
            if (!contains(feature + 4, (size_t)count * 2)) break;
            for (unsigned j = 0; j < count && valid; ++j) {
                const UINT16 index = u16(feature + 4 + j * 2);
                if (index >= lookupCount) { valid = false; break; }
                const size_t lookup = lookups + u16(lookups + 2 + index * 2);
                const UINT16 type = u16(lookup), subtables = u16(lookup + 4);
                if (!contains(lookup + 6, (size_t)subtables * 2)) break;
                for (unsigned k = 0; k < subtables && valid; ++k) subtable(type, lookup + u16(lookup + 6 + k * 2));
            }
        }
        if (!valid) all();
    }
};
}

HRESULT TerminalLigatureMap::initialize(IDWriteFactory* factory, IDWriteTextFormat* const (&fonts)[4]) {
    TerminalLigatureMap next;
    for (unsigned style = 0; style < 4; ++style) {
        RendererComPtr<IDWriteFontCollection> collection;
        HRESULT result = fonts[style]->GetFontCollection(&collection);
        if (SUCCEEDED(result) && !collection) result = factory->GetSystemFontCollection(&collection);
        wchar_t familyName[128];
        if (SUCCEEDED(result)) result = fonts[style]->GetFontFamilyName(familyName, 128);
        UINT32 familyIndex = 0; BOOL exists = FALSE;
        if (SUCCEEDED(result)) result = collection->FindFamilyName(familyName, &familyIndex, &exists);
        if (FAILED(result)) return result;
        if (!exists) { for (unsigned c = 32; c < 127; ++c) next.characters[style][c] = true; continue; }
        RendererComPtr<IDWriteFontFamily> family;
        RendererComPtr<IDWriteFont> font;
        RendererComPtr<IDWriteFontFace> face;
        result = collection->GetFontFamily(familyIndex, &family);
        if (SUCCEEDED(result)) result = family->GetFirstMatchingFont(fonts[style]->GetFontWeight(), fonts[style]->GetFontStretch(), fonts[style]->GetFontStyle(), &font);
        if (SUCCEEDED(result)) result = font->CreateFontFace(&face);
        UINT32 codepoints[95]; UINT16 glyphs[95];
        for (unsigned i = 0; i < 95; ++i) codepoints[i] = i + 32;
        if (SUCCEEDED(result)) result = face->GetGlyphIndices(codepoints, 95, glyphs);
        const void* table = nullptr; UINT32 bytes = 0; void* context = nullptr; BOOL found = FALSE;
        if (SUCCEEDED(result)) result = face->TryGetFontTable(DWRITE_MAKE_OPENTYPE_TAG('G', 'S', 'U', 'B'), &table, &bytes, &context, &found);
        if (FAILED(result)) return result;
        if (found) {
            SubstitutionCoverage coverage = { static_cast<const uint8_t*>(table), bytes, glyphs, next.characters[style], next.before[style], next.input[style], next.after[style] };
            coverage.read();
            // Keep punctuation together: closing delimiters can be a later ligature input
            // even when they never appear in a lookup's initial coverage.
            bool active = false;
            for (unsigned c = 32; c < 127; ++c) active |= next.characters[style][c];
            if (active) for (unsigned c = 33; c < 127; ++c)
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) next.characters[style][c] = true;
            face->ReleaseFontTable(context);
        }
    }
    *this = next;
    return S_OK;
}

void TerminalLigatureMap::next(const uint8_t* text, size_t length, uint32_t style, size_t& start, size_t& end) const {
    const auto& candidates = characters[style & 3];
    start = 0;
    while (start < length && (text[start] >= 128 || !candidates[text[start]])) ++start;
    end = start;
    while (end < length && text[end] < 128 && candidates[text[end]]) ++end;
}
