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
extern "C" void vex_draw_session_exit(VexPaneRect bounds, uint32_t exit_code) {
    if (!target || !brush || bounds.width < 180 || bounds.height < 130) return;
    const float width = fminf(520.0f, bounds.width - 32.0f);
    const float x = bounds.x + (bounds.width - width) * 0.5f, y = bounds.y + bounds.height - 80.0f;
    const auto card = D2D1::RoundedRect(D2D1::RectF(x, y, x + width, y + 64), 12, 12);
    brush->SetColor(rgb(chromePalette.titlebar_bg)); target->FillRoundedRectangle(card, brush);
    brush->SetColor(rgb(chromePalette.border)); target->DrawRoundedRectangle(card, brush, 1);
    const uint32_t statusColor = exit_code == 0 ? (chromePalette.is_dark ? 0x85C9AD : 0x27664E) : (chromePalette.is_dark ? 0xE5A47C : 0x9B4B28);
    brush->SetColor(rgb(statusColor));
    const float cx = x + 26, cy = y + 24;
    target->DrawEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy), 8, 8), brush, 1.4f);
    if (exit_code == 0) {
        target->DrawLine(D2D1::Point2F(cx - 4, cy), D2D1::Point2F(cx - 1, cy + 3), brush, 1.4f);
        target->DrawLine(D2D1::Point2F(cx - 1, cy + 3), D2D1::Point2F(cx + 4, cy - 3), brush, 1.4f);
    } else {
        target->DrawLine(D2D1::Point2F(cx, cy - 4), D2D1::Point2F(cx, cy + 1), brush, 1.4f);
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy + 4), 0.8f, 0.8f), brush);
    }
    wchar_t title[80]; swprintf(title, 80, exit_code == 0 ? L"Session finished · exit code %u" : L"Process exited with code %u", exit_code);
    label(title, x + 46, y + 12, width - 60, chromePalette.text_primary);
    label(L"Output preserved. Ctrl+Shift+T opens a new terminal.", x + 46, y + 35, width - 60, chromePalette.text_muted);
}
extern "C" void vex_draw_split_preview(VexPaneRect bounds) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    const D2D1_ROUNDED_RECT rect = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), 6.0f, 6.0f);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f));
    target->FillRoundedRectangle(rect, brush);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.72f));
    target->DrawRoundedRectangle(rect, brush, 1.5f);
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
