#include "windows_chrome.h"
#include "drawing_color.h"
#include "icon_contrast.h"
#include "ui_renderer.h"
#include "ui_material.h"
#include "terminal_drawing.h"
#include "chrome_motion.h"
#include "chrome_tooltip.h"
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
IDWriteTextFormat* tabTitleFont;
IDWriteTextFormat* tooltipFont;
TabRenameLayout tabRenameLayout;
TabRenameLayout titleLayouts[32];
size_t titleLayoutIndex;
struct TooltipRegion { VexPaneRect bounds; wchar_t text[257]; uint64_t key; };
TooltipRegion tooltipRegions[40];
size_t tooltipRegionCount;
ChromeTooltip tooltip;
float pointerX, pointerY;
bool tooltipsEnabled, chromeShortcutsEnabled;
IDWriteTextLayout* tooltipLayout;
IDWriteTextFormat* tooltipLayoutFont;
wchar_t tooltipLayoutText[257];
float tooltipLayoutWidth, tooltipTextWidth, tooltipTextHeight;
VexChromePalette chromePalette;
VexUiMetrics uiMetrics;
const VexTabStripLayout* tabs;
struct TabRowMotion {
    uint32_t id;
    ChromeMotion along, width, reveal;
    VexPaneRect painted;
    wchar_t title[257];
};
TabRowMotion tabRows[8];
VexTabStripLayout lastTabLayout;
float lastTabClientWidth;
struct ExitingTabRow {
    ChromeMotion opacity;
    VexPaneRect bounds;
    wchar_t title[257];
};
ExitingTabRow exitingRows[8];
unsigned nextExitRow, exitRowCount;
double exitDeadline;
bool tabRowsMoving, tabRowsClipped;
const VexTabStripLayout* windowTabs;
const VexTabStripLayout* movingSidebar;
float sidebarEdge, clientHeight;
bool movingSidebarClip;
int savedTabHover;
bool savedTooltips;
int hoveredTabIndex;
bool hoveredTabClose;
double chromeTimeMs;
bool chromeMotionEnabled, chromeMotionPending, chromeEffectsPending;
bool activePaneBorderEnabled = true;
ChromeMotion tabHoverMotion[8], tabCloseHoverMotion[8], tabEditMotion[8];
struct PaneChromeMotion { uint32_t id = 0; ChromeMotion action[4], focus; };
PaneChromeMotion paneChromeMotion[64];
ChromeMotion splitPreviewAlphaMotion;
bool splitPreviewSeenLastFrame, splitPreviewSeenThisFrame;
float tabSelectionX;
uint64_t activeTabIconKey, focusedPaneIconKey;
size_t activeTabIconCount;
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

static bool visibleTabStrip(const VexTabStripLayout& layout) {
    return layout.horizontal || layout.sidebar_width > 0;
}

static void updateTabRows(const WindowChromeFrame& frame) {
    const auto& next = *frame.tabs;
    const bool sameStrip = visibleTabStrip(next) && visibleTabStrip(lastTabLayout) &&
        next.horizontal == lastTabLayout.horizontal && next.sidebar_width == lastTabLayout.sidebar_width &&
        next.first_row.height == lastTabLayout.first_row.height && frame.clientWidth == lastTabClientWidth;
    const bool animate = frame.motionEnabled && !frame.movingSidebar && sameStrip && (next.horizontal || frame.uiMetrics.pane_gap != 0);
    const double now = frame.timeMs;
    bool rosterChanged = next.count != lastTabLayout.count;
    for (uint32_t i = 0; !rosterChanged && i < next.count; ++i)
        rosterChanged = next.ids[i] != lastTabLayout.ids[i];
    if (!tabRowsMoving && sameStrip && !rosterChanged &&
        next.first_row.width == lastTabLayout.first_row.width && next.first_row.y == lastTabLayout.first_row.y)
        return;
    if (!animate || now >= exitDeadline) nextExitRow = exitRowCount = 0;
    if (rosterChanged || (visibleTabStrip(next) && !visibleTabStrip(lastTabLayout))) {
        // Remap only at roster/visibility changes. An exiting row never owns a session.
        TabRowMotion previous[8];
        if (animate) memcpy(previous, tabRows, sizeof(previous));
        if (animate) {
            for (uint32_t i = 0; i < lastTabLayout.count; ++i) {
                bool remains = false;
                for (uint32_t j = 0; j < next.count; ++j) remains |= previous[i].id == next.ids[j];
                if (remains || previous[i].painted.width <= 0) continue;
                auto& exit = exitingRows[nextExitRow];
                nextExitRow = (nextExitRow + 1) % 8;
                if (exitRowCount < 8) ++exitRowCount;
                exitDeadline = now + ChromeMotion::durationMs;
                exit.bounds = previous[i].painted;
                wcscpy(exit.title, previous[i].title);
                exit.opacity.update(previous[i].reveal.sample(now), now, false);
                exit.opacity.update(0, now, true);
            }
        }
        for (uint32_t i = 0; i < next.count; ++i) {
            int old = -1;
            for (uint32_t j = 0; animate && j < lastTabLayout.count; ++j)
                if (previous[j].id == next.ids[i]) { old = (int)j; break; }
            if (old >= 0) tabRows[i] = previous[old];
            else {
                tabRows[i] = {};
                tabRows[i].id = next.ids[i];
                auto row = chromeTabRow(next, i);
                tabRows[i].along.update((next.horizontal ? row.x : row.y) + (animate ? 5 : 0), now, false);
                tabRows[i].width.update(row.width, now, false);
                tabRows[i].reveal.update(animate ? 0.0f : 1.0f, now, false);
            }
        }
    }
    tabRowsMoving = exitRowCount != 0;
    for (uint32_t i = 0; i < next.count; ++i) {
        auto& motion = tabRows[i];
        auto row = chromeTabRow(next, i);
        const float along = motion.along.update(next.horizontal ? row.x : row.y, now, animate);
        if (next.horizontal) row.x = along; else row.y = along;
        row.width = motion.width.update(row.width, now, animate);
        motion.reveal.update(1, now, animate);
        motion.painted = row;
        tabRowsMoving |= motion.along.pending(now) || motion.width.pending(now) || motion.reveal.pending(now);
    }
    chromeMotionPending |= tabRowsMoving;
    lastTabLayout = next;
    lastTabClientWidth = frame.clientWidth;
}

VexPaneRect interactiveChromeTabRow(const VexTabStripLayout& layout, uint32_t index, float width) {
    if (width == lastTabClientWidth && layout.count == lastTabLayout.count &&
        layout.horizontal == lastTabLayout.horizontal && layout.sidebar_width == lastTabLayout.sidebar_width &&
        layout.row_pitch == lastTabLayout.row_pitch && layout.first_row.height == lastTabLayout.first_row.height &&
        memcmp(layout.ids, lastTabLayout.ids, layout.count * sizeof(layout.ids[0])) == 0)
        return tabRows[index].painted;
    return chromeTabRow(layout, index);
}

