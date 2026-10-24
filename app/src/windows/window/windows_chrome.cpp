#include "windows_chrome.h"
#include "drawing_color.h"
#include "icon_contrast.h"
#include "ui_renderer.h"
#include "terminal_drawing.h"
#include "chrome_motion.h"
#include "tab_rename_layout.h"
#include <math.h>
#include <stdio.h>

namespace {
UiRenderer* target;
IDWriteFactory* textFactory;
UiBrush paint;
UiBrush* brush = &paint;
IDWriteTextFormat* chromeFont;
IDWriteTextFormat* chromeBoldFont;
IDWriteTextFormat* badgeFont;
IDWriteTextFormat* paneTitleFont;
TabRenameLayout tabRenameLayout;
VexChromePalette chromePalette;
float tabWidthDip;
uint32_t tabStripCount;
int hoveredTabIndex;
bool hoveredTabClose;
double chromeTimeMs;
bool chromeMotionEnabled, chromeMotionPending, chromeEffectsPending;
ChromeMotion tabSelectionMotion, tabSelectionWidthMotion, tabHoverMotion[8], tabCloseHoverMotion[8], tabEditMotion[8];
ChromeMotion paneActionHoverMotion[4];
ChromeMotion splitPreviewRectMotion[4], splitPreviewAlphaMotion;
ChromeMotion pathOpenMotion, pathRowMotion;
bool splitPreviewSeenLastFrame, splitPreviewSeenThisFrame;
bool pathCompletionSeenLastFrame, pathCompletionSeenThisFrame;
float previousTabWidth;
uint64_t activeTabIconKey, focusedPaneIconKey;
size_t activeTabIconCount;
constexpr float tabStripLeft = 8;
constexpr bool roundCaps = true;
static uint32_t blendRgb(uint32_t a, uint32_t b, float t) {
    const float clamped = fminf(1.0f, fmaxf(0.0f, t));
    const auto ch = [clamped](uint32_t ca, uint32_t cb) -> uint32_t {
        return (uint32_t)lroundf((float)ca + ((float)cb - (float)ca) * clamped);
    };
    const uint32_t r = ch((a >> 16) & 0xffu, (b >> 16) & 0xffu);
    const uint32_t g = ch((a >> 8) & 0xffu, (b >> 8) & 0xffu);
    const uint32_t bl = ch(a & 0xffu, b & 0xffu);
    return (r << 16) | (g << 8) | bl;
}
static void rectangle(float x, float y, float width, float height, uint32_t color) {
    brush->SetColor(rgb(color));
    target->FillRectangle(D2D1::RectF(x, y, x + width, y + height), brush);
}
static void labelWithFont(IDWriteTextFormat* font, const wchar_t* text, float x, float y, float width, uint32_t color, float alpha = 1.0f) {
    brush->SetColor(rgb(color, alpha < 1.0f ? alpha : 1.0f));
    target->DrawText(text, (UINT32)wcslen(text), font ? font : chromeFont, D2D1::RectF(x, y, x + width, y + 24), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
}
static void label(const wchar_t* text, float x, float y, float width, uint32_t color) {
    labelWithFont(chromeFont, text, x, y, width, color);
}
}

void beginWindowChromeFrame(const WindowChromeFrame& frame) {
    splitPreviewSeenLastFrame = splitPreviewSeenThisFrame;
    splitPreviewSeenThisFrame = false;
    pathCompletionSeenLastFrame = pathCompletionSeenThisFrame;
    pathCompletionSeenThisFrame = false;
    if (chromeFont != frame.normalFont || textFactory != frame.textFactory) tabRenameLayout.clear();
    target = frame.target;
    textFactory = frame.textFactory;
    chromeFont = frame.normalFont;
    chromeBoldFont = frame.boldFont;
    badgeFont = frame.badgeFont;
    paneTitleFont = frame.paneTitleFont;
    chromePalette = frame.palette;
    tabWidthDip = frame.tabWidth;
    tabStripCount = frame.tabCount;
    hoveredTabIndex = frame.hoveredTab;
    hoveredTabClose = frame.hoveredClose;
    chromeTimeMs = frame.timeMs;
    chromeMotionEnabled = frame.motionEnabled;
    chromeMotionPending = false;
    chromeEffectsPending = false;
    activeTabIconKey = 0; focusedPaneIconKey = 0; activeTabIconCount = 0;
}
bool windowChromeAnimationPending() { return chromeMotionPending; }
bool windowChromeEffectsPending() { return chromeEffectsPending; }
WindowChromeDiagnostics windowChromeDiagnostics() {
    return { activeTabIconKey, focusedPaneIconKey, activeTabIconCount, tabSelectionMotion.sample(chromeTimeMs) };
}

static uint64_t computeIconCacheKey(VexAppIcon icon, uint32_t sizeCode) {
    uint64_t badgeWord = ((uint64_t)icon.badge[0] << 8) | (uint64_t)icon.badge[1];
    return ((uint64_t)(icon.color_rgb & 0xffffffu)) |
           ((uint64_t)icon.glyph << 24) |
           ((uint64_t)(icon.kind & 0xfu) << 32) |
           ((uint64_t)(icon.badge_len & 0xfu) << 36) |
           ((uint64_t)(sizeCode & 0xffu) << 40) |
           (badgeWord << 48);
}

static void drawAppIconContent(UiRenderer* rt, UiBrush* b, VexAppIcon icon, float ox, float oy, float sizeDip) {
    const float s = sizeDip / 24.0f;
    const auto pt = [&](float px, float py) -> D2D1_POINT_2F {
        return D2D1::Point2F(ox + px * s, oy + py * s);
    };
    const auto rc = [&](float x1, float y1, float x2, float y2) -> D2D1_RECT_F {
        return D2D1::RectF(ox + x1 * s, oy + y1 * s, ox + x2 * s, oy + y2 * s);
    };
    const auto rrc = [&](float x1, float y1, float x2, float y2, float rx) -> D2D1_ROUNDED_RECT {
        return D2D1::RoundedRect(rc(x1, y1, x2, y2), rx * s, rx * s);
    };
    const float sw = fmaxf(1.25f, 2.1f * s);
    const auto line = [&](float x1, float y1, float x2, float y2) {
        rt->DrawLine(pt(x1, y1), pt(x2, y2), b, sw, roundCaps);
    };
    const auto fillBox = [&](float x1, float y1, float x2, float y2, float rx) {
        rt->FillRoundedRectangle(rrc(x1, y1, x2, y2, rx), b);
    };

    if (icon.kind == 2 && icon.badge_len > 0 && badgeFont) {
        const uint32_t fill = contrastingIconColor(icon.color_rgb, chromePalette.pane_header_bg);
        b->SetColor(rgb(fill));
        fillBox(1.5f, 1.5f, 22.5f, 22.5f, 5.0f);
        wchar_t wide[5] = {};
        uint8_t count = icon.badge_len > 2 ? 2 : icon.badge_len;
        for (uint8_t i = 0; i < count; i++) wide[i] = (wchar_t)icon.badge[i];
        b->SetColor(rgb(iconContrastRatio(0xffffff, fill) >= iconContrastRatio(0, fill) ? 0xffffff : 0));
        const float textLeft = count == 1 ? ox + sizeDip * 0.27f : ox + sizeDip * 0.12f;
        rt->DrawText(wide, count, badgeFont, D2D1::RectF(textLeft, oy + sizeDip * 0.05f, ox + sizeDip, oy + sizeDip), b, D2D1_DRAW_TEXT_OPTIONS_CLIP);
        return;
    }

    b->SetColor(rgb(contrastingIconColor(icon.color_rgb, chromePalette.pane_header_bg)));
    if (icon.kind == 0 || icon.glyph == 0) {
        line(4.5f, 7.0f, 10.5f, 12.0f);
        line(10.5f, 12.0f, 4.5f, 17.0f);
        line(12.0f, 17.0f, 19.5f, 17.0f);
    } else {
        rt->DrawAppIcon(icon.glyph, badgeFont, rc(0, 0, 24, 24), b, chromePalette.pane_header_bg);
    }
}

static void drawAppIcon(VexAppIcon icon, float x, float y, float sizeDip) {
    if (target) drawAppIconContent(target, brush, icon, x, y, sizeDip);
}

static uint64_t drawAppIconTile(VexAppIcon icon, float x, float y) {
    constexpr float glyphSize = 12.0f;
    brush->SetColor(rgb(chromePalette.pane_header_bg));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, y, x + 18.0f, y + 18.0f), 4.0f, 4.0f), brush);
    brush->SetColor(rgb(chromePalette.border));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x + 0.5f, y + 0.5f, x + 17.5f, y + 17.5f), 3.5f, 3.5f), brush, 1.0f);
    drawAppIcon(icon, x + 3.0f, y + 3.0f, glyphSize);
    return computeIconCacheKey(icon, (uint32_t)glyphSize);
}

