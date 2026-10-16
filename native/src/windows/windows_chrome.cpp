#include "windows_chrome.h"
#include "drawing_color.h"
#include "ui_renderer.h"
#include "terminal_drawing.h"
#include <math.h>
#include <stdio.h>

namespace {
UiRenderer* target;
UiBrush paint;
UiBrush* brush = &paint;
IDWriteTextFormat* chromeFont;
IDWriteTextFormat* chromeBoldFont;
IDWriteTextFormat* badgeFont;
VexChromePalette chromePalette;
float tabWidthDip;
uint32_t tabStripCount;
int hoveredTabIndex;
bool hoveredTabClose;
uint64_t activeTabIconKey, focusedPaneIconKey;
size_t activeTabIconCount;
constexpr float tabStripLeft = 8;
constexpr bool roundCaps = true;
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
    target = frame.target;
    chromeFont = frame.normalFont;
    chromeBoldFont = frame.boldFont;
    badgeFont = frame.badgeFont;
    chromePalette = frame.palette;
    tabWidthDip = frame.tabWidth;
    tabStripCount = frame.tabCount;
    hoveredTabIndex = frame.hoveredTab;
    hoveredTabClose = frame.hoveredClose;
    activeTabIconKey = 0; focusedPaneIconKey = 0; activeTabIconCount = 0;
}
WindowChromeDiagnostics windowChromeDiagnostics() {
    return { activeTabIconKey, focusedPaneIconKey, activeTabIconCount };
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
        b->SetColor(rgb(icon.color_rgb ? icon.color_rgb : 0x5c6bc0));
        fillBox(1.5f, 1.5f, 22.5f, 22.5f, 5.0f);
        wchar_t wide[5] = {};
        uint8_t count = icon.badge_len > 2 ? 2 : icon.badge_len;
        for (uint8_t i = 0; i < count; i++) wide[i] = (wchar_t)icon.badge[i];
        b->SetColor(rgb(0xffffff));
        const float textLeft = count == 1 ? ox + sizeDip * 0.27f : ox + sizeDip * 0.12f;
        rt->DrawText(wide, count, badgeFont, D2D1::RectF(textLeft, oy + sizeDip * 0.05f, ox + sizeDip, oy + sizeDip), b, D2D1_DRAW_TEXT_OPTIONS_CLIP);
        return;
    }

    b->SetColor(rgb(icon.color_rgb ? icon.color_rgb : 0x8f9197));
    if (icon.kind == 0 || icon.glyph == 0) {
        line(4.5f, 7.0f, 10.5f, 12.0f);
        line(10.5f, 12.0f, 4.5f, 17.0f);
        line(12.0f, 17.0f, 19.5f, 17.0f);
    } else {
        rt->DrawAppIcon(icon.glyph, badgeFont, rc(0, 0, 24, 24), b);
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
    return chromePalette.text_muted;
}
static void drawProgramStatus(VexProgramStatus status, float x, float y, float width, bool iconOnly) {
    if (status.state < 1 || status.state > 5) return;
    const uint32_t color = programStatusColor(status);
    const uint32_t phase = status.phase % 3;
    if (iconOnly) {
        if (status.state == 2) {
            // Three-dot wave: the lit dot advances once per shared icon phase step.
            for (uint32_t i = 0; i < 3; ++i) {
                const bool lit = (phase + 3 - i) % 3 == 0;
                const float radius = lit ? 2.1f : 1.5f;
                brush->SetColor(rgb(color, lit ? 1.0f : 0.38f));
                target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + 2.5f + (float)i * 4.6f, y + 10.0f), radius, radius), brush);
            }
        } else if (status.state == 1) {
            brush->SetColor(rgb(color));
            target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(x + 6, y + 10), 2.5f, 2.5f), brush, 1);
        } else {
            // Blocked breathes for attention; done and error stay static.
            const float alpha = status.state == 4 ? (phase == 0 ? 1.0f : (phase == 1 ? 0.62f : 0.84f)) : 1.0f;
            labelWithFont(badgeFont, status.state == 3 ? L"\x2713" : status.state == 4 ? L"\x25C7" : L"!", x, y + 2, width, color, alpha);
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
static wchar_t shellName[64] = L"Command Prompt";
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
static void labelUtf8OrDefault(const uint8_t* bytes, size_t length, float x, float y, float width, uint32_t color) {
    if (bytes && length > 0 && length <= 256) {
        wchar_t text[257];
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, text, 256);
        if (count > 0) {
            text[count] = 0;
            label(text, x, y, width, color);
            return;
        }
    }
    label(shellName, x, y, width, color);
}
extern "C" int vex_window_shell_name(const uint8_t* bytes, size_t length) {
    if (!bytes || !length || length > 128 || memchr(bytes, 0, length)) return ERROR_INVALID_PARAMETER;
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, shellName, 63);
    if (!count) return (int)GetLastError();
    shellName[count] = 0;
    return 0;
}
extern "C" void vex_draw_tab(uint32_t index, uint32_t count, bool active, bool editing, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention) {
    if (count != tabStripCount || index >= count) return;
    float width = tabWidthDip, x = tabStripLeft + index * (width + 4);
    if (active) activeTabIconCount = icon_count;
    const bool isHovered = (int)index == hoveredTabIndex;
    if (active || isHovered) {
        brush->SetColor(rgb(active ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 5.0f, x + width, 39.0f), 10.0f, 10.0f), brush);
    }
    float inset = 8.0f;
    if (width >= 68.0f && icons && icon_count > 0) {
        const size_t maxVisible = width >= 120.0f ? 3 : 1;
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
        if (icon_count > visible && width >= 132.0f) {
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
    const bool hasStatus = status.state >= 2 && status.state <= 5 && (status.state != 2 || !active);
    float rightReserve = 24.0f;
    if (hasStatus && width >= 88.0f) {
        drawProgramStatus(status, x + width - 36.0f, 13.0f, 14.0f, true);
        rightReserve = 38.0f;
    } else if (!active && attention) {
        brush->SetColor(rgb(chromePalette.accent));
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + width - 28.0f, 22.0f), 2.5f, 2.5f), brush);
        rightReserve = 34.0f;
    }
    if (width >= 95.0f) {
        const float textW = fmaxf(0.0f, width - inset - rightReserve);
        if (editing && textW > 8.0f) {
            const D2D1_ROUNDED_RECT editBox = D2D1::RoundedRect(D2D1::RectF(x + inset - 3.0f, 10.5f, x + inset + textW + 2.0f, 33.5f), 3.5f, 3.5f);
            brush->SetColor(rgb(chromePalette.window_bg));
            target->FillRoundedRectangle(editBox, brush);
            brush->SetColor(rgb(chromePalette.accent));
            target->DrawRoundedRectangle(editBox, brush, 1.1f);
            if (title && title_length > 0 && title_length <= 256) {
                wchar_t text[257];
                int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(title), (int)title_length, text, 256);
                if (count > 0) {
                    text[count] = 0;
                    label(text, x + inset, 13.0f, textW, chromePalette.text_primary);
                }
            }
        } else {
            labelUtf8OrDefault(title, title_length, x + inset, 13.0f, textW, active ? chromePalette.text_primary : chromePalette.text_muted);
        }
    } else if (width < 68.0f) {
        wchar_t number[12]; swprintf(number, 12, L"%u", index + 1);
        label(number, x + 8.0f, 13.0f, fmaxf(0.0f, width - 22.0f), active ? chromePalette.text_primary : chromePalette.text_muted);
    }
    const float closeX = x + width - 21.0f, closeY = 14.0f;
    const bool closeHot = isHovered && hoveredTabClose;
    if (closeHot) {
        brush->SetColor(rgb(chromePalette.border));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(closeX, closeY, closeX + 16.0f, closeY + 16.0f), 4.0f, 4.0f), brush);
    }
    const float cx = closeX + 8.0f, cy = closeY + 8.0f;
    brush->SetColor(rgb(closeHot ? chromePalette.text_primary : chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(cx - 3.0f, cy - 3.0f), D2D1::Point2F(cx + 3.0f, cy + 3.0f), brush, 1.2f, roundCaps);
    target->DrawLine(D2D1::Point2F(cx + 3.0f, cy - 3.0f), D2D1::Point2F(cx - 3.0f, cy + 3.0f), brush, 1.2f, roundCaps);
}
static void drawPaneHeaderActions(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action) {
    if (bounds.width < 180.0f || bounds.height < 28.0f) return;
    const float right = bounds.x + bounds.width - 6.0f;
    const float top = bounds.y + 4.0f;
    const uint32_t baseColor = (focused || hovered) ? chromePalette.text_muted : chromePalette.border;
    const auto drawButtonPill = [&](float bx, uint32_t fill) {
        brush->SetColor(rgb(fill));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, top, bx + 20.0f, top + 20.0f), 5.0f, 5.0f), brush);
    };
    if (can_focus) {
        const float bx = right - 86.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (zoomed) drawButtonPill(bx, chromePalette.active_tab_bg);
        else if (hovered_action == 1) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg);
        brush->SetColor(rgb(zoomed ? chromePalette.accent : (hovered_action == 1 ? chromePalette.text_primary : baseColor)));
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
        const float bx = right - 64.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 2) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg);
        brush->SetColor(rgb(hovered_action == 2 ? chromePalette.text_primary : baseColor));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), 1.6f, 1.6f), brush, 1.15f);
        target->DrawLine(D2D1::Point2F(cx, cy - 4.5f), D2D1::Point2F(cx, cy + 4.5f), brush, 1.15f, roundCaps);
    }
    {
        const float bx = right - 42.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 3) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg);
        brush->SetColor(rgb(hovered_action == 3 ? chromePalette.text_primary : baseColor));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), 1.6f, 1.6f), brush, 1.15f);
        target->DrawLine(D2D1::Point2F(cx - 4.5f, cy), D2D1::Point2F(cx + 4.5f, cy), brush, 1.15f, roundCaps);
    }
    {
        const float bx = right - 20.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 4) drawButtonPill(bx, chromePalette.is_dark ? 0x4d2b31 : 0xfad2d8);
        brush->SetColor(rgb(hovered_action == 4 ? (chromePalette.is_dark ? 0xffb4be : 0xb42336) : baseColor));
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
    }
    drawPaneHeaderActions(bounds, focused, hovered, can_focus, zoomed, hovered_action);
    const float actionsWidth = (bounds.width >= 180.0f && bounds.height >= 28.0f) ? (can_focus ? 94.0f : 72.0f) : 0.0f;
    const float remainingWidth = fmaxf(0, bounds.width - actionsWidth);
    const float statusWidth = status.state >= 2 && status.state <= 5 && remainingWidth >= 90 ? 20.0f : 0.0f;
    if (statusWidth > 0) drawProgramStatus(status, bounds.x + remainingWidth - statusWidth - 4, bounds.y + 4, statusWidth, true);
    float titleInset = 10.0f;
    if (bounds.width >= 90.0f && bounds.height >= 28.0f) {
        const uint64_t iconKey = drawAppIconTile(icon, bounds.x + 8.0f, bounds.y + 4.0f);
        if (focused) focusedPaneIconKey = iconKey;
        titleInset = 30.0f;
    }
    labelUtf8OrDefault(title, title_length, bounds.x + titleInset, bounds.y + 5.0f, fmaxf(0, remainingWidth - statusWidth - titleInset - 8), focused ? chromePalette.text_primary : chromePalette.text_muted);
    beginTerminalPane(bounds);
}
extern "C" void vex_draw_scrollbar(VexPaneRect bounds, float thumb_y, float thumb_height, bool active) {
    if (!isfinite(thumb_y) || !isfinite(thumb_height) || thumb_height <= 0.0f || bounds.width < 24.0f) return;
    const float width = active ? 10.0f : 6.0f;
    const float right = bounds.x + bounds.width - 2.0f;
    const float left = right - width;
    const float radius = width * 0.5f;
    brush->SetColor(D2D1::ColorF(chromePalette.text_primary, active ? 0.55f : 0.26f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(left, thumb_y, right, thumb_y + thumb_height), radius, radius), brush);
}
extern "C" void vex_draw_split_preview(VexPaneRect bounds) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    const D2D1_ROUNDED_RECT rect = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), 6.0f, 6.0f);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f));
    target->FillRoundedRectangle(rect, brush);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.72f));
    target->DrawRoundedRectangle(rect, brush, 1.5f);
}
static void drawOverlayBackdropAndCard(float mx, float my, float mw, float mh) {
    const D2D1_SIZE_F win = target->GetSize();
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.50f : 0.22f));
    target->FillRectangle(D2D1::RectF(0.0f, 44.0f, win.width, win.height), brush);
    // Rex multi-layer ambient shadow
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.18f : 0.06f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - 4.0f, my + 4.0f, mx + mw + 4.0f, my + mh + 12.0f), 14.0f, 14.0f), brush);
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.28f : 0.10f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - 1.5f, my + 2.0f, mx + mw + 1.5f, my + mh + 6.0f), 13.0f, 13.0f), brush);
    const D2D1_ROUNDED_RECT modal = D2D1::RoundedRect(D2D1::RectF(mx, my, mx + mw, my + mh), 12.0f, 12.0f);
    brush->SetColor(rgb(chromePalette.titlebar_bg));
    target->FillRoundedRectangle(modal, brush);
}
extern "C" void vex_draw_theme_picker(bool dark_mode, const VexThemeCard* cards, size_t count) {
    if (!target || !brush || !cards || count == 0) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(560.0f, fmaxf(320.0f, win.width - 48.0f));
    const float mh = 384.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = roundf(fmaxf(48.0f, (win.height - mh) * 0.44f));
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    const uint32_t activeAccent = chromePalette.is_dark ? 0x2dd4bf : 0x2563eb;
    labelWithFont(chromeBoldFont, L"Appearance", mx + 20.0f, my + 12.0f, 200.0f, chromePalette.text_primary);

    labelWithFont(chromeBoldFont, L"Color scheme", mx + 20.0f, my + 34.0f, 130.0f, chromePalette.text_primary);

    const float toggleX = mx + mw - 148.0f, toggleY = my + 32.0f;
    const D2D1_ROUNDED_RECT toggleTrack = D2D1::RoundedRect(D2D1::RectF(toggleX, toggleY, toggleX + 128.0f, toggleY + 22.0f), 5.0f, 5.0f);
    brush->SetColor(rgb(chromePalette.pane_header_bg));
    target->FillRoundedRectangle(toggleTrack, brush);

    const float activeHalfX = dark_mode ? (toggleX + 2.0f) : (toggleX + 64.0f);
    const D2D1_ROUNDED_RECT activePill = D2D1::RoundedRect(D2D1::RectF(activeHalfX, toggleY + 2.0f, activeHalfX + 62.0f, toggleY + 20.0f), 4.0f, 4.0f);
    brush->SetColor(rgb(chromePalette.active_tab_bg));
    target->FillRoundedRectangle(activePill, brush);
    labelWithFont(badgeFont, L"\x263E Dark", toggleX + 12.0f, toggleY + 3.0f, 52.0f, dark_mode ? chromePalette.text_primary : chromePalette.text_muted);
    labelWithFont(badgeFont, L"\x2600 Light", toggleX + 74.0f, toggleY + 3.0f, 52.0f, !dark_mode ? chromePalette.text_primary : chromePalette.text_muted);

    const float gridX = mx + 20.0f, gridY = my + 58.0f;
    const float colGap = 12.0f, rowGap = 10.0f;
    const float cardW = (mw - 40.0f - colGap) * 0.5f, cardH = 88.0f;
    const size_t visible = count > 6 ? 6 : count;
    for (size_t i = 0; i < visible; i++) {
        const VexThemeCard& c = cards[i];
        const float cx = gridX + (float)(i % 2) * (cardW + colGap);
        const float cy = gridY + (float)(i / 2) * (cardH + rowGap);
        const D2D1_ROUNDED_RECT cardRect = D2D1::RoundedRect(D2D1::RectF(cx, cy, cx + cardW, cy + cardH), 8.0f, 8.0f);
        brush->SetColor(rgb(c.bg_rgb));
        target->FillRoundedRectangle(cardRect, brush);

        // Rex card selection ring: vibrant teal/blue outline.
        if (c.selected || c.hovered) {
            brush->SetColor(rgb(c.selected ? activeAccent : chromePalette.text_muted));
            target->DrawRoundedRectangle(cardRect, brush, c.selected ? 2.0f : 1.0f);
        }

        // Rex-style theme preview elements: "Aa", prompt capsule "~/", and accent cursor bar.
        labelWithFont(chromeBoldFont, L"Aa", cx + 12.0f, cy + 12.0f, 26.0f, c.fg_rgb);

        const D2D1_ROUNDED_RECT promptPill = D2D1::RoundedRect(D2D1::RectF(cx + 42.0f, cy + 14.0f, cx + 70.0f, cy + 30.0f), 4.0f, 4.0f);
        brush->SetColor(rgb(c.fg_rgb, c.is_dark ? 0.14f : 0.08f));
        target->FillRoundedRectangle(promptPill, brush);
        labelWithFont(badgeFont, L"~/", cx + 49.0f, cy + 15.0f, 20.0f, c.fg_rgb);

        const D2D1_ROUNDED_RECT cursorBar = D2D1::RoundedRect(D2D1::RectF(cx + 76.0f, cy + 14.0f, cx + 85.0f, cy + 30.0f), 4.0f, 4.0f);
        brush->SetColor(rgb(c.accent_rgb));
        target->FillRoundedRectangle(cursorBar, brush);

        const uint32_t subColor = c.is_dark ? 0x8b93a6 : 0x68645e;
        labelUtf8WithFont(chromeBoldFont, c.name, c.name_length, cx + 94.0f, cy + 10.0f, cardW - 146.0f, c.fg_rgb);
        labelUtf8WithFont(badgeFont, c.subtitle, c.subtitle_length, cx + 94.0f, cy + 28.0f, cardW - 106.0f, subColor);

        if (c.active) {
            const D2D1_ROUNDED_RECT badge = D2D1::RoundedRect(D2D1::RectF(cx + cardW - 48.0f, cy + 9.0f, cx + cardW - 10.0f, cy + 23.0f), 4.0f, 4.0f);
            brush->SetColor(D2D1::ColorF(c.accent_rgb, 0.20f));
            target->FillRoundedRectangle(badge, brush);
            brush->SetColor(rgb(c.accent_rgb));
            target->DrawRoundedRectangle(badge, brush, 1.0f);
            labelWithFont(badgeFont, L"Active", cx + cardW - 43.0f, cy + 9.0f, 36.0f, c.fg_rgb);
        }

        // Rex palette swatches are overlapping circles; the card-coloured rim keeps each one legible.
        const float swY = cy + cardH - 24.0f;
        uint32_t dots[6] = { c.accent_rgb, c.swatches[0], c.swatches[1], c.swatches[2], c.swatches[3], c.swatches[4] };
        for (int s = 0; s < 6; s++) {
            const D2D1_ELLIPSE dot = D2D1::Ellipse(D2D1::Point2F(cx + 19.0f + (float)s * 12.0f, swY + 8.0f), 8.0f, 8.0f);
            brush->SetColor(rgb(c.bg_rgb));
            target->FillEllipse(dot, brush);
            brush->SetColor(rgb(dots[s]));
            target->FillEllipse(D2D1::Ellipse(dot.point, 6.0f, 6.0f), brush);
        }
    }
    labelWithFont(badgeFont, L"\x2190\x2191\x2193\x2192 Preview    Tab: Dark / Light    Enter: Apply    Esc: Revert", mx + 20.0f, my + mh - 22.0f, mw - 40.0f, chromePalette.text_muted);
}
extern "C" void vex_draw_command_palette(VexPaneRect bounds, const uint8_t* query, size_t query_length, const VexCommandItem* items, size_t count, size_t first, size_t total) {
    if (!target || !brush) return;
    const float mw = bounds.width, mh = bounds.height, mx = bounds.x, my = bounds.y;
    const size_t visible = count;
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    const D2D1_ROUNDED_RECT searchBox = D2D1::RoundedRect(D2D1::RectF(mx + 12.0f, my + 12.0f, mx + mw - 12.0f, my + 52.0f), 6.0f, 6.0f);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(searchBox, brush);
    brush->SetColor(rgb(chromePalette.active_tab_border));
    target->DrawRoundedRectangle(searchBox, brush, 1.1f);
    brush->SetColor(rgb(chromePalette.accent));
    target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(mx + 26.0f, my + 30.0f), 4.5f, 4.5f), brush, 1.4f);
    target->DrawLine(D2D1::Point2F(mx + 29.5f, my + 33.5f), D2D1::Point2F(mx + 33.0f, my + 37.0f), brush, 1.4f, roundCaps);
    if (query && query_length > 0) {
        labelUtf8WithFont(chromeFont, query, query_length, mx + 44.0f, my + 25.0f, mw - 68.0f, chromePalette.text_primary);
    } else {
        label(L"Search actions...", mx + 44.0f, my + 25.0f, mw - 68.0f, chromePalette.text_muted);
    }
    labelWithFont(badgeFont, L"\x2191\x2193 Navigate    Enter Run    Esc Close", mx + 18.0f, my + mh - 23.0f, mw - 130.0f, chromePalette.text_muted);
    wchar_t range[48];
    swprintf(range, 48, L"%u-%u / %u", total ? (unsigned)first + 1 : 0, (unsigned)(first + count), (unsigned)total);
    labelWithFont(badgeFont, range, mx + mw - 104.0f, my + mh - 23.0f, 92.0f, chromePalette.text_muted);
    rectangle(mx + 12, my + mh - 32, mw - 24, 1, chromePalette.border);

    const float listY = my + 66.0f;
    if (visible == 0) {
        label(L"No matching commands", mx + 24.0f, listY + 8.0f, mw - 48.0f, chromePalette.text_muted);
        return;
    }
    for (size_t i = 0; i < visible; i++) {
        const VexCommandItem& item = items[i];
        const float ry = listY + (float)i * 34.0f;
        const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 10.0f, ry, mx + mw - 10.0f, ry + 31.0f), 5.0f, 5.0f);
        if (item.selected || item.hovered) {
            brush->SetColor(rgb(item.selected ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
            target->FillRoundedRectangle(rowRect, brush);
            if (item.selected) {
                brush->SetColor(rgb(chromePalette.active_tab_border));
                target->DrawRoundedRectangle(rowRect, brush, 1.0f);
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 11.0f, ry + 6.0f, mx + 14.0f, ry + 25.0f), 1.5f, 1.5f), brush);
            }
        }
        labelUtf8WithFont(chromeFont, item.title, item.title_length, mx + 22.0f, ry + 7.0f, mw - 160.0f, item.selected ? chromePalette.text_primary : chromePalette.text_muted);
        if (item.shortcut && item.shortcut_length > 0) {
            const float sw = (float)item.shortcut_length * 6.8f + 14.0f;
            const float sx = mx + mw - 20.0f - sw;
            const D2D1_ROUNDED_RECT kbd = D2D1::RoundedRect(D2D1::RectF(sx, ry + 5.5f, sx + sw, ry + 25.5f), 4.0f, 4.0f);
            brush->SetColor(rgb(item.selected ? chromePalette.pane_bg : chromePalette.pane_header_bg));
            target->FillRoundedRectangle(kbd, brush);
            brush->SetColor(rgb(item.selected ? chromePalette.accent : chromePalette.border));
            target->DrawRoundedRectangle(kbd, brush, 1.0f);
            labelUtf8WithFont(badgeFont, item.shortcut, item.shortcut_length, sx + 7.0f, ry + 8.0f, sw - 10.0f, item.selected ? chromePalette.accent : chromePalette.text_muted);
        }
    }
}
extern "C" void vex_draw_shell_picker(uint8_t mode, const VexShellPickerItem* items, size_t count) {
    if (!target || !brush || !items || count == 0) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(460.0f, fmaxf(300.0f, win.width - 48.0f));
    const size_t visible = count > 7 ? 7 : count;
    const float mh = 78.0f + (float)visible * 42.0f + 30.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = 58.0f;
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    label(L"Select Shell", mx + 18.0f, my + 12.0f, 160.0f, chromePalette.text_primary);
    const wchar_t* modeLabels[4] = { L"New Tab", L"Split Right", L"Split Down", L"Default" };
    const float pillGap = 6.0f;
    const float pillW = (mw - 28.0f - 3.0f * pillGap) * 0.25f;
    const float pillY = my + 38.0f;
    for (uint8_t m = 0; m < 4; m++) {
        const float px = mx + 14.0f + (float)m * (pillW + pillGap);
        const bool active = (mode == m);
        const D2D1_ROUNDED_RECT pill = D2D1::RoundedRect(D2D1::RectF(px, pillY, px + pillW, pillY + 26.0f), 5.0f, 5.0f);
        brush->SetColor(rgb(active ? chromePalette.active_tab_bg : chromePalette.pane_header_bg));
        target->FillRoundedRectangle(pill, brush);
        brush->SetColor(rgb(active ? chromePalette.active_tab_border : chromePalette.border));
        target->DrawRoundedRectangle(pill, brush, active ? 1.2f : 1.0f);
        labelWithFont(badgeFont, modeLabels[m], px + 9.0f, pillY + 5.0f, pillW - 12.0f, active ? chromePalette.text_primary : chromePalette.text_muted);
    }

    const float listY = my + 74.0f;
    for (size_t i = 0; i < visible; i++) {
        const VexShellPickerItem& item = items[i];
        const float ry = listY + (float)i * 42.0f;
        const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 10.0f, ry, mx + mw - 10.0f, ry + 38.0f), 6.0f, 6.0f);
        if (item.selected || item.hovered) {
            brush->SetColor(rgb(item.selected ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
            target->FillRoundedRectangle(rowRect, brush);
            if (item.selected) {
                brush->SetColor(rgb(chromePalette.active_tab_border));
                target->DrawRoundedRectangle(rowRect, brush, 1.0f);
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 12.0f, ry + 8.0f, mx + 15.0f, ry + 30.0f), 1.5f, 1.5f), brush);
            }
        }
        VexAppIcon icon = item.icon;
        if (icon.kind == 0) icon.color_rgb = chromePalette.accent;
        drawAppIcon(icon, mx + 22.0f, ry + 10.0f, 18.0f);
        labelUtf8WithFont(chromeFont, item.name, item.name_length, mx + 48.0f, ry + 4.0f, mw - 130.0f, item.selected ? chromePalette.text_primary : chromePalette.text_muted);
        labelUtf8WithFont(badgeFont, item.subtitle, item.subtitle_length, mx + 48.0f, ry + 21.0f, mw - 130.0f, chromePalette.text_muted);
        if (item.is_default) {
            const D2D1_ROUNDED_RECT badge = D2D1::RoundedRect(D2D1::RectF(mx + mw - 72.0f, ry + 10.0f, mx + mw - 18.0f, ry + 28.0f), 4.0f, 4.0f);
            brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f));
            target->FillRoundedRectangle(badge, brush);
            brush->SetColor(rgb(chromePalette.accent));
            target->DrawRoundedRectangle(badge, brush, 1.0f);
            labelWithFont(badgeFont, L"Default", mx + mw - 64.0f, ry + 11.0f, 44.0f, chromePalette.text_primary);
        }
    }
    labelWithFont(badgeFont, L"\x2191\x2193 Select \x2022 Tab: Action Mode \x2022 Enter: Run \x2022 Esc: Close", mx + 18.0f, my + mh - 23.0f, mw - 36.0f, chromePalette.text_muted);
}
extern "C" void vex_draw_tab_peek(const VexTabPeekCard* cards, size_t count) {
    if (!target || !brush || !cards || count == 0) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(620.0f, fmaxf(340.0f, win.width - 48.0f));
    const size_t visible = count > 9 ? 9 : count;
    const size_t cols = visible <= 2 ? visible : 3;
    const size_t rows = (visible + cols - 1) / cols;
    const float cardH = 86.0f, colGap = 12.0f, rowGap = 12.0f;
    const float mh = 62.0f + (float)rows * cardH + (float)(rows > 0 ? rows - 1 : 0) * rowGap + 24.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = roundf(fmaxf(52.0f, (win.height - mh) * 0.42f));
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    label(L"Tab Overview", mx + 20.0f, my + 15.0f, 200.0f, chromePalette.text_primary);
    labelWithFont(badgeFont, L"\x2190\x2191\x2193\x2192 Select tab \x2022 Enter: Switch \x2022 Esc: Close", mx + mw - 260.0f, my + 18.0f, 240.0f, chromePalette.text_muted);

    const float gridX = mx + 20.0f, gridY = my + 50.0f;
    const float cardW = (mw - 40.0f - (float)(cols - 1) * colGap) / (float)cols;
    for (size_t i = 0; i < visible; i++) {
        const VexTabPeekCard& c = cards[i];
        const float cx = gridX + (float)(i % cols) * (cardW + colGap);
        const float cy = gridY + (float)(i / cols) * (cardH + rowGap);
        const D2D1_ROUNDED_RECT rect = D2D1::RoundedRect(D2D1::RectF(cx, cy, cx + cardW, cy + cardH), 7.0f, 7.0f);
        brush->SetColor(rgb(c.selected ? chromePalette.active_tab_bg : (c.hovered ? chromePalette.inactive_tab_hover_bg : chromePalette.pane_bg)));
        target->FillRoundedRectangle(rect, brush);
        brush->SetColor(rgb(c.selected ? chromePalette.accent : (c.active ? chromePalette.active_tab_border : chromePalette.border)));
        target->DrawRoundedRectangle(rect, brush, c.selected ? 2.0f : 1.0f);

        VexAppIcon icon = c.icon;
        if (icon.kind == 0) icon.color_rgb = chromePalette.accent;
        drawAppIcon(icon, cx + 12.0f, cy + 12.0f, 16.0f);
        labelUtf8OrDefault(c.title, c.title_length, cx + 34.0f, cy + 11.0f, cardW - 64.0f, chromePalette.text_primary);
        if (c.status_detail && c.status_detail_length > 0) {
            labelUtf8WithFont(badgeFont, c.status_detail, c.status_detail_length, cx + 12.0f, cy + 33.0f, cardW - 24.0f, chromePalette.text_muted);
        }

        wchar_t numBadge[16];
        swprintf(numBadge, 16, L"#%u", c.index + 1);
        labelWithFont(badgeFont, numBadge, cx + cardW - 28.0f, cy + 12.0f, 24.0f, chromePalette.text_muted);

        wchar_t panesBadge[48];
        if (c.pane_count > 1 && c.status_origin_pane > 0) {
            swprintf(panesBadge, 48, L"%u panes \x2022 Pane %u", c.pane_count, c.status_origin_pane);
            labelWithFont(badgeFont, panesBadge, cx + 12.0f, cy + 56.0f, fmaxf(40.0f, cardW - 128.0f), chromePalette.text_muted);
        } else {
            swprintf(panesBadge, 48, c.pane_count == 1 ? L"1 pane" : L"%u panes", c.pane_count);
            labelWithFont(badgeFont, panesBadge, cx + 12.0f, cy + 56.0f, 80.0f, chromePalette.text_muted);
        }

        if (c.status.state >= 2 && c.status.state <= 5) {
            drawProgramStatus(c.status, cx + cardW - 116.0f, cy + 52.0f, 104.0f, false);
        }
    }
}
extern "C" void vex_draw_path_completion(VexPaneRect bounds, const uint8_t* directory, size_t directory_length, const uint8_t* query, size_t query_length, const VexPathCompletionItem* items, size_t item_count, uint32_t flags) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    const float mx = bounds.x, my = bounds.y, mw = bounds.width, mh = bounds.height;
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.45f : 0.18f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - 1.0f, my + 2.0f, mx + mw + 1.0f, my + mh + 4.0f), 8.0f, 8.0f), brush);
    const D2D1_ROUNDED_RECT card = D2D1::RoundedRect(D2D1::RectF(mx, my, mx + mw, my + mh), 7.0f, 7.0f);
    brush->SetColor(rgb(chromePalette.titlebar_bg));
    target->FillRoundedRectangle(card, brush);
    brush->SetColor(rgb(chromePalette.active_tab_border));
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

    const D2D1_ROUNDED_RECT searchBox = D2D1::RoundedRect(D2D1::RectF(mx + 8.0f, my + 24.0f, mx + mw - 8.0f, my + 50.0f), 5.0f, 5.0f);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(searchBox, brush);
    brush->SetColor(rgb(chromePalette.accent));
    target->DrawRoundedRectangle(searchBox, brush, 1.0f);
    label(L"/", mx + 15.0f, my + 28.0f, 14.0f, chromePalette.accent);
    if (query_length == 0 || !query) {
        labelWithFont(badgeFont, L"Find a file or folder...", mx + 28.0f, my + 30.0f, mw - 40.0f, chromePalette.text_muted);
    } else {
        labelUtf8OrDefault(query, query_length, mx + 28.0f, my + 28.0f, mw - 40.0f, chromePalette.text_primary);
    }

    const float listY = my + 56.0f, rowH = 26.0f;
    const size_t visible = item_count > 8 ? 8 : item_count;
    if (visible == 0 || !items) {
        labelWithFont(badgeFont, (flags & 2) ? L"Indexing folder... You can keep typing" : (flags & 1) ? L"Unable to read this folder" : L"No matching paths", mx + 14.0f, listY + 5.0f, mw - 28.0f, chromePalette.text_muted);
    } else {
        for (size_t i = 0; i < visible; i++) {
            const VexPathCompletionItem& it = items[i];
            const float iy = listY + (float)i * rowH;
            const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 6.0f, iy, mx + mw - 6.0f, iy + rowH - 2.0f), 4.0f, 4.0f);
            if (it.selected || it.hovered) {
                brush->SetColor(rgb(it.selected ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
                target->FillRoundedRectangle(rowRect, brush);
            }
            if (it.selected) {
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 8.0f, iy + 5.0f, mx + 11.0f, iy + rowH - 7.0f), 1.5f, 1.5f), brush);
            }
            const float ix = mx + 16.0f;
            if (it.is_directory) {
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix, iy + 6.0f, ix + 5.5f, iy + 9.0f), 1.0f, 1.0f), brush);
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix, iy + 8.0f, ix + 12.5f, iy + 17.5f), 1.5f, 1.5f), brush);
            } else {
                brush->SetColor(rgb(chromePalette.text_muted));
                target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix + 1.5f, iy + 5.5f, ix + 11.0f, iy + 17.5f), 1.2f, 1.2f), brush, 1.1f);
            }
            labelUtf8OrDefault(it.name, it.name_length, mx + 33.0f, iy + 3.0f, mw - 68.0f, chromePalette.text_primary);
            if (it.is_directory) {
                labelWithFont(badgeFont, L"DIR", mx + mw - 34.0f, iy + 5.0f, 26.0f, chromePalette.accent);
            }
        }
    }
    rectangle(mx + 8, my + mh - 30, mw - 16, 1, chromePalette.border);
    const wchar_t* hint = (flags & 4) ? L"Index limit reached; narrow the folder" : L"\x2190 Parent   Tab Open   Enter Insert   Esc Close";
    labelWithFont(badgeFont, hint, mx + 12, my + mh - 22, mw - 24, chromePalette.text_muted);
    target->PopAxisAlignedClip();
}
extern "C" void vex_draw_close_confirm(VexCloseConfirmModal modal) {
    if (!target || !brush) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(440.0f, fmaxf(300.0f, win.width - 48.0f));
    const float mh = 186.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = roundf(fmaxf(56.0f, (win.height - mh) * 0.44f));
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    const uint32_t dangerColor = (modal.kind == 2)
        ? (chromePalette.is_dark ? 0xf59e0b : 0xd97706)
        : (chromePalette.is_dark ? 0xef4444 : 0xdc2626);

    // Top accent bar inside the modal card
    brush->SetColor(rgb(dangerColor));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 14.0f, my + 1.0f, mx + mw - 14.0f, my + 3.5f), 1.5f, 1.5f), brush);

    // Icon badge
    const D2D1_ROUNDED_RECT iconBox = D2D1::RoundedRect(D2D1::RectF(mx + 20.0f, my + 18.0f, mx + 48.0f, my + 46.0f), 7.0f, 7.0f);
    brush->SetColor(D2D1::ColorF(dangerColor, chromePalette.is_dark ? 0.18f : 0.12f));
    target->FillRoundedRectangle(iconBox, brush);
    brush->SetColor(D2D1::ColorF(dangerColor, 0.45f));
    target->DrawRoundedRectangle(iconBox, brush, 1.0f);

    VexAppIcon icon = modal.icon;
    if (icon.kind == 0) icon.color_rgb = dangerColor;
    drawAppIcon(icon, mx + 25.0f, my + 23.0f, 18.0f);

    if (modal.title && modal.title_length > 0) {
        labelUtf8WithFont(chromeFont, modal.title, modal.title_length, mx + 58.0f, my + 23.0f, mw - 78.0f, chromePalette.text_primary);
    }

    if (modal.message && modal.message_length > 0) {
        wchar_t wide[512];
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(modal.message), (int)modal.message_length, wide, 511);
        if (count > 0 && chromeFont) {
            brush->SetColor(rgb(chromePalette.text_muted));
            target->DrawText(wide, (UINT32)count, chromeFont, D2D1::RectF(mx + 22.0f, my + 58.0f, mx + mw - 22.0f, my + 124.0f), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
        }
    }

    labelWithFont(badgeFont, L"Esc: Cancel \x2022 Enter: Confirm", mx + 22.0f, my + mh - 36.0f, 165.0f, chromePalette.text_muted);

    const float btnY = my + mh - 46.0f;
    const float btnH = 32.0f;
    const float confirmW = 128.0f;
    const float confirmX = mx + mw - 20.0f - confirmW;
    const float cancelW = 88.0f;
    const float cancelX = confirmX - 10.0f - cancelW;

    const bool cancelSelected = (modal.selected_button == 0);
    const bool cancelHovered = (modal.hovered_button == 1);
    const D2D1_ROUNDED_RECT cancelRect = D2D1::RoundedRect(D2D1::RectF(cancelX, btnY, cancelX + cancelW, btnY + btnH), 6.0f, 6.0f);
    brush->SetColor(rgb((cancelSelected || cancelHovered) ? chromePalette.active_tab_bg : chromePalette.pane_bg));
    target->FillRoundedRectangle(cancelRect, brush);
    brush->SetColor(rgb(cancelSelected ? chromePalette.accent : chromePalette.border));
    target->DrawRoundedRectangle(cancelRect, brush, cancelSelected ? 1.5f : 1.0f);
    label(L"Cancel", cancelX + 22.0f, btnY + 7.0f, cancelW - 24.0f, chromePalette.text_primary);

    const bool confirmSelected = (modal.selected_button == 1);
    const bool confirmHovered = (modal.hovered_button == 2);
    const D2D1_ROUNDED_RECT confirmRect = D2D1::RoundedRect(D2D1::RectF(confirmX, btnY, confirmX + confirmW, btnY + btnH), 6.0f, 6.0f);
    brush->SetColor(D2D1::ColorF(dangerColor, (confirmSelected || confirmHovered) ? 0.92f : 0.78f));
    target->FillRoundedRectangle(confirmRect, brush);
    brush->SetColor(rgb(confirmSelected ? 0xffffff : dangerColor));
    target->DrawRoundedRectangle(confirmRect, brush, confirmSelected ? 1.5f : 1.0f);
    if (modal.confirm_label && modal.confirm_label_length > 0) {
        const float textW = (float)modal.confirm_label_length * 6.8f;
        const float textX = confirmX + fmaxf(12.0f, (confirmW - textW) * 0.5f);
        labelUtf8WithFont(chromeFont, modal.confirm_label, modal.confirm_label_length, textX, btnY + 7.0f, confirmW - 16.0f, 0xffffff);
    }
}
