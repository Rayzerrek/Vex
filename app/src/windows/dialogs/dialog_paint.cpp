#include "dialog_internal.h"
#include <gdiplus.h>
#include <math.h>
#include <d2d1.h>
#include "app_icon_geometry.generated.h"

namespace vex::dialog {
COLORREF color(uint32_t rgb) { return RGB((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
int px(float dip) { return (int)lroundf(dip * host.scale); }
COLORREF blend(COLORREF a, COLORREF b, float amount) {
    return RGB((BYTE)lroundf(GetRValue(a) + (GetRValue(b) - GetRValue(a)) * amount),
               (BYTE)lroundf(GetGValue(a) + (GetGValue(b) - GetGValue(a)) * amount),
               (BYTE)lroundf(GetBValue(a) + (GetBValue(b) - GetBValue(a)) * amount));
}
COLORREF surfaceColor() { return blend(color(host.model.palette.pane_bg), color(host.model.palette.text_primary), host.model.palette.is_dark ? 0.045f : 0.015f); }
COLORREF fieldColor() { return blend(surfaceColor(), color(host.model.palette.text_primary), 0.055f); }
COLORREF selectionColor() { return blend(surfaceColor(), color(host.model.palette.accent), host.model.palette.is_dark ? 0.44f : 0.20f); }
int listRowHeight() { return host.model.kind == 9 ? 64 : host.model.kind == 2 ? 32 : 58; }
int themeRowHeight() {
    const float available = (host.model.kind == 4 ? host.height - (host.height < 430 ? 198 : 246) : host.height - 170) - 16;
    return (int)fmaxf(104, floorf(available / 2));
}
int themeColumnWidth() { return (int)floorf((host.width - 32) / 3); }
void drawSymbol(HDC dc, const wchar_t* symbol, RECT bounds, COLORREF ink) {
    HGDIOBJ old = SelectObject(dc, host.symbols);
    SetBkMode(dc, TRANSPARENT); SetTextColor(dc, ink);
    DrawTextW(dc, symbol, -1, &bounds, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    SelectObject(dc, old);
}
void roundedFill(HDC dc, RECT bounds, COLORREF fill, COLORREF border, int diameter) {
    const float x = bounds.left + 0.5f, y = bounds.top + 0.5f;
    const float w = (float)(bounds.right - bounds.left - 1), h = (float)(bounds.bottom - bounds.top - 1);
    if (w <= 0 || h <= 0) return;
    const float arc = fminf((float)diameter, fminf(w, h));
    Gdiplus::Graphics graphics(dc);
    graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
    Gdiplus::GraphicsPath path;
    if (arc > 0) {
        path.AddArc(x, y, arc, arc, 180, 90);
        path.AddArc(x + w - arc, y, arc, arc, 270, 90);
        path.AddArc(x + w - arc, y + h - arc, arc, arc, 0, 90);
        path.AddArc(x, y + h - arc, arc, arc, 90, 90);
        path.CloseFigure();
    } else path.AddRectangle(Gdiplus::RectF(x, y, w, h));
    Gdiplus::SolidBrush brush(Gdiplus::Color(255, GetRValue(fill), GetGValue(fill), GetBValue(fill)));
    graphics.FillPath(&brush, &path);
    if (fill != border) {
        Gdiplus::Pen pen(Gdiplus::Color(255, GetRValue(border), GetGValue(border), GetBValue(border)), 1.0f);
        graphics.DrawPath(&pen, &path);
    }
}
void drawThemeCard(const DRAWITEMSTRUCT& draw, const DialogItem& item, bool selected) {
    ++host.themeCardPaintCount;
    const COLORREF ink = color(host.model.palette.text_primary);
    RECT card = draw.rcItem; InflateRect(&card, -px(5), -px(5));
    const COLORREF border = color(selected ? host.model.palette.accent : host.model.palette.border);
    roundedFill(draw.hDC, card, selected ? border : surfaceColor(), border, px(22));
    RECT inner = card; InflateRect(&inner, -px(2), -px(2));
    roundedFill(draw.hDC, inner, surfaceColor(), surfaceColor(), px(19));
    RECT art = inner; InflateRect(&art, -px(5), -px(5)); art.bottom -= px(45);
    if (art.bottom > art.top) {
        Gdiplus::Graphics graphics(draw.hDC);
        graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        const float x = (float)art.left, y = (float)art.top;
        const float w = (float)(art.right - art.left), h = (float)(art.bottom - art.top);
        const float arc = fminf((float)px(14), fminf(w, h));
        Gdiplus::GraphicsPath path;
        path.AddArc(x, y, arc, arc, 180, 90);
        path.AddArc(x + w - arc, y, arc, arc, 270, 90);
        path.AddArc(x + w - arc, y + h - arc, arc, arc, 0, 90);
        path.AddArc(x, y + h - arc, arc, arc, 90, 90); path.CloseFigure();
        const bool dark = host.model.palette.is_dark;
        const COLORREF start = blend(item.background, item.accent, dark ? 0.22f : 0.12f);
        const COLORREF end = blend(item.background, item.swatches[4], dark ? 0.48f : 0.26f);
        Gdiplus::LinearGradientBrush gradient(Gdiplus::PointF(x, y), Gdiplus::PointF(x + w, y + h),
            Gdiplus::Color(255, GetRValue(start), GetGValue(start), GetBValue(start)),
            Gdiplus::Color(255, GetRValue(end), GetGValue(end), GetBValue(end)));
        graphics.FillPath(&gradient, &path);
    }
    if (art.bottom - art.top >= px(62)) {
        RECT terminal = art; terminal.left += px(13); terminal.right -= px(13); terminal.top += px(12); terminal.bottom -= px(10);
        roundedFill(draw.hDC, terminal, item.background, blend(item.background, item.foreground, 0.18f), px(10));
        for (int i = 0; i < 3; ++i) {
            RECT dot = { terminal.left + px(9 + i * 8), terminal.top + px(8), terminal.left + px(13 + i * 8), terminal.top + px(12) };
            COLORREF tint = blend(item.background, item.foreground, 0.28f);
            roundedFill(draw.hDC, dot, tint, tint, px(4));
        }
        RECT prompt = terminal; prompt.left += px(9); prompt.top += px(21); prompt.right -= px(6);
        SetTextColor(draw.hDC, item.accent);
        DrawTextW(draw.hDC, L"❯  ~/vex", -1, &prompt, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    }
    RECT name = inner; name.left += px(10); name.right -= px(25); name.top = art.bottom + px(7); name.bottom = name.top + px(20);
    SetTextColor(draw.hDC, ink);
    DrawTextW(draw.hDC, item.label, -1, &name, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    RECT detail = name; detail.top += px(20); detail.bottom += px(20); detail.right = inner.right - px(10);
    SetTextColor(draw.hDC, color(host.model.palette.text_muted));
    DrawTextW(draw.hDC, item.detail, -1, &detail, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    if (selected) {
        RECT check = name; check.left = inner.right - px(24); check.right = inner.right - px(6);
        drawSymbol(draw.hDC, L"\xE73E", check, color(host.model.palette.accent));
    }
}
HBITMAP themeCardImage(const DRAWITEMSTRUCT& draw, const DialogItem& item, bool selected) {
    auto& cached = host.themeCards[draw.itemID];
    const int width = draw.rcItem.right - draw.rcItem.left, height = draw.rcItem.bottom - draw.rcItem.top;
    if (cached.width != width || cached.height != height || cached.scale != host.scale) {
        for (auto& image : cached.images) if (image) { DeleteObject(image); image = nullptr; }
        cached.width = width; cached.height = height; cached.scale = host.scale;
    }
    HBITMAP& image = cached.images[selected ? 1 : 0];
    if (image) return image;
    HDC memory = CreateCompatibleDC(draw.hDC);
    if (memory) {
        image = CreateCompatibleBitmap(draw.hDC, width, height);
        if (image) {
            HGDIOBJ previous = SelectObject(memory, image);
            DRAWITEMSTRUCT frame = draw; frame.hDC = memory; frame.rcItem = { 0, 0, width, height };
            FillRect(memory, &frame.rcItem, host.background);
            SetBkMode(memory, TRANSPARENT); SelectObject(memory, host.font);
            drawThemeCard(frame, item, selected);
            SelectObject(memory, previous);
        }
        DeleteDC(memory);
    }
    return image;
}
void drawProgramIcon(HDC dc, VexAppIcon icon, RECT bounds) {
    const COLORREF ink = icon.kind ? color(icon.color_rgb) : color(host.model.palette.text_muted);
    const COLORREF tile = blend(surfaceColor(), ink, 0.08f);
    roundedFill(dc, bounds, tile, tile, px(12));
    const float x = (float)bounds.left + px(6), y = (float)bounds.top + px(6);
    const float size = (float)(bounds.right - bounds.left) - px(12);
    if (icon.kind == 1 && icon.glyph < sizeof(appIconGeometries) / sizeof(appIconGeometries[0]) && appIconGeometries[icon.glyph].figureCount) {
        const auto& geometry = appIconGeometries[icon.glyph];
        Gdiplus::Graphics graphics(dc);
        graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        Gdiplus::GraphicsPath path(geometry.fill == D2D1_FILL_MODE_WINDING ? Gdiplus::FillModeWinding : Gdiplus::FillModeAlternate);
        for (uint32_t i = 0; i < geometry.figureCount; ++i) {
            const auto& figure = geometry.figures[i];
            path.StartFigure();
            for (uint32_t j = 1; j < figure.count; ++j) {
                const auto a = geometry.points[figure.offset + j - 1], b = geometry.points[figure.offset + j];
                path.AddLine(x + a.x * size / 24.0f, y + a.y * size / 24.0f, x + b.x * size / 24.0f, y + b.y * size / 24.0f);
            }
            if (figure.closed) path.CloseFigure();
        }
        Gdiplus::SolidBrush brush(Gdiplus::Color(255, GetRValue(ink), GetGValue(ink), GetBValue(ink)));
        graphics.FillPath(&brush, &path);
    } else if (icon.kind) {
        wchar_t badge[5] = {};
        if (icon.kind == 2) for (int i = 0; i < icon.badge_len && i < 4; ++i) badge[i] = icon.badge[i];
        else if (icon.glyph < sizeof(appIconGeometries) / sizeof(appIconGeometries[0])) {
            const char* text = appIconGeometries[icon.glyph].badge;
            for (int i = 0; i < 4 && text[i]; ++i) badge[i] = text[i];
        }
        SelectObject(dc, host.font); SetTextColor(dc, ink);
        DrawTextW(dc, badge, -1, &bounds, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    } else drawSymbol(dc, L"\xE756", bounds, ink);
}
void drawListItem(const DRAWITEMSTRUCT& draw) {
    if (draw.itemID == (UINT)-1 || draw.itemID >= host.model.count) return;
    const DialogItem& item = listItem(draw.itemID);
    const bool selected = (draw.itemState & ODS_SELECTED) != 0;
    if (host.model.kind == 9) {
        FillRect(draw.hDC, &draw.rcItem, host.background);
        RECT row = draw.rcItem; InflateRect(&row, -px(4), 0);
        if (selected && GetFocus() == draw.hwndItem) roundedFill(draw.hDC, row, fieldColor(), color(host.model.palette.accent), px(10));
        RECT icon = { row.left + px(8), row.top + px(15), row.left + px(40), row.top + px(47) };
        drawProgramIcon(draw.hDC, item.icon, icon);
        RECT name = { row.left + px(54), row.top + px(10), row.right - px(12), row.top + px(31) };
        SelectObject(draw.hDC, host.font); SetBkMode(draw.hDC, TRANSPARENT);
        SetTextColor(draw.hDC, color(host.model.palette.text_primary));
        DrawTextW(draw.hDC, item.label, -1, &name, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
        RECT detail = name; detail.top += px(23); detail.bottom += px(23);
        SetTextColor(draw.hDC, color(host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.detail, -1, &detail, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
        return;
    }
    const bool theme = host.model.kind == 1 || draw.CtlID == themeGridId;
    if (theme) {
        HBITMAP image = themeCardImage(draw, item, selected);
        const int width = draw.rcItem.right - draw.rcItem.left, height = draw.rcItem.bottom - draw.rcItem.top;
        HDC memory = image ? CreateCompatibleDC(draw.hDC) : nullptr;
        if (memory) {
            HGDIOBJ previous = SelectObject(memory, image);
            BitBlt(draw.hDC, draw.rcItem.left, draw.rcItem.top, width, height, memory, 0, 0, SRCCOPY);
            SelectObject(memory, previous);
        } else {
            DialogPaintBuffer buffer(draw.hDC, draw.rcItem);
            DRAWITEMSTRUCT frame = draw; frame.hDC = buffer.dc;
            FillRect(frame.hDC, &frame.rcItem, host.background);
            SetBkMode(frame.hDC, TRANSPARENT); SelectObject(frame.hDC, host.font);
            drawThemeCard(frame, item, selected);
        }
        if (memory) DeleteDC(memory);
        return;
    }
    FillRect(draw.hDC, &draw.rcItem, host.background);
    SetBkMode(draw.hDC, TRANSPARENT);
    SelectObject(draw.hDC, host.font);
    RECT row = draw.rcItem;
    row.top += px(host.model.kind == 5 ? 3 : 2);
    row.bottom -= px(host.model.kind == 5 ? 3 : 2);
    row.left += px(2);
    row.right -= px(3);
    const bool commands = host.model.kind == 2;
    const bool shells = host.model.kind == 5;
    const bool dark = host.model.palette.is_dark;
    COLORREF fill = selected
        ? (commands ? blend(surfaceColor(), color(host.model.palette.accent), dark ? 0.34f : 0.18f)
            : shells ? blend(surfaceColor(), color(host.model.palette.accent), dark ? 0.24f : 0.14f)
            : selectionColor())
        : (shells ? fieldColor() : surfaceColor());
    COLORREF stroke = selected
        ? (commands ? blend(surfaceColor(), color(host.model.palette.accent), dark ? 0.56f : 0.32f)
            : shells ? blend(surfaceColor(), color(host.model.palette.accent), dark ? 0.68f : 0.42f)
            : fill)
        : (shells ? blend(surfaceColor(), color(host.model.palette.border), dark ? 0.68f : 0.52f) : fill);
    roundedFill(draw.hDC, row, fill, stroke, px(commands ? 8 : 14));
    RECT text = row; text.left += px(12); text.right -= px(12);
    if (commands) {
        RECT shortcut = text;
        SetTextColor(draw.hDC, color(selected ? host.model.palette.text_primary : host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.detail, -1, &shortcut, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        const int shortcutWidth = shortcut.right - shortcut.left;
        shortcut = text; shortcut.left = shortcut.right - shortcutWidth;
        DrawTextW(draw.hDC, item.detail, -1, &shortcut, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        if (shortcutWidth) text.right = shortcut.left - px(20);
        SetTextColor(draw.hDC, color(host.model.palette.text_primary));
        DrawTextW(draw.hDC, item.label, -1, &text, DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
    } else if (shells) {
        COLORREF brand = color(host.model.palette.accent);
        const wchar_t* envDesc = item.detail;
        switch (item.value) {
        case 1:
            brand = dark ? RGB(148, 163, 184) : RGB(100, 116, 139);
            envDesc = L"Windows Command Processor";
            break;
        case 2:
            brand = dark ? RGB(59, 130, 246) : RGB(37, 99, 235);
            envDesc = L"Windows PowerShell 5.1";
            break;
        case 3:
            brand = dark ? RGB(56, 189, 248) : RGB(14, 142, 208);
            envDesc = L"PowerShell Core · Cross-platform";
            break;
        case 4:
            brand = dark ? RGB(52, 211, 153) : RGB(16, 163, 108);
            envDesc = L"Structured data shell";
            break;
        case 5:
            brand = dark ? RGB(249, 115, 82) : RGB(218, 78, 46);
            envDesc = L"MINGW64 Bourne Again Shell";
            break;
        case 7:
            brand = dark ? RGB(251, 146, 60) : RGB(217, 107, 27);
            envDesc = L"Windows Subsystem for Linux";
            break;
        }
        RECT icon = { row.left + px(11), row.top + px(8), row.left + px(47), row.bottom - px(8) };
        COLORREF tileFill = blend(fill, brand, dark ? 0.20f : 0.14f);
        COLORREF tileStroke = blend(fill, brand, dark ? 0.46f : 0.32f);
        roundedFill(draw.hDC, icon, tileFill, tileStroke, px(12));
        {
            Gdiplus::Graphics g(draw.hDC);
            g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
            Gdiplus::Color gc(255, GetRValue(brand), GetGValue(brand), GetBValue(brand));
            Gdiplus::Pen pen(gc, host.scale * 1.85f);
            pen.SetStartCap(Gdiplus::LineCapRound);
            pen.SetEndCap(Gdiplus::LineCapRound);
            pen.SetLineJoin(Gdiplus::LineJoinRound);
            const float cx = (icon.left + icon.right) * 0.5f;
            const float cy = (icon.top + icon.bottom) * 0.5f;
            const float s = host.scale;
            if (item.value == 5) {
                // Git branch vector glyph
                g.DrawLine(&pen, cx - 4.5f * s, cy - 6.0f * s, cx - 4.5f * s, cy + 6.0f * s);
                g.DrawLine(&pen, cx - 4.5f * s, cy + 1.5f * s, cx + 4.5f * s, cy - 2.5f * s);
                g.DrawLine(&pen, cx + 4.5f * s, cy - 2.5f * s, cx + 4.5f * s, cy - 5.5f * s);
                Gdiplus::SolidBrush dotBrush(gc);
                g.FillEllipse(&dotBrush, cx - 6.8f * s, cy - 8.2f * s, 4.6f * s, 4.6f * s);
                g.FillEllipse(&dotBrush, cx - 6.8f * s, cy + 3.8f * s, 4.6f * s, 4.6f * s);
                g.FillEllipse(&dotBrush, cx + 2.2f * s, cy - 8.2f * s, 4.6f * s, 4.6f * s);
            } else if (item.value == 7) {
                // WSL / Linux tilde + prompt
                Gdiplus::PointF wave[4] = {
                    Gdiplus::PointF(cx - 7.5f * s, cy + 0.5f * s),
                    Gdiplus::PointF(cx - 3.5f * s, cy - 4.5f * s),
                    Gdiplus::PointF(cx + 0.5f * s, cy + 3.5f * s),
                    Gdiplus::PointF(cx + 4.5f * s, cy - 1.5f * s),
                };
                g.DrawCurve(&pen, wave, 4, 0.5f);
                g.DrawLine(&pen, cx + 3.0f * s, cy + 4.8f * s, cx + 8.0f * s, cy + 4.8f * s);
            } else if (item.value == 4) {
                // Nushell chevron + pipe
                Gdiplus::PointF chev[3] = {
                    Gdiplus::PointF(cx - 6.5f * s, cy - 4.8f * s),
                    Gdiplus::PointF(cx - 1.0f * s, cy),
                    Gdiplus::PointF(cx - 6.5f * s, cy + 4.8f * s),
                };
                g.DrawLines(&pen, chev, 3);
                g.DrawLine(&pen, cx + 4.0f * s, cy - 5.5f * s, cx + 4.0f * s, cy + 5.5f * s);
            } else {
                // Terminal prompt chevron >_
                Gdiplus::PointF chev[3] = {
                    Gdiplus::PointF(cx - 6.8f * s, cy - 5.0f * s),
                    Gdiplus::PointF(cx - 0.8f * s, cy + 0.2f * s),
                    Gdiplus::PointF(cx - 6.8f * s, cy + 5.2f * s),
                };
                g.DrawLines(&pen, chev, 3);
                g.DrawLine(&pen, cx + 1.5f * s, cy + 5.2f * s, cx + 7.2f * s, cy + 5.2f * s);
                if (item.value == 3) {
                    // PowerShell 7 core accent spark dot
                    Gdiplus::SolidBrush sparkBrush(gc);
                    g.FillEllipse(&sparkBrush, cx + 4.2f * s, cy - 6.8f * s, 3.8f * s, 3.8f * s);
                }
            }
        }

        RECT cmdCalc = text;
        DrawTextW(draw.hDC, item.detail, -1, &cmdCalc, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        const int cmdWidth = (cmdCalc.right - cmdCalc.left) + px(16);
        const int midY = (row.top + row.bottom) / 2;
        RECT cmdPill = { row.right - px(10) - cmdWidth, midY - px(11), row.right - px(10), midY + px(11) };
        if (cmdPill.left > row.left + px(170)) {
            COLORREF pillFill = selected
                ? blend(fill, color(host.model.palette.accent), dark ? 0.22f : 0.14f)
                : blend(fill, color(host.model.palette.pane_bg), 0.75f);
            COLORREF pillStroke = selected
                ? blend(fill, color(host.model.palette.accent), dark ? 0.48f : 0.30f)
                : blend(fill, color(host.model.palette.border), 0.70f);
            roundedFill(draw.hDC, cmdPill, pillFill, pillStroke, px(8));
            SetTextColor(draw.hDC, color(selected ? host.model.palette.text_primary : host.model.palette.text_muted));
            DrawTextW(draw.hDC, item.detail, -1, &cmdPill, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            text.right = cmdPill.left - px(10);
        }

        text.left = row.left + px(58);
        RECT titleRect = { text.left, row.top + px(6), text.right, row.top + px(27) };
        SetTextColor(draw.hDC, color(host.model.palette.text_primary));
        DrawTextW(draw.hDC, item.label, -1, &titleRect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);

        if (item.value == host.model.settings.shell) {
            RECT nameCalc = titleRect;
            DrawTextW(draw.hDC, item.label, -1, &nameCalc, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
            RECT defPill = { nameCalc.right + px(8), row.top + px(8), nameCalc.right + px(64), row.top + px(25) };
            if (defPill.right <= text.right) {
                COLORREF defFill = blend(fill, color(host.model.palette.accent), dark ? 0.24f : 0.15f);
                COLORREF defStroke = blend(fill, color(host.model.palette.accent), dark ? 0.52f : 0.35f);
                roundedFill(draw.hDC, defPill, defFill, defStroke, px(6));
                SetTextColor(draw.hDC, color(host.model.palette.accent));
                DrawTextW(draw.hDC, L"default", -1, &defPill, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            }
        }

        RECT subRect = { text.left, row.top + px(27), text.right, row.bottom - px(5) };
        SetTextColor(draw.hDC, color(host.model.palette.text_muted));
        DrawTextW(draw.hDC, envDesc, -1, &subRect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    } else {
        text.top += px(8);
        SetTextColor(draw.hDC, color(host.model.palette.text_primary));
        DrawTextW(draw.hDC, item.label, -1, &text, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        text.top += px(21); SetTextColor(draw.hDC, color(host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.detail, -1, &text, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    }
    if ((shells || GetFocus() == draw.hwndItem) && selected) {
        RECT mark = row; mark.left += px(3); mark.right = mark.left + px(3); mark.top += px(10); mark.bottom -= px(10);
        roundedFill(draw.hDC, mark, color(host.model.palette.accent), color(host.model.palette.accent), px(3));
    }
}
void drawDialogButton(const DRAWITEMSTRUCT& draw) {
    const int id = (int)draw.CtlID;
    const bool navigation = id >= firstPageId && id < firstPageId + 4;
    const bool toggle = id == blinkId || id == copyOnSelectId || id == confirmCloseId;
    const bool checked = navigation ? (int)host.page == id - firstPageId : id == blinkId ? host.draft.cursor_blink : id == confirmCloseId ? host.draft.confirm_close : id == copyOnSelectId && host.draft.copy_on_select;
    const bool pressed = (draw.itemState & ODS_SELECTED) != 0;
    RECT bounds = draw.rcItem; InflateRect(&bounds, -1, -1);
    FillRect(draw.hDC, &draw.rcItem, toggle ? host.field : host.background);
    COLORREF surface = checked || pressed ? fieldColor() : surfaceColor();
    if (toggle) surface = color(host.model.palette.pane_bg);
    COLORREF border = navigation && checked ? color(host.model.palette.border) : surface;
    if (GetFocus() == draw.hwndItem) border = color(host.model.palette.accent);
    const bool primary = id == IDOK;
    const bool closing = destructiveDialog();
    if (primary) {
        surface = closing ? (host.model.palette.is_dark ? RGB(158, 57, 65) : RGB(177, 47, 59)) : selectionColor();
        if (pressed) surface = blend(surface, color(host.model.palette.text_primary), 0.16f);
        border = GetFocus() == draw.hwndItem ? color(host.model.palette.text_primary) : surface;
    }
    if (closing && !primary) border = GetFocus() == draw.hwndItem ? color(host.model.palette.accent) : color(host.model.palette.border);
    roundedFill(draw.hDC, bounds, surface, border, px(navigation ? 22 : closing ? 12 : 14));
    if (id == closeId) {
        Gdiplus::Graphics graphics(draw.hDC);
        graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        const COLORREF ink = color(pressed ? host.model.palette.text_primary : host.model.palette.text_muted);
        Gdiplus::Pen pen(Gdiplus::Color(255, GetRValue(ink), GetGValue(ink), GetBValue(ink)), 1.35f * host.scale);
        pen.SetStartCap(Gdiplus::LineCapRound); pen.SetEndCap(Gdiplus::LineCapRound);
        const float cx = (bounds.left + bounds.right) * 0.5f, cy = (bounds.top + bounds.bottom) * 0.5f;
        const float radius = 4.5f * host.scale;
        graphics.DrawLine(&pen, cx - radius, cy - radius, cx + radius, cy + radius);
        graphics.DrawLine(&pen, cx + radius, cy - radius, cx - radius, cy + radius);
        return;
    }
    SetBkMode(draw.hDC, TRANSPARENT); SelectObject(draw.hDC, host.font);
    SetTextColor(draw.hDC, primary && closing ? RGB(255, 255, 255) : color(checked && navigation ? host.model.palette.accent : host.model.palette.text_primary));
    wchar_t label[96]; GetWindowTextW(draw.hwndItem, label, 96);
    if (navigation) {
        const wchar_t* icons[] = { L"\xE713", L"\xE790", L"\xE756", L"\xE946" };
        RECT icon = bounds; icon.bottom = icon.top + px(host.height < 430 ? 24 : 34);
        drawSymbol(draw.hDC, icons[id - firstPageId], icon, color(checked ? host.model.palette.accent : host.model.palette.text_muted));
        bounds.top = icon.bottom;
        SetTextColor(draw.hDC, color(checked ? host.model.palette.accent : host.model.palette.text_muted));
    }
    if (toggle) {
        const auto now = GetTickCount64();
        auto& motion = id == blinkId ? host.cursorBlinkMotion : id == confirmCloseId ? host.confirmCloseMotion : host.copyOnSelectMotion;
        float position = motion.update(checked ? 1.0f : 0.0f, now, vex_window_animations_enabled());
        if (motion.pending(now) && !SetTimer(host.window, 8, 16, nullptr))
            position = motion.update(checked ? 1.0f : 0.0f, now, false);
        RECT toggle = bounds;
        toggle.right -= px(12); toggle.left = toggle.right - px(36);
        toggle.top = (bounds.top + bounds.bottom - px(20)) / 2; toggle.bottom = toggle.top + px(20);
        const COLORREF track = blend(fieldColor(), color(host.model.palette.accent), position);
        const COLORREF stroke = blend(color(host.model.palette.border), color(host.model.palette.accent), position);
        roundedFill(draw.hDC, toggle, track, stroke, px(20));
        Gdiplus::Graphics graphics(draw.hDC);
        graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        const COLORREF thumb = host.model.palette.is_dark ? RGB(238, 241, 244) : RGB(255, 255, 255);
        Gdiplus::SolidBrush ink(Gdiplus::Color(255, GetRValue(thumb), GetGValue(thumb), GetBValue(thumb)));
        graphics.FillEllipse(&ink, toggle.left + host.scale * (3.0f + 16.0f * position), toggle.top + host.scale * 3.0f, host.scale * 14.0f, host.scale * 14.0f);
        bounds.right -= px(60);
    }
    if (toggle) bounds.left += px(12);
    DrawTextW(draw.hDC, label, -1, &bounds, DT_SINGLELINE | DT_VCENTER | (toggle ? DT_LEFT : DT_CENTER) | DT_NOPREFIX);

}
void drawComboItem(const DRAWITEMSTRUCT& draw) {
    RECT bounds = draw.rcItem;
    const COLORREF fill = (draw.itemState & ODS_SELECTED) ? selectionColor() : fieldColor();
    roundedFill(draw.hDC, bounds, fill, fill, 0);
    if (draw.itemID == (UINT)-1) return;
    wchar_t text[256] = {};
    SendMessageW(draw.hwndItem, CB_GETLBTEXT, draw.itemID, reinterpret_cast<LPARAM>(text));
    bounds.left += px(12); bounds.right -= px(10);
    SelectObject(draw.hDC, host.font); SetBkMode(draw.hDC, TRANSPARENT); SetTextColor(draw.hDC, color(host.model.palette.text_primary));
    DrawTextW(draw.hDC, text, -1, &bounds, DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
}
void paintDialogSurface(HDC dc) {
    RECT bounds; GetClientRect(host.window, &bounds);
    FillRect(dc, &bounds, host.background);
    roundedFill(dc, bounds, surfaceColor(), color(host.model.palette.border), px(24));
    if (host.model.kind == 4 && (host.page == SettingsPage::general || host.page == SettingsPage::terminal)) {
        const bool compact = host.height < 430;
        const int cardBottom = host.page == SettingsPage::terminal ? 422 - (compact ? 46 : 0) : compact ? 350 : 508;
        RECT card = { px(28), px(compact ? 176 : 222), bounds.right - px(28), px(cardBottom) };
        roundedFill(dc, card, color(host.model.palette.pane_bg), color(host.model.palette.pane_bg), px(28));
        if (host.page == SettingsPage::terminal) {
            HWND edit = GetDlgItem(host.window, sizeId);
            RECT field; GetWindowRect(edit, &field);
            MapWindowPoints(nullptr, host.window, reinterpret_cast<POINT*>(&field), 2);
            InflateRect(&field, px(8), px(6));
            roundedFill(dc, field, fieldColor(), color(GetFocus() == edit ? host.model.palette.accent : host.model.palette.border), px(12));
        }
    }
    if (host.model.kind == 7) {
        const int fields[] = { shellNameId, shellProgramId, shellArgumentsId };
        for (int id : fields) {
            RECT field; GetWindowRect(GetDlgItem(host.window, id), &field);
            MapWindowPoints(nullptr, host.window, reinterpret_cast<POINT*>(&field), 2);
            InflateRect(&field, px(8), px(5));
            roundedFill(dc, field, fieldColor(), color(GetFocus() == GetDlgItem(host.window, id) ? host.model.palette.accent : host.model.palette.border), px(12));
        }
    }
    if (host.model.kind == 2) {
        RECT search = { px(12), px(12), bounds.right - px(12), px(52) };
        roundedFill(dc, search, fieldColor(), fieldColor(), px(20));
        {
            Gdiplus::Graphics graphics(dc);
            graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
            COLORREF ink = color(host.model.palette.text_muted);
            Gdiplus::Pen pen(Gdiplus::Color(255, GetRValue(ink), GetGValue(ink), GetBValue(ink)), host.scale * 1.5f);
            graphics.DrawEllipse(&pen, (float)px(26), (float)px(23), (float)px(12), (float)px(12));
            graphics.DrawLine(&pen, (float)px(36), (float)px(33), (float)px(41), (float)px(38));
        }
        if (!host.model.count) {
            RECT empty = { px(24), px(78), bounds.right - px(24), px(120) };
            SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color(host.model.palette.text_muted)); SelectObject(dc, host.font);
            DrawTextW(dc, L"No matching commands", -1, &empty, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        }
        RECT rule = { px(16), bounds.bottom - px(43), bounds.right - px(16), bounds.bottom - px(43) + 1 };
        SetDCBrushColor(dc, color(host.model.palette.border));
        FillRect(dc, &rule, reinterpret_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        RECT footer = { px(20), bounds.bottom - px(38), bounds.right - px(20), bounds.bottom - px(8) };
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color(host.model.palette.text_muted)); SelectObject(dc, host.font);
        DrawTextW(dc, L"↑ ↓  Navigate", -1, &footer, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        DrawTextW(dc, L"Enter  Run     Esc  Close", -1, &footer, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }
    if (host.model.kind == 5) {
        RECT icon = { px(18), px(14), px(54), px(50) };
        COLORREF accent = color(host.model.palette.accent);
        COLORREF tint = blend(surfaceColor(), accent, host.model.palette.is_dark ? 0.20f : 0.12f);
        COLORREF border = blend(surfaceColor(), accent, host.model.palette.is_dark ? 0.45f : 0.28f);
        roundedFill(dc, icon, tint, border, px(12));
        {
            Gdiplus::Graphics g(dc);
            g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
            Gdiplus::Pen pen(Gdiplus::Color(255, GetRValue(accent), GetGValue(accent), GetBValue(accent)), host.scale * 1.85f);
            pen.SetStartCap(Gdiplus::LineCapRound);
            pen.SetEndCap(Gdiplus::LineCapRound);
            pen.SetLineJoin(Gdiplus::LineJoinRound);
            const float cx = (icon.left + icon.right) * 0.5f;
            const float cy = (icon.top + icon.bottom) * 0.5f;
            const float s = host.scale;
            Gdiplus::PointF chev[3] = {
                Gdiplus::PointF(cx - 6.0f * s, cy - 4.5f * s),
                Gdiplus::PointF(cx - 0.5f * s, cy + 0.2f * s),
                Gdiplus::PointF(cx - 6.0f * s, cy + 4.8f * s),
            };
            g.DrawLines(&pen, chev, 3);
            g.DrawLine(&pen, cx + 1.5f * s, cy + 4.8f * s, cx + 6.8f * s, cy + 4.8f * s);
        }
        SetBkMode(dc, TRANSPARENT);
        RECT hdrTitle = { px(66), px(14), bounds.right - px(48), px(50) };
        HGDIOBJ prevFont = SelectObject(dc, host.heading);
        SetTextColor(dc, color(host.model.palette.text_primary));
        DrawTextW(dc, host.model.title, -1, &hdrTitle, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        if (prevFont) SelectObject(dc, prevFont);

        RECT topRule = { px(16), px(62), bounds.right - px(16), px(62) + 1 };
        SetDCBrushColor(dc, color(host.model.palette.border));
        FillRect(dc, &topRule, reinterpret_cast<HBRUSH>(GetStockObject(DC_BRUSH)));

        RECT rule = { px(16), bounds.bottom - px(43), bounds.right - px(16), bounds.bottom - px(43) + 1 };
        SetDCBrushColor(dc, color(host.model.palette.border));
        FillRect(dc, &rule, reinterpret_cast<HBRUSH>(GetStockObject(DC_BRUSH)));

        RECT footer = { px(20), bounds.bottom - px(38), bounds.right - px(20), bounds.bottom - px(8) };
        SelectObject(dc, host.font);
        SetTextColor(dc, color(host.model.palette.text_muted));
        DrawTextW(dc, L"↑ ↓  Navigate", -1, &footer, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        DrawTextW(dc, L"Enter  Open     Esc  Close", -1, &footer, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }
    if (host.model.kind == 6 || host.model.kind == 8) {
        RECT icon = { px(24), px(22), px(56), px(54) };
        drawProgramIcon(dc, host.model.icon, icon);
    }
    if (host.model.kind == 9) {
        RECT line = { px(24), px(host.height - 72), px(host.width - 24), px(host.height - 72) + 1 };
        SetDCBrushColor(dc, color(host.model.palette.border));
        FillRect(dc, &line, (HBRUSH)GetStockObject(DC_BRUSH));
    }
}
}