static VexPaneRect drawTabRow(uint32_t index) {
    return movingSidebarClip ? chromeTabRow(*tabs, index) : tabRows[index].painted;
}

void beginWindowChromeFrame(const WindowChromeFrame& frame) {
    splitPreviewSeenLastFrame = splitPreviewSeenThisFrame;
    splitPreviewSeenThisFrame = false;
    if (chromeFont != frame.normalFont || textFactory != frame.textFactory) tabRenameLayout.clear();
    target = frame.target;
    textFactory = frame.textFactory;
    chromeFont = frame.normalFont;
    chromeBoldFont = frame.boldFont;
    badgeFont = frame.badgeFont;
    paneTitleFont = frame.paneTitleFont;
    tabTitleFont = frame.normalFont;
    tooltipFont = frame.tooltipFont;
    titleLayoutIndex = 0;
    tooltipRegionCount = 0;
    pointerX = frame.pointerX; pointerY = frame.pointerY;
    tooltipsEnabled = frame.tooltipsEnabled;
    chromeShortcutsEnabled = true;
    chromePalette = frame.palette;
    uiMetrics = frame.uiMetrics;
    if (uiMetrics.pane_gap == 0) {
        chromePalette.window_bg = chromePalette.pane_bg;
        chromePalette.titlebar_bg = chromePalette.pane_bg;
        chromePalette.pane_header_bg = chromePalette.pane_bg;
    }
    tabs = windowTabs = frame.tabs;
    movingSidebar = frame.movingSidebar;
    sidebarEdge = frame.sidebarEdge;
    clientHeight = frame.clientHeight;
    movingSidebarClip = false;
    hoveredTabIndex = frame.hoveredTab;
    hoveredTabClose = frame.hoveredClose;
    chromeTimeMs = frame.timeMs;
    chromeMotionEnabled = frame.motionEnabled;
    chromeMotionPending = false;
    chromeEffectsPending = false;
    updateTabRows(frame);
    activeTabIconKey = 0; focusedPaneIconKey = 0; activeTabIconCount = 0;
}
bool windowChromeAnimationPending() { return chromeMotionPending; }
bool windowChromeEffectsPending() { return chromeEffectsPending; }
WindowChromeDiagnostics windowChromeDiagnostics() {
    return { activeTabIconKey, focusedPaneIconKey, activeTabIconCount, tabSelectionX };
}

namespace {
struct TooltipLayoutOwner {
    ~TooltipLayoutOwner() {
        if (tooltipLayout) tooltipLayout->Release();
        if (tooltipLayoutFont) tooltipLayoutFont->Release();
    }
} tooltipLayoutOwner;
static bool contains(VexPaneRect bounds, float x, float y) {
    return x >= bounds.x && x < bounds.x + bounds.width && y >= bounds.y && y < bounds.y + bounds.height;
}
static void offerTooltip(const wchar_t* text, VexPaneRect bounds) {
    if (!tooltipsEnabled || !text || !*text || tooltipRegionCount >= 40 || bounds.width <= 0 || bounds.height <= 0) return;
    auto& region = tooltipRegions[tooltipRegionCount++];
    region.bounds = bounds;
    wcsncpy(region.text, text, 256); region.text[256] = 0;
    uint64_t hash = 14695981039346656037ull;
    const auto add = [&hash](const void* data, size_t size) {
        const auto* bytes = (const uint8_t*)data;
        for (size_t i = 0; i < size; ++i) { hash ^= bytes[i]; hash *= 1099511628211ull; }
    };
    add(&bounds, sizeof(bounds)); add(region.text, wcslen(region.text) * sizeof(wchar_t));
    region.key = hash ? hash : 1;
}
static const TooltipRegion* tooltipAt(float x, float y) {
    if (tooltipsEnabled) for (size_t i = tooltipRegionCount; i > 0; --i) {
        if (contains(tooltipRegions[i - 1].bounds, x, y)) return &tooltipRegions[i - 1];
    }
    return nullptr;
}
}
void offerWindowChromeShortcut(const wchar_t* text, VexPaneRect bounds) {
    if (chromeShortcutsEnabled) offerTooltip(text, bounds);
}
bool windowChromeTooltipPointer(float x, float y) {
    const auto* region = tooltipAt(x, y);
    return (region ? region->key : 0) != tooltip.key;
}
bool dismissWindowChromeTooltip() {
    const bool hadTooltip = tooltip.key != 0;
    tooltip.reset();
    return hadTooltip;
}
double windowChromeTooltipDeadline() { return tooltip.deadline(chromeTimeMs); }
void drawWindowChromeTooltip() {
    const auto* region = tooltipAt(pointerX, pointerY);
    tooltip.update(region ? region->key : 0, chromeTimeMs);
    if (!region || !tooltip.visible(chromeTimeMs) || !tooltipFont || !textFactory) return;
    const auto viewport = target->GetSize();
    const float maxWidth = fminf(520.0f, viewport.width - 40.0f);
    if (maxWidth <= 0) return;
    if (!tooltipLayout || tooltipLayoutFont != tooltipFont || tooltipLayoutWidth != maxWidth || wcscmp(tooltipLayoutText, region->text)) {
        if (tooltipLayout) { tooltipLayout->Release(); tooltipLayout = nullptr; }
        if (tooltipLayoutFont) { tooltipLayoutFont->Release(); tooltipLayoutFont = nullptr; }
        if (FAILED(textFactory->CreateTextLayout(region->text, (UINT32)wcslen(region->text), tooltipFont, maxWidth, 4096, &tooltipLayout))) return;
        tooltipLayoutFont = tooltipFont; tooltipLayoutFont->AddRef();
        wcscpy(tooltipLayoutText, region->text); tooltipLayoutWidth = maxWidth;
        DWRITE_TEXT_METRICS metrics = {};
        if (FAILED(tooltipLayout->GetMetrics(&metrics))) return;
        tooltipTextWidth = ceilf(metrics.widthIncludingTrailingWhitespace);
        tooltipTextHeight = ceilf(metrics.height);
    }
    const float w = fminf(maxWidth, tooltipTextWidth) + 20;
    const float h = fminf(viewport.height - 24, tooltipTextHeight + 14);
    const auto anchor = region->bounds;
    const float x = fmaxf(8, fminf(viewport.width - w - 8, anchor.x + anchor.width * 0.5f - w * 0.5f));
    const float below = anchor.y + anchor.height + 7;
    const float y = below + h <= viewport.height - 8 ? below : fmaxf(8, anchor.y - h - 7);
    const float alpha = tooltip.opacity(chromeTimeMs, chromeMotionEnabled);
    chromeEffectsPending |= tooltip.animating(chromeTimeMs, chromeMotionEnabled);
    brush->SetColor(rgb(0, (chromePalette.is_dark ? 0.26f : 0.10f) * alpha));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x - 2, y + 1, x + w + 2, y + h + 4), 9, 9), brush);
    const auto box = D2D1::RoundedRect(D2D1::RectF(x, y, x + w, y + h), 7, 7);
    fillUiMaterial(*target, *brush, box, chromePalette, chromePalette.active_tab_bg, alpha);
    brush->SetColor(rgb(chromePalette.text_primary, alpha));
    // Keep the layout width used for measurement: wrapping must match the cached raster exactly.
    target->DrawText(region->text, (UINT32)wcslen(region->text), tooltipFont, D2D1::RectF(x + 10, y + 7, x + 10 + maxWidth, y + h - 7), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
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
    if (uiMetrics.pane_gap != 0) {
        const uint32_t rawInk = icon.kind ? icon.color_rgb : chromePalette.text_muted;
        const uint32_t ink = contrastingIconColor(rawInk, chromePalette.pane_header_bg);
        const uint32_t tileBg = blendRgb(chromePalette.pane_header_bg, ink, chromePalette.is_dark ? 0.12f : 0.08f);
        brush->SetColor(rgb(tileBg, chromePalette.is_dark ? 0.85f : 0.75f));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, y, x + 18.0f, y + 18.0f), 4.5f, 4.5f), brush);
    }
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
    const bool motion = chromeMotionEnabled && uiMetrics.pane_gap != 0;
    const float phase = motion ? (float)(status.phase % 1200) / 1200.0f : 0.0f;
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
                if (status.state == 4 && status.kind == 3) {
                    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 2.6f, cy - 0.8f, cx + 2.6f, cy + 2.8f), 0.8f, 0.8f), brush, 1);
                    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 1.4f, cy - 3.4f, cx + 1.4f, cy - 0.8f), 1, 1), brush, 1);
                } else if (status.state == 4 && status.kind == 2) {
                    target->DrawLine(D2D1::Point2F(cx - 1.6f, cy - 2.3f), D2D1::Point2F(cx + 1.3f, cy - 2.3f), brush, 1.2f, true);
                    target->DrawLine(D2D1::Point2F(cx + 1.3f, cy - 2.3f), D2D1::Point2F(cx, cy + 0.5f), brush, 1.2f, true);
                    target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cx, cy + 2.6f), 0.7f, 0.7f), brush);
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
static float titleLabel(const uint8_t* bytes, size_t length, float x, float y, float width, uint32_t color, IDWriteTextFormat* font, VexPaneRect hitBounds, wchar_t* snapshot = nullptr, float alpha = 1) {
    wchar_t local[257];
    auto* text = snapshot ? snapshot : local;
    const int count = bytes && length > 0 && length <= 256
        ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, text, 256) : 0;
    if (count > 0) text[count] = 0;
    else wcscpy(text, shellName);
    float fullWidth = 0;
    auto& cache = titleLayouts[titleLayoutIndex++ % 32];
    if (auto* layout = cache.get(textFactory, font, text, (UINT32)wcslen(text))) {
        DWRITE_TEXT_METRICS metrics = {};
        if (SUCCEEDED(layout->GetMetrics(&metrics))) fullWidth = metrics.widthIncludingTrailingWhitespace;
    }
    if (width > 0) labelWithFont(font, text, x, y, width, color, alpha);
    if (width <= 0 || fullWidth > width + 0.5f) offerTooltip(text, hitBounds);
    return fminf(fmaxf(0, width), fullWidth);
}
extern "C" int vex_window_shell_name(const uint8_t* bytes, size_t length) {
    if (!bytes || !length || length >= sizeof(shellName) / sizeof(shellName[0]) || memchr(bytes, 0, length)) return ERROR_INVALID_PARAMETER;
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, shellName, (int)(sizeof(shellName) / sizeof(shellName[0]) - 1));
    if (!count) return (int)GetLastError();
    shellName[count] = 0;
    return 0;
}
static D2D1_ROUNDED_RECT tabPill(VexPaneRect row) {
    const float radius = uiMetrics.pane_gap == 0 ? 4.0f : 8.0f;
    return D2D1::RoundedRect(D2D1::RectF(row.x, row.y, row.x + row.width, row.y + row.height), radius, radius);
}