static void labelUtf8WithFont(IDWriteTextFormat* format, const uint8_t* bytes, size_t length, float x, float y, float width, uint32_t color);
static uint32_t programStatusColor(VexProgramStatus status) {
    if (status.state == 4) return chromePalette.is_dark ? 0xe8c98d : 0x8b6019;
    if (status.state == 5) return chromePalette.is_dark ? 0xe99ba4 : 0xaa3040;
    if (status.state == 3) return chromePalette.is_dark ? 0x85c9ad : 0x27664e;
    if (status.state == 2) return chromePalette.accent;
    return chromePalette.text_muted;
}
static void drawProgramStatus(VexProgramStatus status, float x, float y, float width, bool iconOnly) {
    if (status.state < 1 || status.state > 5) return;
    const uint32_t color = programStatusColor(status);
    const float phase = (float)(status.phase % 1200) / 1200.0f;
    const bool motion = vex_window_animations_enabled();
    if (iconOnly) {
        if (status.state == 2) {
            // A staggered raised-cosine wave eases to rest between lifts.
            for (uint32_t i = 0; i < 3; ++i) {
                const float t = fmodf(phase + 1.0f - (float)i * 0.14f, 1.0f);
                const float lift = motion && t < 0.56f ? 0.5f - 0.5f * cosf(t / 0.56f * 6.2831853f) : 0.0f;
                const float radius = 1.45f + lift * 0.35f;
                brush->SetColor(rgb(color, motion ? 0.42f + lift * 0.58f : 0.85f));
                target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + 2.5f + (float)i * 4.6f, y + 10.0f - lift * 2.5f), radius, radius), brush);
            }
        } else if (status.state == 1) {
            brush->SetColor(rgb(color));
            target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(x + 6, y + 10), 2.5f, 2.5f), brush, 1);
        } else {
            // Waiting breathes quietly; success and failure remain unambiguous at rest.
            const float alpha = status.state == 4 && motion ? 0.72f + 0.28f * cosf(phase * 6.2831853f) : 1.0f;
            brush->SetColor(rgb(color, alpha));
            const float cx = x + 6.5f, cy = y + 10.0f;
            if (status.state == 3) {
                target->DrawLine(D2D1::Point2F(cx - 4, cy), D2D1::Point2F(cx - 1, cy + 3), brush, 1.6f, true);
                target->DrawLine(D2D1::Point2F(cx - 1, cy + 3), D2D1::Point2F(cx + 4.5f, cy - 3.5f), brush, 1.6f, true);
            } else {
                target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy), 5.5f, 5.5f), brush, 1.2f);
                if (status.state == 4) {
                    for (int i = 0; i < 2; ++i) {
                        const float dx = i ? 1.8f : -1.8f;
                        target->DrawLine(D2D1::Point2F(cx + dx, cy - 2), D2D1::Point2F(cx + dx, cy + 2), brush, 1.4f, true);
                    }
                } else {
                    target->DrawLine(D2D1::Point2F(cx, cy - 2.7f), D2D1::Point2F(cx, cy + 0.5f), brush, 1.4f, true);
                    target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy + 3), 0.8f, 0.8f), brush);
                }
            }
        }
        return;
    }
    const bool progress = status.progress >= 0 && status.progress <= 100 && (status.state == 2 || status.state == 4);
    const float reserve = progress ? 32.0f : 0;
    labelUtf8WithFont(badgeFont, status.detail, status.detail_length, x + 4, y + 2, fmaxf(0, width - reserve - 8), color);
    if (progress) {
        wchar_t caption[8]; swprintf(caption, 8, L"%d%%", status.progress);
        labelWithFont(badgeFont, caption, x + width - 30, y + 2, 30, color);
    }
}
static wchar_t shellName[256] = L"Command Prompt";
static void labelUtf8WithFont(IDWriteTextFormat* format, const uint8_t* bytes, size_t length, float x, float y, float width, uint32_t color) {
    if (bytes && length > 0 && length <= 256) {
        wchar_t text[257];
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, text, 256);
        if (count > 0) {
            text[count] = 0;
            labelWithFont(format, text, x, y, width, color);
        }
    }
}
static void labelUtf8OrDefault(const uint8_t* bytes, size_t length, float x, float y, float width, uint32_t color, IDWriteTextFormat* font = nullptr) {
    if (bytes && length > 0 && length <= 256) {
        wchar_t text[257];
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, text, 256);
        if (count > 0) {
            text[count] = 0;
            labelWithFont(font, text, x, y, width, color);
            return;
        }
    }
    labelWithFont(font, shellName, x, y, width, color);
}
extern "C" int vex_window_shell_name(const uint8_t* bytes, size_t length) {
    if (!bytes || !length || length >= sizeof(shellName) / sizeof(shellName[0]) || memchr(bytes, 0, length)) return ERROR_INVALID_PARAMETER;
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, shellName, (int)(sizeof(shellName) / sizeof(shellName[0]) - 1));
    if (!count) return (int)GetLastError();
    shellName[count] = 0;
    return 0;
}
extern "C" void vex_draw_tab_strip_background(uint32_t active_index) {
    if (active_index >= tabStripCount || tabStripCount > 8) return;
    const bool animate = chromeMotionEnabled && previousTabWidth == tabWidthDip;
    previousTabWidth = tabWidthDip;
    for (uint32_t i = 0; i < 8; ++i) {
        const float opacity = tabHoverMotion[i].update(i < tabStripCount && (int)i == hoveredTabIndex ? 1.0f : 0.0f, chromeTimeMs, animate);
        chromeMotionPending |= tabHoverMotion[i].pending(chromeTimeMs);
        if (i >= tabStripCount || opacity <= 0) continue;
        const float x = tabStripLeft + i * (tabWidthDip + 4);
        brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg, opacity));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 5, x + tabWidthDip, 39), 10, 10), brush);
    }
    // Closing a tab can also change slot widths; keep the selection continuous in both dimensions.
    const float x = tabSelectionMotion.update(tabStripLeft + active_index * (tabWidthDip + 4), chromeTimeMs, chromeMotionEnabled);
    const float width = tabSelectionWidthMotion.update(tabWidthDip, chromeTimeMs, chromeMotionEnabled);
    chromeMotionPending |= tabSelectionMotion.pending(chromeTimeMs) || tabSelectionWidthMotion.pending(chromeTimeMs);
    const D2D1_ROUNDED_RECT activeRect = D2D1::RoundedRect(D2D1::RectF(x, 5, x + width, 39), 10, 10);
    brush->SetColor(rgb(chromePalette.active_tab_bg));
    target->FillRoundedRectangle(activeRect, brush);
    brush->SetColor(rgb(blendRgb(chromePalette.border, chromePalette.active_tab_border, 0.42f), chromePalette.is_dark ? 0.55f : 0.40f));
    target->DrawRoundedRectangle(activeRect, brush, 1.0f);
}