extern "C" bool vex_begin_sidebar_rows(bool exiting) {
    if (exiting && (!movingSidebar || windowTabs->sidebar_width > 0)) return false;
    if (!movingSidebar || (windowTabs->sidebar_width == 0 && !exiting)) {
        if (tabRowsMoving && visibleTabStrip(*tabs)) {
            if (tabs->horizontal)
                target->PushAxisAlignedClip(D2D1::RectF(0, 0, tabs->plus_x, 44), D2D1_ANTIALIAS_MODE_ALIASED);
            else if (clientHeight > 44)
                target->PushAxisAlignedClip(D2D1::RectF(0, 44, tabs->sidebar_width, clientHeight), D2D1_ANTIALIAS_MODE_ALIASED);
            tabRowsClipped = tabs->horizontal || clientHeight > 44;
        }
        return true;
    }
    tabs = movingSidebar;
    target->PushAxisAlignedClip(D2D1::RectF(0, 44, sidebarEdge, clientHeight), D2D1_ANTIALIAS_MODE_ALIASED);
    movingSidebarClip = true;
    savedTabHover = hoveredTabIndex;
    savedTooltips = tooltipsEnabled;
    hoveredTabIndex = -1;
    tooltipsEnabled = false;
    const bool compact = uiMetrics.pane_gap == 0;
    const uint32_t sidebarBg = compact ? chromePalette.pane_bg
        : blendRgb(chromePalette.pane_bg, chromePalette.is_dark ? 0 : chromePalette.accent, chromePalette.is_dark ? 0.10f : 0.035f);
    rectangle(0, 44, sidebarEdge, clientHeight - 44, sidebarBg);
    brush->SetColor(rgb(chromePalette.border, compact ? 0.42f : (chromePalette.is_dark ? 0.5f : 0.6f)));
    target->FillRectangle(D2D1::RectF(sidebarEdge - 1, 44, sidebarEdge, clientHeight), brush);
    return true;
}

extern "C" void vex_end_sidebar_rows() {
    if (tabRowsClipped) {
        target->PopAxisAlignedClip();
        tabRowsClipped = false;
    }
    if (!movingSidebarClip) return;
    target->PopAxisAlignedClip();
    tabs = windowTabs;
    hoveredTabIndex = savedTabHover;
    tooltipsEnabled = savedTooltips;
    movingSidebarClip = false;
}

extern "C" void vex_draw_tab_strip_background(uint32_t active_index) {
    if (active_index >= tabs->count) return;
    tabSelectionX = windowTabs->horizontal ? drawTabRow(active_index).x : chromeTabRow(*windowTabs, active_index).x;
    const bool compact = uiMetrics.pane_gap == 0;
    const bool expanded = tabs->sidebar_width > 0;
    const bool visible = expanded || tabs->horizontal;
    if (expanded && !compact) labelWithFont(badgeFont, L"Sessions", tabs->first_row.x + 8, 48, tabs->first_row.width - 16, chromePalette.text_muted, 0.82f);
    if (!visible) return;
    for (unsigned i = 0; i < exitRowCount; ++i) {
        const auto& exit = exitingRows[i];
        if (!exit.opacity.pending(chromeTimeMs)) continue;
        const float opacity = exit.opacity.sample(chromeTimeMs);
        auto row = exit.bounds;
        if (tabs->horizontal) row.y += (1 - opacity) * 5;
        else row.x -= (1 - opacity) * 5;
        brush->SetColor(rgb(chromePalette.active_tab_bg, 0.65f * opacity));
        target->FillRoundedRectangle(tabPill(row), brush);
        if (row.width > 22) labelWithFont(tabTitleFont, exit.title, row.x + 9, row.y + row.height / 2 - 9, row.width - 22, chromePalette.text_muted, opacity);
    }
    for (uint32_t i = 0; i < tabs->count; ++i) {
        auto row = drawTabRow(i);
        const float hover = tabHoverMotion[i].update(row.width > 0 && (int)i == hoveredTabIndex ? 1.0f : 0.0f, chromeTimeMs, chromeMotionEnabled && !compact);
        chromeMotionPending |= tabHoverMotion[i].pending(chromeTimeMs);
        if (row.width <= 0 || (i != active_index && hover <= 0)) continue;
        if (i == active_index) {
            if (compact) {
                brush->SetColor(rgb(blendRgb(chromePalette.pane_bg, chromePalette.text_primary, chromePalette.is_dark ? 0.12f : 0.08f)));
            } else if (tabs->horizontal) {
                auto shadow = row;
                shadow.y += 1;
                brush->SetColor(rgb(0, chromePalette.is_dark ? 0.24f : 0.12f));
                target->FillRoundedRectangle(tabPill(shadow), brush);
                fillUiMaterial(*target, *brush, tabPill(row), chromePalette, chromePalette.active_tab_bg);
                continue;
            } else {
                brush->SetColor(rgb(blendRgb(chromePalette.active_tab_bg, chromePalette.text_primary, chromePalette.is_dark ? 0.19f : 0.09f)));
            }
        } else {
            brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg, hover));
        }
        target->FillRoundedRectangle(tabPill(row), brush);
    }
    if (!compact && tabs->horizontal) for (uint32_t i = 1; i < tabs->count; ++i) {
        if (i == active_index || i - 1 == active_index) continue;
        const auto row = drawTabRow(i);
        const float opacity = 1 - fmaxf(tabHoverMotion[i - 1].sample(chromeTimeMs), tabHoverMotion[i].sample(chromeTimeMs));
        brush->SetColor(rgb(chromePalette.border, 0.36f * opacity));
        target->FillRectangle(D2D1::RectF(row.x - 2.5f, row.y + 7.5f, row.x - 1.5f, row.y + row.height - 7.5f), brush);
    }
    if (!compact && !movingSidebarClip) for (uint32_t i = 0; i < tabs->count; ++i) {
        const float reveal = tabRows[i].reveal.sample(chromeTimeMs);
        if (reveal >= 0.999f) continue;
        brush->SetColor(rgb(chromePalette.accent, 0.18f * (1 - reveal)));
        target->FillRoundedRectangle(tabPill(drawTabRow(i)), brush);
    }
}

extern "C" void vex_draw_tab_insertion_marker(VexPaneRect bounds) {
    brush->SetColor(rgb(chromePalette.accent));
    target->FillRoundedRectangle(tabPill(bounds), brush);
}

extern "C" void vex_draw_sidebar_attention(uint32_t state) {
    VexProgramStatus status = {};
    status.state = state;
    const float x = tabs->toggle.x + tabs->toggle.width / 2 + 7, y = 16;
    brush->SetColor(rgb(chromePalette.titlebar_bg));
    target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), 4.5f, 4.5f), brush);
    brush->SetColor(rgb(state == 1 ? chromePalette.accent : programStatusColor(status)));
    target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), 3, 3), brush);
}

static void offerTabStatus(VexProgramStatus status, VexPaneRect bounds) {
    if (!tooltipsEnabled || !chromeContains(bounds, pointerX, pointerY)) return;
    wchar_t text[257];
    if (!status.detail || !status.detail_length || status.detail_length > 256) return;
    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(status.detail), (int)status.detail_length, text, 256);
    if (!length) return;
    text[length] = 0;
    if (status.progress >= 0 && status.progress <= 100 && length <= 247) {
        swprintf(text + length, 257 - length, L"  %d%%", status.progress);
    }
    offerTooltip(text, bounds);
}