extern "C" void vex_draw_tab(uint32_t index, uint32_t count, bool active, VexTabEdit edit, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention) {
    if (count != tabStripCount || index >= count) return;
    float width = tabWidthDip, x = tabStripLeft + index * (width + 4);
    if (active) activeTabIconCount = icon_count;
    const bool isHovered = (int)index == hoveredTabIndex;
    if (active && !edit.editing) tabRenameLayout.clear();
    const float editT = index < 8
        ? tabEditMotion[index].update(edit.editing ? 1.0f : 0.0f, chromeTimeMs, chromeMotionEnabled && edit.editing)
        : (edit.editing ? 1.0f : 0.0f);
    if (index < 8) chromeEffectsPending |= tabEditMotion[index].pending(chromeTimeMs);
    float inset = 8.0f;
    if (width >= 68.0f && icons && icon_count > 0) {
        const size_t maxVisible = edit.editing ? 1 : (width >= 120.0f ? 3 : 1);
        const size_t visible = icon_count > maxVisible ? maxVisible : icon_count;
        const uint32_t tabBg = active ? chromePalette.active_tab_bg : (isHovered ? chromePalette.inactive_tab_hover_bg : chromePalette.titlebar_bg);
        for (size_t i = 0; i < visible; i++) {
            const float tx = x + 7.0f + (float)i * 10.0f;
            const float ty = 13.0f;
            if (i > 0) {
                brush->SetColor(rgb(tabBg));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(tx - 1.0f, ty - 1.0f, tx + 19.0f, ty + 19.0f), 4.5f, 4.5f), brush);
            }
            const uint64_t iconKey = drawAppIconTile(icons[i], tx, ty);
            if (active && i == 0) activeTabIconKey = iconKey;
        }
        inset = 7.0f + 18.0f + (float)(visible - 1) * 10.0f + 5.0f;
        if (!edit.editing && icon_count > visible && width >= 132.0f) {
            const float bx = x + inset - 2.0f;
            const float by = 14.0f;
            brush->SetColor(rgb(chromePalette.pane_header_bg));
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, by, bx + 18.0f, by + 16.0f), 3.5f, 3.5f), brush);
            wchar_t plusText[8];
            swprintf(plusText, 8, L"+%u", (unsigned)(icon_count - visible));
            labelWithFont(badgeFont, plusText, bx + 2.5f, by + 1.0f, 16.0f, chromePalette.text_muted);
            inset += 20.0f;
        }
    }
    const bool hasStatus = !edit.editing && status.state >= 2 && status.state <= 5;
    float rightReserve = 24.0f;
    if (hasStatus && width >= 88.0f) {
        drawProgramStatus(status, x + width - 36.0f, 13.0f, 14.0f, true);
        rightReserve = 38.0f;
    } else if (!edit.editing && !active && attention) {
        brush->SetColor(rgb(chromePalette.accent));
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + width - 28.0f, 22.0f), 2.5f, 2.5f), brush);
        rightReserve = 34.0f;
    }
    if (edit.editing && width >= 56.0f) {
        const float textW = fmaxf(0.0f, width - inset - rightReserve);
        if (textW > 8.0f) {
            const float boxLeft = x + inset - 4.0f;
            const float boxRight = x + inset + textW + 2.0f;
            const float boxTop = 9.5f, boxBottom = 34.5f;
            const D2D1_ROUNDED_RECT glowBox = D2D1::RoundedRect(D2D1::RectF(boxLeft - 1.0f, boxTop - 1.0f, boxRight + 1.0f, boxBottom + 1.0f), 7.0f, 7.0f);
            brush->SetColor(rgb(chromePalette.accent, (chromePalette.is_dark ? 0.24f : 0.16f) * fmaxf(0.35f, editT)));
            target->DrawRoundedRectangle(glowBox, brush, 2.0f);

            const D2D1_ROUNDED_RECT editBox = D2D1::RoundedRect(D2D1::RectF(boxLeft, boxTop, boxRight, boxBottom), 6.0f, 6.0f);
            const uint32_t fieldBg = blendRgb(chromePalette.window_bg, chromePalette.pane_bg, chromePalette.is_dark ? 0.35f : 0.55f);
            brush->SetColor(rgb(fieldBg));
            target->FillRoundedRectangle(editBox, brush);
            brush->SetColor(rgb(blendRgb(chromePalette.active_tab_border, chromePalette.accent, 0.85f), chromePalette.is_dark ? 0.92f : 0.85f));
            target->DrawRoundedRectangle(editBox, brush, 1.15f);

            const float innerLeft = boxLeft + 6.0f;
            const float innerRight = boxRight - 6.0f;
            const float innerW = fmaxf(0.0f, innerRight - innerLeft);
            if (innerW > 2.0f) {
                target->PushAxisAlignedClip(D2D1::RectF(boxLeft + 2.0f, boxTop + 1.0f, boxRight - 2.0f, boxBottom - 1.0f), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                wchar_t text[257];
                const int count = (title && title_length > 0 && title_length <= 256)
                    ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(title), (int)title_length, text, 256)
                    : 0;
                if (count > 0) {
                    text[count] = 0;
                    const size_t cursorBytes = edit.cursor <= title_length ? edit.cursor : title_length;
                    const int cursorWide = cursorBytes > 0
                        ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(title), (int)cursorBytes, nullptr, 0)
                        : 0;
                    float textAdv = innerW;
                    float caretX = innerW;
                    if (textFactory && chromeFont) {
                        if (IDWriteTextLayout* layout = tabRenameLayout.get(textFactory, chromeFont, text, (UINT32)count)) {
                            DWRITE_TEXT_METRICS metrics = {};
                            if (SUCCEEDED(layout->GetMetrics(&metrics))) {
                                textAdv = metrics.widthIncludingTrailingWhitespace;
                            }
                            FLOAT hx = 0.0f, hy = 0.0f;
                            DWRITE_HIT_TEST_METRICS htm = {};
                            const UINT32 targetPos = (UINT32)(cursorWide >= 0 && cursorWide <= count ? cursorWide : count);
                            if (SUCCEEDED(layout->HitTestTextPosition(targetPos, FALSE, &hx, &hy, &htm))) {
                                caretX = hx;
                            } else {
                                caretX = textAdv;
                            }
                        }
                    }
                    if (edit.select_all) {
                        const float selW = fminf(innerW, fmaxf(4.0f, textAdv));
                        const D2D1_ROUNDED_RECT selPill = D2D1::RoundedRect(D2D1::RectF(innerLeft - 2.0f, 12.5f, innerLeft + selW + 2.0f, 31.5f), 3.5f, 3.5f);
                        const uint32_t selFill = blendRgb(fieldBg, chromePalette.accent, chromePalette.is_dark ? 0.38f : 0.22f);
                        brush->SetColor(rgb(selFill));
                        target->FillRoundedRectangle(selPill, brush);
                        brush->SetColor(rgb(chromePalette.accent, chromePalette.is_dark ? 0.52f : 0.34f));
                        target->DrawRoundedRectangle(selPill, brush, 1.0f);
                        label(text, innerLeft, 13.0f, innerW, chromePalette.text_primary);
                    } else {
                        const float scrollX = caretX > innerW - 2.0f ? (caretX - (innerW - 2.0f)) : 0.0f;
                        const float drawW = fmaxf(innerW + scrollX + 8.0f, textAdv + 8.0f);
                        label(text, innerLeft - scrollX, 13.0f, drawW, chromePalette.text_primary);
                        const float cx = fminf(innerRight - 0.5f, fmaxf(innerLeft, innerLeft + caretX - scrollX));
                        brush->SetColor(rgb(chromePalette.accent));
                        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 0.75f, 14.0f, cx + 0.75f, 30.0f), 0.75f, 0.75f), brush);
                    }
                } else {
                    brush->SetColor(rgb(chromePalette.accent));
                    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(innerLeft - 0.75f, 14.0f, innerLeft + 0.75f, 30.0f), 0.75f, 0.75f), brush);
                    const float placeholderW = fmaxf(0.0f, innerW - 4.0f);
                    if (placeholderW > 4.0f) {
                        wchar_t placeholder[257];
                        const int pCount = (edit.placeholder && edit.placeholder_length > 0 && edit.placeholder_length <= 256)
                            ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(edit.placeholder), (int)edit.placeholder_length, placeholder, 256)
                            : 0;
                        if (pCount > 0) {
                            placeholder[pCount] = 0;
                            labelWithFont(chromeFont, placeholder, innerLeft + 3.5f, 13.0f, placeholderW, chromePalette.text_muted, 0.58f);
                        } else {
                            labelWithFont(chromeFont, shellName, innerLeft + 3.5f, 13.0f, placeholderW, chromePalette.text_muted, 0.58f);
                        }
                    }
                }
                target->PopAxisAlignedClip();
            }
        }
    } else if (width >= 95.0f) {
        const float textW = fmaxf(0.0f, width - inset - rightReserve);
        labelUtf8OrDefault(title, title_length, x + inset, 13.0f, textW, active ? chromePalette.text_primary : chromePalette.text_muted);
    } else if (width < 68.0f) {
        wchar_t number[12]; swprintf(number, 12, L"%u", index + 1);
        label(number, x + 8.0f, 13.0f, fmaxf(0.0f, width - 22.0f), active ? chromePalette.text_primary : chromePalette.text_muted);
    }
    const float closeX = x + width - 21.0f, closeY = 14.0f;
    const bool closeHot = isHovered && hoveredTabClose;
    const float closeAlpha = index < 8
        ? tabCloseHoverMotion[index].update(closeHot ? 1.0f : 0.0f, chromeTimeMs, chromeMotionEnabled)
        : (closeHot ? 1.0f : 0.0f);
    if (index < 8) chromeEffectsPending |= tabCloseHoverMotion[index].pending(chromeTimeMs);
    if (closeAlpha > 0.0f) {
        brush->SetColor(rgb(chromePalette.border, closeAlpha));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(closeX, closeY, closeX + 16.0f, closeY + 16.0f), 4.0f, 4.0f), brush);
    }
    const float cx = closeX + 8.0f, cy = closeY + 8.0f;
    brush->SetColor(rgb(blendRgb(chromePalette.text_muted, chromePalette.text_primary, closeAlpha)));
    target->DrawLine(D2D1::Point2F(cx - 3.0f, cy - 3.0f), D2D1::Point2F(cx + 3.0f, cy + 3.0f), brush, 1.2f, roundCaps);
    target->DrawLine(D2D1::Point2F(cx + 3.0f, cy - 3.0f), D2D1::Point2F(cx - 3.0f, cy + 3.0f), brush, 1.2f, roundCaps);
}
static void drawPaneHeaderActions(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action) {
    if (bounds.width < 180.0f || bounds.height < 28.0f) return;
    const float right = bounds.x + bounds.width - 6.0f;
    const float top = bounds.y + 3.5f;
    const uint32_t baseColor = (focused || hovered) ? chromePalette.text_muted : chromePalette.border;
    float actionAlpha[4] = {};
    for (uint8_t i = 0; i < 4; ++i) {
        const float targetAlpha = (hovered && hovered_action == i + 1) ? 1.0f : 0.0f;
        actionAlpha[i] = hovered
            ? paneActionHoverMotion[i].update(targetAlpha, chromeTimeMs, chromeMotionEnabled)
            : targetAlpha;
        if (hovered) chromeEffectsPending |= paneActionHoverMotion[i].pending(chromeTimeMs);
    }
    const auto drawButtonPill = [&](float bx, uint32_t fill, float alpha = 1.0f) {
        if (alpha <= 0.0f) return;
        brush->SetColor(rgb(fill, alpha));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, top, bx + 20.0f, top + 19.0f), 5.0f, 5.0f), brush);
    };
    if (can_focus) {
        const float bx = right - 86.0f, cx = bx + 10.0f, cy = top + 9.5f;
        if (zoomed) drawButtonPill(bx, chromePalette.active_tab_bg, 1.0f);
        else if (actionAlpha[0] > 0.0f) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg, actionAlpha[0]);
        brush->SetColor(rgb(zoomed ? chromePalette.accent : blendRgb(baseColor, chromePalette.text_primary, actionAlpha[0])));
        if (!zoomed) {
            target->DrawLine(D2D1::Point2F(cx + 1.0f, cy - 1.0f), D2D1::Point2F(cx + 4.5f, cy - 4.5f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx + 1.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy - 4.5f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy - 1.5f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 1.0f, cy + 1.0f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 1.5f, cy + 4.5f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 4.5f, cy + 4.5f), D2D1::Point2F(cx - 4.5f, cy + 1.5f), brush, 1.2f, roundCaps);
        } else {
            target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx + 1.0f, cy - 1.0f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx + 1.0f, cy - 4.0f), D2D1::Point2F(cx + 1.0f, cy - 1.0f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx + 1.0f, cy - 1.0f), D2D1::Point2F(cx + 4.0f, cy - 1.0f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 4.5f, cy + 4.5f), D2D1::Point2F(cx - 1.0f, cy + 1.0f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 1.0f, cy + 4.0f), D2D1::Point2F(cx - 1.0f, cy + 1.0f), brush, 1.2f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 1.0f, cy + 1.0f), D2D1::Point2F(cx - 4.0f, cy + 1.0f), brush, 1.2f, roundCaps);
        }
    }
    {
        const float bx = right - 64.0f, cx = bx + 10.0f, cy = top + 9.5f;
        drawButtonPill(bx, chromePalette.inactive_tab_hover_bg, actionAlpha[1]);
        brush->SetColor(rgb(blendRgb(baseColor, chromePalette.text_primary, actionAlpha[1])));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 6.0f, cy - 6.0f, cx + 6.0f, cy + 6.0f), 1.8f, 1.8f), brush, 1.4f);
        target->DrawLine(D2D1::Point2F(cx, cy - 6.0f), D2D1::Point2F(cx, cy + 6.0f), brush, 1.4f, roundCaps);
    }
    {
        const float bx = right - 42.0f, cx = bx + 10.0f, cy = top + 9.5f;
        drawButtonPill(bx, chromePalette.inactive_tab_hover_bg, actionAlpha[2]);
        brush->SetColor(rgb(blendRgb(baseColor, chromePalette.text_primary, actionAlpha[2])));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 6.0f, cy - 6.0f, cx + 6.0f, cy + 6.0f), 1.8f, 1.8f), brush, 1.4f);
        target->DrawLine(D2D1::Point2F(cx - 6.0f, cy), D2D1::Point2F(cx + 6.0f, cy), brush, 1.4f, roundCaps);
    }
    {
        const float bx = right - 20.0f, cx = bx + 10.0f, cy = top + 9.5f;
        drawButtonPill(bx, chromePalette.is_dark ? 0x4d2b31 : 0xfad2d8, actionAlpha[3]);
        brush->SetColor(rgb(blendRgb(baseColor, chromePalette.is_dark ? 0xffb4be : 0xb42336, actionAlpha[3])));
        target->DrawLine(D2D1::Point2F(cx - 3.5f, cy - 3.5f), D2D1::Point2F(cx + 3.5f, cy + 3.5f), brush, 1.25f, roundCaps);
        target->DrawLine(D2D1::Point2F(cx + 3.5f, cy - 3.5f), D2D1::Point2F(cx - 3.5f, cy + 3.5f), brush, 1.25f, roundCaps);
    }
}
extern "C" void vex_begin_pane(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action, const uint8_t* title, size_t title_length, VexAppIcon icon, VexProgramStatus status) {
    const D2D1_ROUNDED_RECT paneCard = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), 6.0f, 6.0f);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(paneCard, brush);
    if (bounds.height >= 28.0f) {
        brush->SetColor(rgb(chromePalette.pane_header_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + 26.0f), 6.0f, 6.0f), brush);
        rectangle(bounds.x, bounds.y + 18.0f, bounds.width, 8.0f, chromePalette.pane_header_bg);
        brush->SetColor(rgb(chromePalette.border, chromePalette.is_dark ? 0.38f : 0.45f));
        target->FillRectangle(D2D1::RectF(bounds.x + 1.0f, bounds.y + 26.0f, bounds.x + bounds.width - 1.0f, bounds.y + 27.0f), brush);
    }
    const uint32_t borderCol = (can_focus && focused)
        ? blendRgb(chromePalette.border, chromePalette.active_tab_border, 0.55f)
        : chromePalette.border;
    brush->SetColor(rgb(borderCol, (can_focus && focused) ? 0.82f : 0.48f));
    target->DrawRoundedRectangle(paneCard, brush, 1.0f);
    drawPaneHeaderActions(bounds, focused, hovered, can_focus, zoomed, hovered_action);
    const float actionsWidth = (bounds.width >= 180.0f && bounds.height >= 28.0f) ? (can_focus ? 94.0f : 72.0f) : 0.0f;
    const float remainingWidth = fmaxf(0, bounds.width - actionsWidth);
    const float statusWidth = status.state >= 2 && status.state <= 5 && remainingWidth >= 90 ? 20.0f : 0.0f;
    float titleInset = 10.0f;
    if (bounds.width >= 90.0f && bounds.height >= 28.0f) {
        drawAppIcon(icon, bounds.x + 11.0f, bounds.y + 7.0f, 12.0f);
        if (focused) focusedPaneIconKey = computeIconCacheKey(icon, 12);
        titleInset = 30.0f;
    }
    const float titleWidth = fmaxf(0, remainingWidth - titleInset - statusWidth - 12);
    labelUtf8OrDefault(title, title_length, bounds.x + titleInset, bounds.y + 4.0f, titleWidth, focused ? chromePalette.text_primary : chromePalette.text_muted, paneTitleFont);
    if (statusWidth > 0 && titleWidth > 0) {
        wchar_t text[257];
        int count = title && title_length > 0 && title_length <= 256
            ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(title), (int)title_length, text, 256) : 0;
        const wchar_t* caption = count > 0 ? text : shellName;
        const UINT32 units = count > 0 ? (UINT32)count : (UINT32)wcslen(shellName);
        float captionWidth = titleWidth;
        IDWriteTextLayout* layout = nullptr;
        if (SUCCEEDED(textFactory->CreateTextLayout(caption, units, paneTitleFont, titleWidth, 24, &layout))) {
            DWRITE_TEXT_METRICS metrics = {};
            if (SUCCEEDED(layout->GetMetrics(&metrics))) captionWidth = fminf(titleWidth, metrics.width);
            layout->Release();
        }
        // Follow short titles closely, but reserve the indicator's space when text is clipped.
        drawProgramStatus(status, bounds.x + titleInset + captionWidth + 4, bounds.y + 4, statusWidth, true);
    }
    beginTerminalPane(bounds);
}
extern "C" void vex_draw_scrollbar(VexPaneRect bounds, float thumb_y, float thumb_height, bool active, float opacity) {
    if (!isfinite(thumb_y) || !isfinite(thumb_height) || !isfinite(opacity) || opacity <= 0.0f || thumb_height <= 0.0f || bounds.width < 24.0f) return;
    const float width = active ? 5.0f : 3.0f;
    const float right = bounds.x + bounds.width - 4.0f;
    const float left = right - width;
    const float radius = width * 0.5f;
    // A translucent thumb borrows the pane's palette without painting a separate rail.
    brush->SetColor(rgb(chromePalette.text_primary, (active ? 0.38f : 0.22f) * fminf(opacity, 1.0f)));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(left, thumb_y, right, thumb_y + thumb_height), radius, radius), brush);
}
extern "C" void vex_draw_copy_feedback(VexPaneRect bounds, float cell_width, float cell_height, const VexCopySpan* spans, size_t span_count, float progress) {
    if (!target || !brush || !spans || span_count == 0 || !isfinite(progress) || progress < 0.0f || progress >= 1.0f) return;
    if (!isfinite(cell_width) || !isfinite(cell_height) || cell_width <= 0.0f || cell_height <= 0.0f) return;
    if (chromeMotionEnabled) chromeEffectsPending = true;

    const float gridLeft = bounds.x + 6.0f;
    const float gridTop = bounds.y + 30.0f;
    const float clipRight = bounds.x + bounds.width - 14.0f;
    const float clipBottom = bounds.y + bounds.height - 4.0f;
    if (clipRight <= gridLeft || clipBottom <= gridTop) return;

    target->PushAxisAlignedClip(D2D1::RectF(bounds.x + 3.0f, bounds.y + 28.0f, clipRight, clipBottom), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    float minX = 1e9f, maxX = -1e9f;
    float minY = 1e9f, maxY = -1e9f;
    const size_t count = span_count;
    for (size_t i = 0; i < count; ++i) {
        if (spans[i].end_col <= spans[i].start_col) continue;
        const float x1 = gridLeft + (float)spans[i].start_col * cell_width;
        const float x2 = gridLeft + (float)spans[i].end_col * cell_width;
        if (x1 < minX) minX = x1;
        if (x2 > maxX) maxX = x2;
        const float y1 = gridTop + (float)spans[i].row * cell_height;
        if (y1 < minY) minY = y1;
        if (y1 + cell_height > maxY) maxY = y1 + cell_height;
    }
    if (maxX > minX && maxY > minY) {
        const float spanW = fmaxf(cell_width * 2.0f, maxX - minX);
        const float bandHalfW = fmaxf(28.0f, fminf(120.0f, spanW * 0.34f));
        const float sweepT = 1.0f - powf(1.0f - progress, 2.25f);
        const float bandCenter = (minX - bandHalfW * 0.75f) + (spanW + bandHalfW * 1.5f) * sweepT;
        const float env = (1.0f - progress) * (1.0f - progress);
        const float shimmerEnv = sinf(progress * 3.14159265f) * (1.0f - progress * 0.30f);
        const uint32_t shimmerGlow = blendRgb(chromePalette.accent, 0xffffff, chromePalette.is_dark ? 0.38f : 0.25f);
        const uint32_t shimmerCore = blendRgb(chromePalette.accent, 0xffffff, chromePalette.is_dark ? 0.78f : 0.58f);

        // Share one sweep, but never fill blank rows or the padding after short lines.
        // Equal adjacent extents coalesce without allocating geometry every frame.
        for (size_t i = 0; i < count; ++i) {
            const auto& span = spans[i];
            if (span.end_col <= span.start_col) continue;
            size_t last = i;
            while (last + 1 < count && spans[last + 1].row == spans[last].row + 1 &&
                spans[last + 1].start_col == span.start_col && spans[last + 1].end_col == span.end_col) ++last;
            const float sx1 = gridLeft + (float)span.start_col * cell_width - 1.5f;
            const float sx2 = gridLeft + (float)span.end_col * cell_width + 1.5f;
            const float sy1 = gridTop + (float)span.row * cell_height - 0.5f;
            const float sy2 = gridTop + (float)(spans[last].row + 1) * cell_height + 0.5f;
            i = last;

            const D2D1_ROUNDED_RECT pill = D2D1::RoundedRect(D2D1::RectF(sx1, sy1, sx2, sy2), 3.5f, 3.5f);
            brush->SetColor(rgb(chromePalette.accent, (chromePalette.is_dark ? 0.18f : 0.13f) * env));
            target->FillRoundedRectangle(pill, brush);

            const auto drawWaveSlice = [&](float halfW, uint32_t col, float peakAlpha) {
                const float wx1 = fmaxf(sx1, bandCenter - halfW);
                const float wx2 = fminf(sx2, bandCenter + halfW);
                if (wx2 > wx1 + 0.5f) {
                    brush->SetColor(rgb(col, peakAlpha * shimmerEnv));
                    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(wx1, sy1, wx2, sy2), 3.5f, 3.5f), brush);
                }
            };
            drawWaveSlice(bandHalfW, shimmerGlow, chromePalette.is_dark ? 0.20f : 0.15f);
            drawWaveSlice(bandHalfW * 0.56f, shimmerCore, chromePalette.is_dark ? 0.26f : 0.20f);
            drawWaveSlice(bandHalfW * 0.24f, 0xffffff, chromePalette.is_dark ? 0.34f : 0.25f);

            brush->SetColor(rgb(shimmerGlow, (chromePalette.is_dark ? 0.60f : 0.46f) * env));
            target->DrawRoundedRectangle(pill, brush, 1.1f);
        }
    }
    target->PopAxisAlignedClip();
}
extern "C" void vex_draw_tab_overview(bool animating) {
    // Share monitor-paced scheduling with tab chrome; settled previews request no frames.
    chromeMotionPending = chromeMotionPending || animating;
    const auto size = target->GetSize();
    rectangle(0, 44, size.width, fmaxf(0, size.height - 44), chromePalette.titlebar_bg);
    label(L"Arrows / Tab to choose    Home / End    Enter to switch    Esc to return", 28, size.height - 54, size.width - 56, chromePalette.text_muted);
    label(L"Del: close tab    Ctrl+Shift+W: close active pane", 28, size.height - 32, size.width - 56, chromePalette.text_muted);
}
extern "C" void vex_draw_preview_close(VexPaneRect bounds, bool hovered) {
    if (hovered) {
        brush->SetColor(rgb(chromePalette.is_dark ? 0x4d2b31 : 0xfad2d8));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x + 2.0f, bounds.y + 2.0f, bounds.x + bounds.width - 2.0f, bounds.y + bounds.height - 2.0f), 5.0f, 5.0f), brush);
    }
    brush->SetColor(rgb(hovered ? (chromePalette.is_dark ? 0xffb4be : 0xb42336) : chromePalette.text_muted));
    const float x = bounds.x + bounds.width * 0.5f, y = bounds.y + bounds.height * 0.5f;
    target->DrawLine(D2D1::Point2F(x - 3.5f, y - 3.5f), D2D1::Point2F(x + 3.5f, y + 3.5f), brush, 1.4f, roundCaps);
    target->DrawLine(D2D1::Point2F(x + 3.5f, y - 3.5f), D2D1::Point2F(x - 3.5f, y + 3.5f), brush, 1.4f, roundCaps);
}
extern "C" void vex_draw_tab_preview_card(VexPaneRect bounds, const uint8_t* title, size_t length, bool selected) {
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? (selected ? 0.42f : 0.26f) : (selected ? 0.16f : 0.09f)));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x - 2.0f, bounds.y + 3.0f, bounds.x + bounds.width + 2.0f, bounds.y + bounds.height + 5.0f), 9.0f, 9.0f), brush);
    const auto card = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), 7, 7);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(card, brush);
    if (bounds.height >= 28.0f) {
        brush->SetColor(rgb(chromePalette.pane_header_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + 26.0f), 7.0f, 7.0f), brush);
        rectangle(bounds.x, bounds.y + 18.0f, bounds.width, 8.0f, chromePalette.pane_header_bg);
        brush->SetColor(rgb(chromePalette.border, 0.40f));
        target->FillRectangle(D2D1::RectF(bounds.x + 1.0f, bounds.y + 26.0f, bounds.x + bounds.width - 1.0f, bounds.y + 27.0f), brush);
    }
    if (selected) {
        brush->SetColor(rgb(chromePalette.accent, 0.25f));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x - 1.5f, bounds.y - 1.5f, bounds.x + bounds.width + 1.5f, bounds.y + bounds.height + 1.5f), 8.5f, 8.5f), brush, 2.5f);
    }
    brush->SetColor(rgb(selected ? chromePalette.accent : chromePalette.border));
    target->DrawRoundedRectangle(card, brush, selected ? 2.0f : 1.0f);
    labelUtf8OrDefault(title, length, bounds.x + 10, bounds.y + 4, fmaxf(0, bounds.width - 44), selected ? chromePalette.text_primary : chromePalette.text_muted, paneTitleFont);
}
extern "C" void vex_draw_split_preview(VexPaneRect bounds) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    splitPreviewSeenThisFrame = true;
    const bool animate = chromeMotionEnabled && splitPreviewSeenLastFrame;
    if (!splitPreviewSeenLastFrame) {
        splitPreviewAlphaMotion = {};
        splitPreviewAlphaMotion.update(0.0f, chromeTimeMs, false);
    }
    const float alpha = splitPreviewAlphaMotion.update(1.0f, chromeTimeMs, chromeMotionEnabled);
    const float rx = splitPreviewRectMotion[0].update(bounds.x, chromeTimeMs, animate);
    const float ry = splitPreviewRectMotion[1].update(bounds.y, chromeTimeMs, animate);
    const float rw = splitPreviewRectMotion[2].update(bounds.width, chromeTimeMs, animate);
    const float rh = splitPreviewRectMotion[3].update(bounds.height, chromeTimeMs, animate);
    chromeEffectsPending |= splitPreviewAlphaMotion.pending(chromeTimeMs);
    for (int i = 0; i < 4; ++i) chromeEffectsPending |= splitPreviewRectMotion[i].pending(chromeTimeMs);

    const D2D1_ROUNDED_RECT rect = D2D1::RoundedRect(D2D1::RectF(rx, ry, rx + rw, ry + rh), 6.0f, 6.0f);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f * alpha));
    target->FillRoundedRectangle(rect, brush);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.78f * alpha));
    target->DrawRoundedRectangle(rect, brush, 1.5f);
}
extern "C" void vex_draw_path_completion(VexPaneRect bounds, const uint8_t* directory, size_t directory_length, const uint8_t* query, size_t query_length, const VexPathCompletionItem* items, size_t item_count, uint32_t flags) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    pathCompletionSeenThisFrame = true;
    if (!pathCompletionSeenLastFrame) {
        pathOpenMotion = {};
        pathOpenMotion.update(0.0f, chromeTimeMs, false);
    }
    const float openT = pathOpenMotion.update(1.0f, chromeTimeMs, chromeMotionEnabled);
    chromeEffectsPending |= pathOpenMotion.pending(chromeTimeMs);
    const float slideY = (1.0f - openT) * 5.0f;
    const float mx = bounds.x, my = bounds.y + slideY, mw = bounds.width, mh = bounds.height;

    for (int layer = 3; layer >= 1; --layer) {
        const float sp = (float)layer * 3.0f;
        brush->SetColor(D2D1::ColorF(0x000000, (chromePalette.is_dark ? 0.16f : 0.07f) * openT));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - sp, my + 2.0f - sp * 0.5f, mx + mw + sp, my + mh + 4.0f + sp), 8.0f + sp, 8.0f + sp), brush);
    }
    const D2D1_ROUNDED_RECT card = D2D1::RoundedRect(D2D1::RectF(mx, my, mx + mw, my + mh), 8.0f, 8.0f);
    brush->SetColor(rgb(chromePalette.titlebar_bg));
    target->FillRoundedRectangle(card, brush);
    brush->SetColor(rgb(blendRgb(chromePalette.border, chromePalette.active_tab_border, 0.65f)));
    target->DrawRoundedRectangle(card, brush, 1.2f);

    target->PushAxisAlignedClip(D2D1::RectF(mx + 4.0f, my + 2.0f, mx + mw - 4.0f, my + mh - 2.0f), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    if (directory && directory_length > 0) {
        wchar_t dirBuf[260];
        const int wlen = MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<const char*>(directory), (int)directory_length, dirBuf, 259);
        if (wlen > 0) {
            dirBuf[wlen] = L'\0';
            const wchar_t* displayDir = wlen > 44 ? (dirBuf + (wlen - 44)) : dirBuf;
            labelWithFont(badgeFont, displayDir, mx + 10.0f, my + 6.0f, mw - 20.0f, chromePalette.text_muted);
        }
    }

    const D2D1_ROUNDED_RECT searchBox = D2D1::RoundedRect(D2D1::RectF(mx + 8.0f, my + 24.0f, mx + mw - 8.0f, my + 50.0f), 6.0f, 6.0f);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(searchBox, brush);
    brush->SetColor(rgb(chromePalette.accent, 0.85f));
    target->DrawRoundedRectangle(searchBox, brush, 1.1f);
    brush->SetColor(rgb(chromePalette.active_tab_bg));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 12.0f, my + 28.0f, mx + 28.0f, my + 46.0f), 4.0f, 4.0f), brush);
    labelWithFont(chromeBoldFont, L"/", mx + 17.5f, my + 28.0f, 12.0f, chromePalette.accent);
    if (query_length == 0 || !query) {
        labelWithFont(badgeFont, L"Find a file or folder...", mx + 34.0f, my + 30.0f, mw - 46.0f, chromePalette.text_muted);
    } else {
        labelUtf8OrDefault(query, query_length, mx + 34.0f, my + 28.0f, mw - 46.0f, chromePalette.text_primary, chromeBoldFont);
    }

    const float listY = my + 56.0f, rowH = 26.0f;
    const size_t visible = item_count > 8 ? 8 : item_count;
    if (visible == 0 || !items) {
        labelWithFont(badgeFont, (flags & 2) ? L"Indexing folder... You can keep typing" : (flags & 1) ? L"Unable to read this folder" : L"No matching paths", mx + 14.0f, listY + 5.0f, mw - 28.0f, chromePalette.text_muted);
    } else {
        int selectedIdx = -1;
        for (size_t i = 0; i < visible; i++) {
            if (items[i].selected) { selectedIdx = (int)i; break; }
        }
        if (selectedIdx >= 0) {
            const float targetRowY = (float)selectedIdx * rowH;
            if (!pathCompletionSeenLastFrame) {
                pathRowMotion = {};
                pathRowMotion.update(targetRowY, chromeTimeMs, false);
            }
            const float animRowY = listY + pathRowMotion.update(targetRowY, chromeTimeMs, chromeMotionEnabled);
            chromeEffectsPending |= pathRowMotion.pending(chromeTimeMs);
            const D2D1_ROUNDED_RECT selRect = D2D1::RoundedRect(D2D1::RectF(mx + 6.0f, animRowY, mx + mw - 6.0f, animRowY + rowH - 2.0f), 5.0f, 5.0f);
            const uint32_t selFill = blendRgb(chromePalette.active_tab_bg, chromePalette.accent, chromePalette.is_dark ? 0.28f : 0.16f);
            brush->SetColor(rgb(selFill));
            target->FillRoundedRectangle(selRect, brush);
            brush->SetColor(rgb(chromePalette.accent, chromePalette.is_dark ? 0.50f : 0.38f));
            target->DrawRoundedRectangle(selRect, brush, 1.0f);
            brush->SetColor(rgb(chromePalette.accent));
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 8.5f, animRowY + 5.0f, mx + 11.5f, animRowY + rowH - 7.0f), 1.5f, 1.5f), brush);
        }
        for (size_t i = 0; i < visible; i++) {
            const VexPathCompletionItem& it = items[i];
            const float iy = listY + (float)i * rowH;
            if (it.hovered && !it.selected) {
                const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 6.0f, iy, mx + mw - 6.0f, iy + rowH - 2.0f), 5.0f, 5.0f);
                brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg));
                target->FillRoundedRectangle(rowRect, brush);
            }
            const float ix = mx + 16.0f;
            if (it.is_directory) {
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix, iy + 6.0f, ix + 5.5f, iy + 9.0f), 1.0f, 1.0f), brush);
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix, iy + 8.0f, ix + 12.5f, iy + 17.5f), 1.5f, 1.5f), brush);
            } else {
                brush->SetColor(rgb(it.selected ? chromePalette.text_primary : chromePalette.text_muted));
                target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix + 1.5f, iy + 5.5f, ix + 11.0f, iy + 17.5f), 1.2f, 1.2f), brush, 1.1f);
            }
            labelUtf8OrDefault(it.name, it.name_length, mx + 33.0f, iy + 3.0f, mw - 68.0f, chromePalette.text_primary, it.selected ? chromeBoldFont : chromeFont);
            if (it.is_directory) {
                brush->SetColor(rgb(chromePalette.pane_bg, 0.85f));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + mw - 38.0f, iy + 4.0f, mx + mw - 10.0f, iy + 20.0f), 3.5f, 3.5f), brush);
                labelWithFont(badgeFont, L"DIR", mx + mw - 33.5f, iy + 5.0f, 24.0f, chromePalette.accent);
            }
        }
    }
    rectangle(mx + 8, my + mh - 30, mw - 16, 1, chromePalette.border);
    const wchar_t* hint = (flags & 4) ? L"Index limit reached; narrow the folder" : L"\x2190 Parent   Tab Open   Enter Insert   Esc Close";
    labelWithFont(badgeFont, hint, mx + 12, my + mh - 22, mw - 24, chromePalette.text_muted);
    target->PopAxisAlignedClip();
}