extern "C" void vex_draw_tab(uint32_t index, uint32_t count, bool active, VexTabEdit edit, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention) {
    if (count != tabs->count || index >= count) return;
    const auto row = drawTabRow(index);
    if (row.width <= 0) return;
    const float width = row.width, x = row.x, dy = row.y + row.height / 2 - 22;
    if (active) activeTabIconCount = icon_count;
    if (edit.editing) tooltipsEnabled = false;
    const bool isHovered = (int)index == hoveredTabIndex;
    const bool compact = uiMetrics.pane_gap == 0;
    if (active && !edit.editing) tabRenameLayout.clear();
    const float editT = index < 8
        ? tabEditMotion[index].update(edit.editing ? 1.0f : 0.0f, chromeTimeMs, chromeMotionEnabled && edit.editing)
        : (edit.editing ? 1.0f : 0.0f);
    if (index < 8) chromeEffectsPending |= tabEditMotion[index].pending(chromeTimeMs);
    float inset = 9.0f;
    if (icons && icon_count && width >= 68) {
        const float tx = x + 8.0f, ty = 13.0f + dy;
        const bool secondary = !compact && icon_count > 1 && tabs->sidebar_width > 0 && !edit.editing && width >= 128;
        if (secondary) drawAppIconTile(icons[1], tx + 10, ty);
        const uint64_t key = drawAppIconTile(icons[0], tx, ty);
        if (active) activeTabIconKey = key;
        inset = secondary ? 41.0f : 33.0f;
    }
    const bool hasStatus = !edit.editing && status.state >= 2 && status.state <= 5;
    float rightReserve = 24.0f;
    if (hasStatus && width >= 88.0f) {
        rightReserve = 46.0f;
    } else if (width >= 68 && !edit.editing && !active && attention) {
        brush->SetColor(rgb(chromePalette.accent));
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + width - 30.0f, 22.0f + dy), 2.5f, 2.5f), brush);
        rightReserve = 34.0f;
    }
    if (edit.editing && width >= 46.0f) {
        const float textW = fmaxf(0.0f, width - inset - rightReserve);
        if (textW > 8.0f) {
            const float boxLeft = x + inset - 4.0f;
            const float boxRight = x + inset + textW + 2.0f;
            const float boxTop = 9.5f + dy, boxBottom = 34.5f + dy;
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
                        const D2D1_ROUNDED_RECT selPill = D2D1::RoundedRect(D2D1::RectF(innerLeft - 2.0f, 12.5f + dy, innerLeft + selW + 2.0f, 31.5f + dy), 3.5f, 3.5f);
                        const uint32_t selFill = blendRgb(fieldBg, chromePalette.accent, chromePalette.is_dark ? 0.38f : 0.22f);
                        brush->SetColor(rgb(selFill));
                        target->FillRoundedRectangle(selPill, brush);
                        brush->SetColor(rgb(chromePalette.accent, chromePalette.is_dark ? 0.52f : 0.34f));
                        target->DrawRoundedRectangle(selPill, brush, 1.0f);
                        label(text, innerLeft, 13.0f + dy, innerW, chromePalette.text_primary);
                    } else {
                        const float scrollX = caretX > innerW - 2.0f ? (caretX - (innerW - 2.0f)) : 0.0f;
                        const float drawW = fmaxf(innerW + scrollX + 8.0f, textAdv + 8.0f);
                        label(text, innerLeft - scrollX, 13.0f + dy, drawW, chromePalette.text_primary);
                        const float cx = fminf(innerRight - 0.5f, fmaxf(innerLeft, innerLeft + caretX - scrollX));
                        brush->SetColor(rgb(chromePalette.accent));
                        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 0.75f, 14.0f + dy, cx + 0.75f, 30.0f + dy), 0.75f, 0.75f), brush);
                    }
                } else {
                    brush->SetColor(rgb(chromePalette.accent));
                    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(innerLeft - 0.75f, 14.0f + dy, innerLeft + 0.75f, 30.0f + dy), 0.75f, 0.75f), brush);
                    const float placeholderW = fmaxf(0.0f, innerW - 4.0f);
                    if (placeholderW > 4.0f) {
                        wchar_t placeholder[257];
                        const int pCount = (edit.placeholder && edit.placeholder_length > 0 && edit.placeholder_length <= 256)
                            ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(edit.placeholder), (int)edit.placeholder_length, placeholder, 256)
                            : 0;
                        if (pCount > 0) {
                            placeholder[pCount] = 0;
                            labelWithFont(chromeFont, placeholder, innerLeft + 3.5f, 13.0f + dy, placeholderW, chromePalette.text_muted, 0.58f);
                        } else {
                            labelWithFont(chromeFont, shellName, innerLeft + 3.5f, 13.0f + dy, placeholderW, chromePalette.text_muted, 0.58f);
                        }
                    }
                }
                target->PopAxisAlignedClip();
            }
        }
    } else {
        const bool animatedRows = !movingSidebarClip && (tabs->horizontal || !compact);
        const float textW = width >= 68.0f ? fmaxf(0.0f, width - inset - rightReserve) : 0;
        titleLabel(title, title_length, x + inset, 13.0f + dy, textW, active ? chromePalette.text_primary : chromePalette.text_muted, tabTitleFont,
            { x, row.y, fmaxf(0, width - rightReserve), row.height }, animatedRows ? tabRows[index].title : nullptr,
            animatedRows ? 0.35f + 0.65f * tabRows[index].reveal.sample(chromeTimeMs) : 1.0f);
        if (tabs->horizontal && textW <= 0) {
            const wchar_t number[] = { (wchar_t)(L'1' + index), 0 };
            labelWithFont(tabTitleFont, number, x + 8, 13.0f + dy, fmaxf(0, width - 22),
                active ? chromePalette.text_primary : chromePalette.text_muted);
        }
        if (hasStatus && width >= 88.0f) {
            const float sx = x + width - 39;
            drawProgramStatus(status, sx, 12.0f + dy, 14.0f, true);
            if (status.progress > 0 && status.progress <= 100 && (status.state == 2 || status.state == 4)) {
                brush->SetColor(rgb(programStatusColor(status), 0.8f));
                target->FillRectangle(D2D1::RectF(sx, 28 + dy, sx + status.progress * 0.14f, 29 + dy), brush);
            }
            offerTabStatus(status, { sx - 4, row.y, 21, row.height });
        }
    }
    const float closeX = x + width - 20.0f, closeY = 14.0f + dy;
    const bool closeHot = isHovered && hoveredTabClose;
    const float closeAlpha = index < 8
        ? tabCloseHoverMotion[index].update(closeHot ? 1.0f : 0.0f, chromeTimeMs, chromeMotionEnabled && !compact)
        : (closeHot ? 1.0f : 0.0f);
    if (index < 8) chromeEffectsPending |= tabCloseHoverMotion[index].pending(chromeTimeMs);
    if (closeAlpha > 0.0f) {
        brush->SetColor(rgb(chromePalette.border, closeAlpha));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(closeX, closeY, closeX + 16.0f, closeY + 16.0f), 4.0f, 4.0f), brush);
    }
    const float tabHoverT = index < 8 ? tabHoverMotion[index].sample(chromeTimeMs) : (isHovered ? 1.0f : 0.0f);
    const float closeVisibility = active && (compact || tabs->horizontal) ? 0.76f + 0.24f * closeAlpha : fmaxf(tabHoverT, closeAlpha);
    if (closeVisibility <= 0) return;
    const float cx = closeX + 8.0f, cy = closeY + 8.0f;
    brush->SetColor(rgb(blendRgb(chromePalette.text_muted, chromePalette.text_primary, closeAlpha), closeVisibility));
    target->DrawLine(D2D1::Point2F(cx - 2.9f, cy - 2.9f), D2D1::Point2F(cx + 2.9f, cy + 2.9f), brush, 1.15f, roundCaps);
    target->DrawLine(D2D1::Point2F(cx + 2.9f, cy - 2.9f), D2D1::Point2F(cx - 2.9f, cy + 2.9f), brush, 1.15f, roundCaps);
}
static void drawPaneHeaderActions(VexPaneRect bounds, PaneChromeMotion& motion, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action) {
    if (bounds.width < 180.0f || bounds.height < uiMetrics.pane_header_height) return;
    // Glyphs are drawn for the 20 DIP comfortable button and scaled to the density's button size.
    const float size = uiMetrics.header_action, pill = size - 1.0f, pitch = size + 2.0f, s = size / 20.0f;
    const float right = bounds.x + bounds.width - uiMetrics.header_edge;
    const float top = bounds.y + (uiMetrics.pane_header_height - size - 1.0f) / 2.0f;
    const float cy = top + pill / 2.0f;
    // Slots count from the right edge (0 close, 1 split down, 2 split right, 3 focus) and match hit testing.
    const auto slotX = [&](int slot) { return right - (slot * pitch + size); };
    const uint32_t baseColor = blendRgb(chromePalette.pane_bg, chromePalette.text_muted, focused ? 0.78f : (hovered ? 0.68f : 0.42f));
    float actionAlpha[4] = {};
    for (uint8_t i = 0; i < 4; ++i) {
        const float targetAlpha = (hovered && hovered_action == i + 1) ? 1.0f : 0.0f;
        actionAlpha[i] = motion.action[i].update(targetAlpha, chromeTimeMs, chromeMotionEnabled);
        chromeEffectsPending |= motion.action[i].pending(chromeTimeMs);
    }
    const auto drawButtonPill = [&](float bx, uint32_t fill, float alpha = 1.0f) {
        if (alpha <= 0.0f) return;
        brush->SetColor(rgb(fill, alpha));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, top, bx + size, top + pill), 5.0f * s, 5.0f * s), brush);
    };
    const auto line = [&](float cx, float glyphY, float x1, float y1, float x2, float y2, float width) {
        target->DrawLine(D2D1::Point2F(cx + x1 * s, glyphY + y1 * s), D2D1::Point2F(cx + x2 * s, glyphY + y2 * s), brush, width, roundCaps);
    };
    if (can_focus) {
        const float bx = slotX(3), cx = bx + size / 2.0f;
        if (zoomed) drawButtonPill(bx, chromePalette.active_tab_bg, 1.0f);
        else if (actionAlpha[0] > 0.0f) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg, actionAlpha[0]);
        brush->SetColor(rgb(zoomed ? chromePalette.accent : blendRgb(baseColor, chromePalette.text_primary, actionAlpha[0])));
        if (!zoomed) {
            line(cx, cy, 1.0f, -1.0f, 4.5f, -4.5f, 1.15f);
            line(cx, cy, 1.5f, -4.5f, 4.5f, -4.5f, 1.15f);
            line(cx, cy, 4.5f, -4.5f, 4.5f, -1.5f, 1.15f);
            line(cx, cy, -1.0f, 1.0f, -4.5f, 4.5f, 1.15f);
            line(cx, cy, -1.5f, 4.5f, -4.5f, 4.5f, 1.15f);
            line(cx, cy, -4.5f, 4.5f, -4.5f, 1.5f, 1.15f);
        } else {
            line(cx, cy, 4.5f, -4.5f, 1.0f, -1.0f, 1.15f);
            line(cx, cy, 1.0f, -4.0f, 1.0f, -1.0f, 1.15f);
            line(cx, cy, 1.0f, -1.0f, 4.0f, -1.0f, 1.15f);
            line(cx, cy, -4.5f, 4.5f, -1.0f, 1.0f, 1.15f);
            line(cx, cy, -1.0f, 4.0f, -1.0f, 1.0f, 1.15f);
            line(cx, cy, -1.0f, 1.0f, -4.0f, 1.0f, 1.15f);
        }
    }
    {
        const float bx = slotX(2), cx = bx + size / 2.0f;
        offerTooltip(L"Ctrl + Shift + R", { bx, top, size, pill });
        drawButtonPill(bx, chromePalette.inactive_tab_hover_bg, actionAlpha[1]);
        brush->SetColor(rgb(blendRgb(baseColor, chromePalette.text_primary, actionAlpha[1])));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 5.5f * s, cy - 5.5f * s, cx + 5.5f * s, cy + 5.5f * s), 1.6f * s, 1.6f * s), brush, 1.2f);
        line(cx, cy, 0.0f, -5.5f, 0.0f, 5.5f, 1.2f);
    }
    {
        const float bx = slotX(1), cx = bx + size / 2.0f;
        offerTooltip(L"Ctrl + Shift + D", { bx, top, size, pill });
        drawButtonPill(bx, chromePalette.inactive_tab_hover_bg, actionAlpha[2]);
        brush->SetColor(rgb(blendRgb(baseColor, chromePalette.text_primary, actionAlpha[2])));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 5.5f * s, cy - 5.5f * s, cx + 5.5f * s, cy + 5.5f * s), 1.6f * s, 1.6f * s), brush, 1.2f);
        line(cx, cy, -5.5f, 0.0f, 5.5f, 0.0f, 1.2f);
    }
    {
        const float bx = slotX(0), cx = bx + size / 2.0f;
        // The keyboard chord closes the active pane, never a different hovered pane.
        if (focused) offerTooltip(L"Ctrl + Shift + W", { bx, top, size, pill });
        drawButtonPill(bx, chromePalette.is_dark ? 0x4d2b31 : 0xfad2d8, actionAlpha[3]);
        brush->SetColor(rgb(blendRgb(baseColor, chromePalette.is_dark ? 0xffb4be : 0xb42336, actionAlpha[3])));
        line(cx, cy, -3.3f, -3.3f, 3.3f, 3.3f, 1.15f);
        line(cx, cy, 3.3f, -3.3f, -3.3f, 3.3f, 1.15f);
    }
}
extern "C" void vex_begin_pane(VexPaneRect bounds, VexPaneRect grid, uint32_t pane_id, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action, const uint8_t* title, size_t title_length, VexAppIcon icon, VexProgramStatus status, bool moving, bool single_pane) {
    const bool joined = uiMetrics.pane_gap == 0;
    // Caption centred in the header rule; the 0.665 factor approximates half of Segoe UI's line height.
    const float captionY = bounds.y + (uiMetrics.pane_header_height - 2.0f) / 2.0f - uiMetrics.header_title_size * 0.665f;
    const D2D1_ROUNDED_RECT paneCard = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), joined ? 0.0f : 6.0f, joined ? 0.0f : 6.0f);
    if (!joined) {
        brush->SetColor(rgb(0, chromePalette.is_dark ? 0.18f : 0.05f));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y + 1.5f, bounds.x + bounds.width, bounds.y + bounds.height + 2.0f), 6.0f, 6.0f), brush);
    }
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(paneCard, brush);
    if (single_pane) {
        if (focused) focusedPaneIconKey = computeIconCacheKey(icon, (uint32_t)uiMetrics.header_icon);
        beginTerminalPane(bounds, grid, moving, bounds.y, joined ? 1.5f : 2.5f);
        return;
    }
    auto& motion = paneChromeMotion[pane_id % 64];
    if (motion.id != pane_id) { motion = {}; motion.id = pane_id; }
    const float focus = motion.focus.update(can_focus && focused ? 1.0f : 0.0f, chromeTimeMs, chromeMotionEnabled && !joined);
    chromeEffectsPending |= motion.focus.pending(chromeTimeMs);
    const uint32_t borderCol = blendRgb(chromePalette.border, chromePalette.active_tab_border, focus * 0.45f);
    if (joined) {
        brush->SetColor(rgb(chromePalette.border, 0.42f));
        if (bounds.y > 44.0f) target->DrawLine(D2D1::Point2F(bounds.x, bounds.y + 0.5f), D2D1::Point2F(bounds.x + bounds.width, bounds.y + 0.5f), brush, 1);
        if (bounds.x > windowTabs->sidebar_width) target->DrawLine(D2D1::Point2F(bounds.x + 0.5f, bounds.y), D2D1::Point2F(bounds.x + 0.5f, bounds.y + bounds.height), brush, 1);
    } else {
        brush->SetColor(rgb(borderCol, 0.46f + focus * 0.28f));
        target->DrawRoundedRectangle(paneCard, brush, 1.0f);
    }
    if (activePaneBorderEnabled && focus > 0.0f) {
        // Eased with the focus motion so the ring fades between panes instead of snapping.
        // Inset by half the stroke so the rim stays inside the pane and is never clipped by neighbours.
        const float inset = 0.55f, radius = joined ? 0.0f : 6.0f - inset;
        const uint32_t ring = blendRgb(chromePalette.active_tab_border, chromePalette.accent, chromePalette.is_dark ? 0.50f : 0.65f);
        brush->SetColor(rgb(ring, focus * 0.78f));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x + inset, bounds.y + inset, bounds.x + bounds.width - inset, bounds.y + bounds.height - inset), radius, radius), brush, 1.1f);
    }
    // A revealing pane must not paint its caption or actions over its neighbour.
    target->PushAxisAlignedClip(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    drawPaneHeaderActions(bounds, motion, focused, hovered, can_focus, zoomed, hovered_action);
    const float actionPitch = uiMetrics.header_action + 2.0f;
    const float actionsWidth = (bounds.width >= 180.0f && bounds.height >= uiMetrics.pane_header_height) ? (can_focus ? 3.0f : 2.0f) * actionPitch + uiMetrics.header_action + uiMetrics.header_edge + 2.0f : 0.0f;
    const float remainingWidth = fmaxf(0, bounds.width - actionsWidth);
    const float statusWidth = status.state >= 2 && status.state <= 5 && remainingWidth >= 90 ? 20.0f : 0.0f;
    float titleInset = 10.0f;
    if (bounds.width >= 90.0f && bounds.height >= uiMetrics.pane_header_height) {
        const float iconSize = uiMetrics.header_icon;
        drawAppIcon(icon, bounds.x + uiMetrics.header_inset, bounds.y + (uiMetrics.pane_header_height - 2.0f - iconSize) / 2.0f, iconSize);
        if (focused) focusedPaneIconKey = computeIconCacheKey(icon, (uint32_t)iconSize);
        titleInset = uiMetrics.header_inset + 7.0f + iconSize;
    }
    const float titleWidth = fmaxf(0, remainingWidth - titleInset - statusWidth - 12);
    const float captionWidth = titleLabel(title, title_length, bounds.x + titleInset, captionY, titleWidth,
        focused ? chromePalette.text_primary : chromePalette.text_muted, paneTitleFont, { bounds.x, bounds.y, remainingWidth, uiMetrics.pane_header_height - 2 });
    if (statusWidth > 0 && titleWidth > 0) {
        // Follow short titles closely, but reserve the indicator's space when text is trimmed.
        drawProgramStatus(status, bounds.x + titleInset + captionWidth + 4, captionY, statusWidth, true);
    }
    target->PopAxisAlignedClip();
    // Edge cells may extend until the border and focus ring, which the chrome drew above.
    beginTerminalPane(bounds, grid, moving, bounds.y + uiMetrics.pane_header_height + 2.0f, joined ? 1.5f : 2.5f);
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
extern "C" void vex_draw_copy_feedback(VexPaneRect grid, float cell_width, float cell_height, const VexCopySpan* spans, size_t span_count, float progress) {
    if (!target || !brush || !spans || span_count == 0 || !isfinite(progress) || progress < 0.0f || progress >= 1.0f) return;
    if (!isfinite(cell_width) || !isfinite(cell_height) || cell_width <= 0.0f || cell_height <= 0.0f) return;
    if (chromeMotionEnabled) chromeEffectsPending = true;

    const float gridLeft = grid.x;
    const float gridTop = grid.y;
    const float clipRight = grid.x + grid.width + 2;
    const float clipBottom = grid.y + grid.height + 2;
    if (clipRight <= gridLeft || clipBottom <= gridTop) return;

    target->PushAxisAlignedClip(D2D1::RectF(gridLeft - 2, gridTop - 2, clipRight, clipBottom), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

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
    // Only preview titles are interactive here, never the obscured workspace chrome.
    tooltipRegionCount = 0;
    chromeShortcutsEnabled = false;
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
    fillUiMaterial(*target, *brush, card, chromePalette, chromePalette.pane_bg);
    if (bounds.height >= 28.0f) {
        const uint32_t headerTint = blendRgb(chromePalette.pane_bg, chromePalette.pane_header_bg, selected ? 0.55f : 0.30f);
        target->FillRoundedGradient(D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + 26.0f), 7.0f, 7.0f),
            rgb(materialMix(headerTint, 0xffffff, chromePalette.is_dark ? 0.025f : 0.28f)), rgb(chromePalette.pane_bg));
        brush->SetColor(rgb(chromePalette.border, 0.28f));
        target->FillRectangle(D2D1::RectF(bounds.x + 8.0f, bounds.y + 26.0f, bounds.x + bounds.width - 8.0f, bounds.y + 27.0f), brush);
    }
    if (selected) {
        brush->SetColor(rgb(chromePalette.accent, 0.25f));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x - 1.5f, bounds.y - 1.5f, bounds.x + bounds.width + 1.5f, bounds.y + bounds.height + 1.5f), 8.5f, 8.5f), brush, 2.5f);
    }
    brush->SetColor(rgb(selected ? chromePalette.accent : chromePalette.border));
    target->DrawRoundedRectangle(card, brush, selected ? 1.6f : 1.0f);
    titleLabel(title, length, bounds.x + 10, bounds.y + 4, fmaxf(0, bounds.width - 44),
        selected ? chromePalette.text_primary : chromePalette.text_muted, paneTitleFont, { bounds.x + 2, bounds.y + 2, fmaxf(0, bounds.width - 32), 26 });
}
extern "C" void vex_draw_tab_drag(VexPaneRect bounds, uint32_t source, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, bool valid_target) {
    tooltipsEnabled = false;
    if (!isfinite(bounds.x) || !isfinite(bounds.y) || bounds.width <= 0 || bounds.height <= 0) return;
    // Pane ghosts share tab chrome, but have no source slot in the strip.
    const auto sourceRow = chromeTabRow(*tabs, source);
    if (sourceRow.width > 0) {
        const auto placeholder = tabPill(sourceRow);
        brush->SetColor(rgb(chromePalette.titlebar_bg, 0.84f)); target->FillRoundedRectangle(placeholder, brush);
        brush->SetColor(rgb(chromePalette.border, 0.6f)); target->DrawRoundedRectangle(placeholder, brush, 1);
    }
    const float x = bounds.x, y = bounds.y, w = bounds.width, h = bounds.height;
    brush->SetColor(rgb(0, chromePalette.is_dark ? 0.28f : 0.12f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x - 2, y + 2, x + w + 2, y + h + 5), 12, 12), brush);
    const auto card = D2D1::RoundedRect(D2D1::RectF(x, y, x + w, y + h), 10, 10);
    fillUiMaterial(*target, *brush, card, chromePalette, chromePalette.active_tab_bg);
    brush->SetColor(rgb(valid_target ? chromePalette.accent : chromePalette.text_muted, valid_target ? 0.85f : 0.6f));
    target->DrawRoundedRectangle(card, brush, 1);
    float inset = 10;
    if (icons && icon_count && w >= 68) {
        const size_t visible = w >= 132 ? (icon_count > 3 ? 3 : icon_count) : 1;
        for (size_t i = 0; i < visible; ++i) drawAppIconTile(icons[i], x + 8 + (float)i * 12, y + 8);
        inset = 30 + (float)(visible - 1) * 12;
    }
    titleLabel(title, title_length, x + inset, y + 8, fmaxf(0, w - inset - 20), chromePalette.text_primary, tabTitleFont, {});
    if (w >= 48) {
        brush->SetColor(rgb(chromePalette.text_muted));
        const float cx = x + w - 13, cy = y + 17;
        target->DrawLine(D2D1::Point2F(cx - 3, cy - 3), D2D1::Point2F(cx + 3, cy + 3), brush, 1.2f, roundCaps);
        target->DrawLine(D2D1::Point2F(cx + 3, cy - 3), D2D1::Point2F(cx - 3, cy + 3), brush, 1.2f, roundCaps);
    }
}
extern "C" void vex_draw_pane_drag(VexPaneRect bounds, VexPaneRect detach_target, const uint8_t* title, size_t title_length, VexAppIcon icon, bool valid_target) {
    if (detach_target.width > 0) {
        brush->SetColor(rgb(chromePalette.accent, 0.68f));
        target->DrawRoundedRectangle(tabPill(detach_target), brush, 1.2f);
    }
    vex_draw_tab_drag(bounds, tabs->count, title, title_length, &icon, 1, valid_target || detach_target.width > 0);
}
extern "C" void vex_draw_split_preview(VexPaneRect bounds) {
    if (!target || !brush || !isfinite(bounds.x) || !isfinite(bounds.y) || !isfinite(bounds.width) || !isfinite(bounds.height)) return;
    if (bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    splitPreviewSeenThisFrame = true;
    tooltipsEnabled = false;
    const bool animateFill = chromeMotionEnabled && uiMetrics.pane_gap != 0;
    if (animateFill && !splitPreviewSeenLastFrame) splitPreviewAlphaMotion.update(0.0f, chromeTimeMs, false);
    const float alpha = splitPreviewAlphaMotion.update(1.0f, chromeTimeMs, animateFill);
    chromeEffectsPending |= splitPreviewAlphaMotion.pending(chromeTimeMs);

    // The outline is the hit target, so only its fill may animate.
    const auto rect = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), 6.0f, 6.0f);
    if (alpha > 0.0f) {
        brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f * alpha));
        target->FillRoundedRectangle(rect, brush);
    }
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.78f));
    target->DrawRoundedRectangle(rect, brush, 1.5f);
}
extern "C" void vex_draw_path_completion(VexPaneRect bounds, const uint8_t* directory, size_t directory_length, const uint8_t* query, size_t query_length, const VexPathCompletionItem* items, size_t item_count, uint32_t flags) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    tooltipsEnabled = false;
    const float mx = bounds.x, my = bounds.y, mw = bounds.width, mh = bounds.height;

    for (int layer = 3; layer >= 1; --layer) {
        const float sp = (float)layer * 3.0f;
        brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.16f : 0.07f));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - sp, my + 2.0f - sp * 0.5f, mx + mw + sp, my + mh + 4.0f + sp), 8.0f + sp, 8.0f + sp), brush);
    }
    const D2D1_ROUNDED_RECT card = D2D1::RoundedRect(D2D1::RectF(mx, my, mx + mw, my + mh), 8.0f, 8.0f);
    fillUiMaterial(*target, *brush, card, chromePalette, chromePalette.titlebar_bg);

    target->PushAxisAlignedClip(D2D1::RectF(mx + 4.0f, my + 2.0f, mx + mw - 4.0f, my + mh - 2.0f), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    if (directory && directory_length > 0) {
        // Convert only the visible suffix: long UNC paths must not overflow the
        // conversion buffer, and neither UTF-8 nor UTF-16 characters may be split.
        size_t start = directory_length > 176 ? directory_length - 176 : 0;
        while (start < directory_length && (directory[start] & 0xc0) == 0x80) ++start;
        wchar_t dirBuf[180];
        const int wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(directory + start), (int)(directory_length - start), dirBuf + 1, 178);
        if (wlen > 0) {
            int skip = wlen > 44 ? wlen - 44 : 0;
            if (skip && dirBuf[skip + 1] >= 0xdc00 && dirBuf[skip + 1] <= 0xdfff) ++skip;
            wchar_t* displayDir = dirBuf + 1 + skip;
            if (start || skip) *--displayDir = L'\x2026';
            dirBuf[wlen + 1] = L'\0';
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
        const uint32_t selFill = blendRgb(chromePalette.active_tab_bg, chromePalette.accent, chromePalette.is_dark ? 0.28f : 0.16f);
        for (size_t i = 0; i < visible; i++) {
            const VexPathCompletionItem& it = items[i];
            const float iy = listY + (float)i * rowH;
            if (it.selected) {
                const auto selection = D2D1::RoundedRect(D2D1::RectF(mx + 6.0f, iy, mx + mw - 6.0f, iy + rowH - 2.0f), 5.0f, 5.0f);
                brush->SetColor(rgb(selFill));
                target->FillRoundedRectangle(selection, brush);
                brush->SetColor(rgb(chromePalette.accent, chromePalette.is_dark ? 0.50f : 0.38f));
                target->DrawRoundedRectangle(selection, brush, 1.0f);
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 8.5f, iy + 5.0f, mx + 11.5f, iy + rowH - 7.0f), 1.5f, 1.5f), brush);
            } else if (it.hovered) {
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
    const wchar_t* hint = (flags & 4) ? ((flags & 8) ? L"Index limit reached; some paths skipped" : L"Index limit reached; narrow the folder")
        : (flags & 8) || (flags & 1) ? L"Some paths could not be read or indexed"
        : L"\x2190 Parent   Tab Open   Enter Insert   Esc Close";
    labelWithFont(badgeFont, hint, mx + 12, my + mh - 22, mw - 24, chromePalette.text_muted);
    target->PopAxisAlignedClip();
}
extern "C" void vex_window_set_active_pane_border(bool enabled) {
    activePaneBorderEnabled = enabled;
    vex_window_invalidate();
}
