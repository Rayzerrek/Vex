#include "windows_dialog.h"
#include "ui_renderer.h"
#include <commctrl.h>
#include <gdiplus.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

namespace {
constexpr int titleId = 100, descriptionId = 101, queryId = 102, listId = 103;
constexpr int themeId = 110, sizeId = 111, blinkId = 112, shellId = 113, themeGridId = 115;
constexpr int pageTitleId = 104, pageDetailId = 105, closeId = 125, firstPageId = 130, firstModeId = 140;
enum class SettingsPage { general, appearance, terminal, about };
constexpr size_t itemLimit = 64, shellLimit = 8;
struct DialogItem {
    uint32_t value;
    wchar_t label[256], detail[256];
    COLORREF background, foreground, accent, swatches[5];
};
struct DialogModel {
    uint32_t kind, selected, mode;
    wchar_t title[128], description[512], accept[64];
    DialogItem items[itemLimit], shells[shellLimit];
    size_t count, shellCount;
    VexUserSettings settings;
    VexChromePalette palette;
};
struct ControlLayout { HWND window; float x, y, width, height; };
struct DialogHost {
    HWND owner, window, previousFocus;
    ULONG_PTR graphicsToken = 0;
    VexWindowCallbacks callbacks;
    DialogModel model;
    HFONT font, heading, symbols;
    HBRUSH background, field;
    HWND hovered = nullptr;
    int hotRow = -1;
    enum class Phase { closed, creating, active, closing } phase = Phase::closed;
    bool synchronizing = false;
    SettingsPage page = SettingsPage::general;
    VexUserSettings draft = {};
    float scale = 1, width = 600, height = 470, baseWidth = 600, baseHeight = 470;
    ControlLayout controls[40];
    size_t controlCount = 0;
};
DialogHost host;
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
int listRowHeight() { return host.model.kind == 2 ? 34 : 58; }
int themeRowHeight() { return host.model.kind == 4 && host.height < 430 ? 104 : 120; }
void roundedFill(HDC dc, RECT bounds, COLORREF fill, COLORREF border, int radius);
void acceptDialog();
void drawSymbol(HDC dc, const wchar_t* symbol, RECT bounds, COLORREF ink) {
    HGDIOBJ old = SelectObject(dc, host.symbols);
    SetBkMode(dc, TRANSPARENT); SetTextColor(dc, ink);
    DrawTextW(dc, symbol, -1, &bounds, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    SelectObject(dc, old);
}
bool decodeText(VexDialogText text, wchar_t* output, int capacity) {
    if ((!text.bytes && text.length) || text.length > 4096) return false;
    if (!text.length) { output[0] = 0; return true; }
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(text.bytes), (int)text.length, output, capacity - 1);
    if (!length) return false;
    for (int i = 0; i < length; ++i) if (!output[i]) return false;
    output[length] = 0;
    return true;
}
bool copyItems(const VexDialogItem* input, size_t count, DialogItem* output, size_t capacity) {
    if (count > capacity || (count && !input)) return false;
    for (size_t i = 0; i < count; ++i) {
        output[i].value = input[i].value;
        if (!decodeText(input[i].label, output[i].label, 256) || !decodeText(input[i].detail, output[i].detail, 256)) return false;
        output[i].background = color(input[i].background);
        output[i].foreground = color(input[i].foreground);
        output[i].accent = color(input[i].accent);
        for (int c = 0; c < 5; ++c) output[i].swatches[c] = color(input[i].swatches[c]);
    }
    return true;
}
void releasePaint() {
    if (host.font) { DeleteObject(host.font); host.font = nullptr; }
    if (host.heading) { DeleteObject(host.heading); host.heading = nullptr; }
    if (host.symbols) { DeleteObject(host.symbols); host.symbols = nullptr; }
    if (host.background) { DeleteObject(host.background); host.background = nullptr; }
    if (host.field) { DeleteObject(host.field); host.field = nullptr; }
}
void notify(uint32_t event, uint32_t value = 0, const uint8_t* text = nullptr, size_t length = 0, VexUserSettings settings = {}) {
    if (host.callbacks.dialog) host.callbacks.dialog(host.callbacks.context, event, value, text, length, settings);
}
void closeDialog(bool restoreFocus) {
    if (host.phase == DialogHost::Phase::closed || host.phase == DialogHost::Phase::closing) return;
    host.phase = DialogHost::Phase::closing;
    HWND dialog = host.window;
    host.window = nullptr;
    if (IsWindow(dialog)) DestroyWindow(dialog);
    releasePaint();
    host.phase = DialogHost::Phase::closed;
    host.controlCount = 0;
    host.hovered = nullptr;
    host.hotRow = -1;
    if (IsWindow(host.owner)) {
        EnableWindow(host.owner, TRUE);
        if (restoreFocus) {
            SetActiveWindow(host.owner);
            SetFocus(IsWindow(host.previousFocus) ? host.previousFocus : host.owner);
        }
    }
}
void finish(uint32_t event, uint32_t value = 0, VexUserSettings settings = {}) {
    closeDialog(true);
    notify(event, value, nullptr, 0, settings);
}
bool navigateCommandList(WPARAM key) {
    if (host.model.kind != 2 || GetKeyState(VK_CONTROL) < 0 || GetKeyState(VK_MENU) < 0) return false;
    HWND list = GetDlgItem(host.window, listId);
    if (!list) return false;
    LRESULT count = SendMessageW(list, LB_GETCOUNT, 0, 0);
    if (key != VK_UP && key != VK_DOWN && key != VK_HOME && key != VK_END && key != VK_PRIOR && key != VK_NEXT) return false;
    if (count <= 0) return true;
    LRESULT selected = SendMessageW(list, LB_GETCURSEL, 0, 0);
    if (selected < 0) selected = 0;
    if (key == VK_UP) selected = selected == 0 ? count - 1 : selected - 1;
    else if (key == VK_DOWN) selected = selected + 1 == count ? 0 : selected + 1;
    else if (key == VK_HOME) selected = 0;
    else if (key == VK_END) selected = count - 1;
    else if (key == VK_PRIOR) selected = selected > 8 ? selected - 8 : 0;
    else selected = selected + 8 < count ? selected + 8 : count - 1;
    SendMessageW(list, LB_SETCURSEL, selected, 0);
    LRESULT value = SendMessageW(list, LB_GETITEMDATA, selected, 0);
    if (value >= 0 && (uint64_t)value <= UINT32_MAX) notify(2, (uint32_t)value);
    return true;
}
LRESULT CALLBACK dialogControlFeedback(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if (message == WM_KEYDOWN && GetDlgCtrlID(control) == listId && navigateCommandList(wparam)) return 0;
    if (message == WM_MOUSEMOVE) {
        TRACKMOUSEEVENT tracking = { sizeof(tracking), TME_LEAVE, control, 0 }; TrackMouseEvent(&tracking);
        HWND previous = host.hovered;
        host.hovered = control;
        int row = -1;
        if (GetDlgCtrlID(control) == listId) {
            LRESULT hit = SendMessageW(control, LB_ITEMFROMPOINT, 0, lparam);
            if (!HIWORD(hit)) row = LOWORD(hit);
        }
        if (previous != control || host.hotRow != row) {
            host.hotRow = row;
            if (IsWindow(previous)) InvalidateRect(previous, nullptr, FALSE);
            InvalidateRect(control, nullptr, FALSE);
        }
    }
    if (message == WM_MOUSELEAVE && host.hovered == control) {
        host.hovered = nullptr; host.hotRow = -1; InvalidateRect(control, nullptr, FALSE);
    }
    if (message == WM_LBUTTONUP && GetDlgCtrlID(control) == listId && (host.model.kind == 2 || host.model.kind == 5)) {
        LRESULT hit = SendMessageW(control, LB_ITEMFROMPOINT, 0, lparam);
        if (!HIWORD(hit) && LOWORD(hit) < host.model.count) {
            SendMessageW(control, LB_SETCURSEL, LOWORD(hit), 0);
            acceptDialog();
            return 0;
        }
    }
    return DefSubclassProc(control, message, wparam, lparam);
}
LRESULT CALLBACK themedCombo(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if (message == WM_PAINT || message == WM_PRINTCLIENT) {
        PAINTSTRUCT paint = {};
        HDC dc = message == WM_PAINT ? BeginPaint(control, &paint) : reinterpret_cast<HDC>(wparam);
        if (dc) {
            RECT bounds; GetClientRect(control, &bounds);
            FillRect(dc, &bounds, host.field);
            RECT field = bounds; InflateRect(&field, -1, -1);
            const bool focus = GetFocus() == control;
            roundedFill(dc, field, fieldColor(), focus ? color(host.model.palette.accent) : fieldColor(), px(14));
            wchar_t text[256]; GetWindowTextW(control, text, 256);
            RECT label = field; label.left += px(12); label.right -= px(32);
            SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color(host.model.palette.text_primary)); SelectObject(dc, host.font);
            DrawTextW(dc, text, -1, &label, DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
            RECT arrow = field; arrow.left = arrow.right - px(30);
            drawSymbol(dc, L"\xE70D", arrow, color(host.model.palette.text_muted));
        }
        if (message == WM_PAINT) EndPaint(control, &paint);
        return 0;
    }
    LRESULT result = DefSubclassProc(control, message, wparam, lparam);
    if (message == WM_SETFOCUS || message == WM_KILLFOCUS || message == CB_SETCURSEL) InvalidateRect(control, nullptr, FALSE);
    return result;
}
LRESULT CALLBACK selectableText(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if (message == WM_KEYDOWN && GetDlgCtrlID(control) == queryId && navigateCommandList(wparam)) return 0;
    if (message == WM_KEYDOWN && wparam == 'A' && GetKeyState(VK_CONTROL) < 0) {
        SendMessageW(control, EM_SETSEL, 0, -1);
        return 0;
    }
    LRESULT result = DefSubclassProc(control, message, wparam, lparam);
    if ((message == WM_PAINT || message == WM_PRINT || message == WM_PRINTCLIENT) && GetDlgCtrlID(control) == queryId && !GetWindowTextLengthW(control)) {
        HDC dc = message == WM_PAINT ? GetDC(control) : reinterpret_cast<HDC>(wparam);
        if (dc) {
            RECT bounds; GetClientRect(control, &bounds);
            SelectObject(dc, host.font); SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color(host.model.palette.text_muted));
            DrawTextW(dc, L"Search commands...", -1, &bounds, DT_SINGLELINE | DT_NOPREFIX);
            if (message == WM_PAINT) ReleaseDC(control, dc);
        }
    }
    return result;
}
HWND control(const wchar_t* klass, const wchar_t* text, DWORD style, int id, float x, float y, float width, float height) {
    if (host.controlCount >= 40) return nullptr;
    HWND result = CreateWindowExW(0, klass, text, WS_CHILD | WS_VISIBLE | style, px(x), px(y), px(width), px(height), host.window, reinterpret_cast<HMENU>((INT_PTR)id), GetModuleHandleW(nullptr), nullptr);
    if (!result) return nullptr;
    SendMessageW(result, WM_SETFONT, reinterpret_cast<WPARAM>(id == pageTitleId || (id == titleId && host.model.kind != 4) ? host.heading : host.font), FALSE);
    if (wcscmp(klass, L"EDIT") == 0 && !SetWindowSubclass(result, selectableText, 1, 0)) { DestroyWindow(result); return nullptr; }
    if ((wcscmp(klass, L"LISTBOX") == 0 || wcscmp(klass, L"BUTTON") == 0) && !SetWindowSubclass(result, dialogControlFeedback, 1, 0)) { DestroyWindow(result); return nullptr; }
    if (wcscmp(klass, L"LISTBOX") == 0 || wcscmp(klass, L"COMBOBOX") == 0)
        SetWindowTheme(result, host.model.palette.is_dark ? L"DarkMode_Explorer" : L"Explorer", nullptr);
    if (wcscmp(klass, L"COMBOBOX") == 0) {
        if (!SetWindowSubclass(result, themedCombo, 1, 0)) { DestroyWindow(result); return nullptr; }
        SendMessageW(result, CB_SETITEMHEIGHT, (WPARAM)-1, px(30));
        SendMessageW(result, CB_SETITEMHEIGHT, 0, px(30));
    }
    host.controls[host.controlCount++] = { result, x, y, width, height };
    return result;
}
void fillCombo(HWND combo, const DialogItem* items, size_t count, uint32_t selected) {
    SendMessageW(combo, CB_RESETCONTENT, 0, 0);
    int selection = -1;
    for (size_t i = 0; i < count; ++i) {
        LRESULT row = SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(items[i].label));
        if (row >= 0) {
            SendMessageW(combo, CB_SETITEMDATA, (WPARAM)row, items[i].value);
            if (items[i].value == selected) selection = (int)row;
        }
    }
    SendMessageW(combo, CB_SETCURSEL, selection, 0);
}
void fillList(int id = listId) {
    HWND list = GetDlgItem(host.window, id);
    if (!list) return;
    SendMessageW(list, WM_SETREDRAW, FALSE, 0);
    SendMessageW(list, LB_RESETCONTENT, 0, 0);
    int selected = -1;
    for (size_t i = 0; i < host.model.count; ++i) {
        LRESULT row = SendMessageW(list, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(host.model.items[i].label));
        if (row >= 0) {
            SendMessageW(list, LB_SETITEMDATA, (WPARAM)row, host.model.items[i].value);
            if (host.model.items[i].value == (id == themeGridId ? host.draft.theme : host.model.selected)) selected = (int)row;
        }
    }
    SendMessageW(list, LB_SETCURSEL, selected, 0);
    SendMessageW(list, WM_SETREDRAW, TRUE, 0);
    if (host.model.kind == 2) ShowWindow(list, host.model.count ? SW_SHOW : SW_HIDE);
    InvalidateRect(list, nullptr, FALSE);
}
bool listSelection(uint32_t& value, int id = listId) {
    HWND list = GetDlgItem(host.window, id);
    LRESULT row = SendMessageW(list, LB_GETCURSEL, 0, 0);
    if (row == LB_ERR) return false;
    LRESULT item = SendMessageW(list, LB_GETITEMDATA, (WPARAM)row, 0);
    if (item == LB_ERR || item < 0 || (uint64_t)item > UINT32_MAX) return false;
    value = (uint32_t)item;
    return true;
}
bool comboSelection(int id, uint32_t& value) {
    HWND combo = GetDlgItem(host.window, id);
    LRESULT row = SendMessageW(combo, CB_GETCURSEL, 0, 0);
    if (row == CB_ERR) return false;
    LRESULT item = SendMessageW(combo, CB_GETITEMDATA, (WPARAM)row, 0);
    if (item == CB_ERR || item < 0 || (uint64_t)item > UINT32_MAX) return false;
    value = (uint32_t)item;
    return true;
}
void showPage();
void acceptDialog() {
    if (host.model.kind == 4) {
        VexUserSettings settings = host.model.settings;
        BOOL valid = FALSE;
        settings.font_size = GetDlgItemInt(host.window, sizeId, &valid, FALSE);
        if (!valid || settings.font_size < 9 || settings.font_size > 32) {
            host.page = SettingsPage::terminal;
            showPage();
            SetDlgItemTextW(host.window, pageDetailId, L"Enter a font size between 9 and 32. Preferences have not been saved.");
            SetFocus(GetDlgItem(host.window, sizeId));
            return;
        }
        if (!comboSelection(themeId, settings.theme) || !comboSelection(shellId, settings.shell)) {
            host.page = SettingsPage::general;
            showPage();
            SetDlgItemTextW(host.window, pageDetailId, L"Choose an available shell before saving preferences.");
            SetFocus(GetDlgItem(host.window, shellId));
            return;
        }
        settings.cursor_blink = host.draft.cursor_blink;
        finish(4, 0, settings);
    } else if (host.model.kind == 6) finish(1);
    else {
        uint32_t value;
        if (listSelection(value)) finish(1, value);
    }
}
void queryChanged() {
    wchar_t wide[129];
    GetDlgItemTextW(host.window, queryId, wide, 129);
    uint8_t bytes[512];
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide, -1, reinterpret_cast<char*>(bytes), sizeof(bytes), nullptr, nullptr);
    if (!length) return;
    --length;
    if (length > 128) {
        int end = 128;
        while (end > 0 && (bytes[end] & 0xc0) == 0x80) --end;
        wchar_t bounded[129];
        int units = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), end, bounded, 128);
        if (!units) return;
        bounded[units] = 0;
        host.synchronizing = true;
        SetDlgItemTextW(host.window, queryId, bounded);
        SendDlgItemMessageW(host.window, queryId, EM_SETSEL, units, units);
        host.synchronizing = false;
        length = end;
    }
    notify(3, 0, bytes, (size_t)length);
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
void drawListItem(const DRAWITEMSTRUCT& draw) {
    if (draw.itemID == (UINT)-1 || draw.itemID >= host.model.count) return;
    const DialogItem& item = host.model.items[draw.itemID];
    const bool selected = (draw.itemState & ODS_SELECTED) != 0;
    const bool theme = host.model.kind == 1 || draw.CtlID == themeGridId;
    FillRect(draw.hDC, &draw.rcItem, draw.CtlID == themeGridId ? host.field : host.background);
    SetBkMode(draw.hDC, TRANSPARENT);
    SelectObject(draw.hDC, host.font);
    if (theme) {
        RECT card = draw.rcItem; card.left += px(5); card.right -= px(9); card.top += px(4); card.bottom = card.top + px(themeRowHeight() - 34);
        roundedFill(draw.hDC, card, selected ? color(host.model.palette.accent) : color(host.model.palette.border), selected ? color(host.model.palette.accent) : color(host.model.palette.border), px(26));
        RECT inner = card; InflateRect(&inner, -px(selected ? 3 : 1), -px(selected ? 3 : 1));
        roundedFill(draw.hDC, inner, item.background, item.background, px(22));
        RECT preview = card; preview.left += px(14); preview.top += px(14); preview.bottom -= px(30);
        SelectObject(draw.hDC, host.heading); SetTextColor(draw.hDC, item.foreground);
        DrawTextW(draw.hDC, L"Aa", 2, &preview, DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(draw.hDC, host.font);
        for (int i = 0; i < 5; ++i) {
            int x = card.left + px(14 + i * 23), y = card.bottom - px(27);
            RECT dot = { x, y, x + px(17), y + px(17) };
            roundedFill(draw.hDC, dot, item.swatches[i], item.swatches[i], px(17));
        }
        RECT name = draw.rcItem; name.top = card.bottom + px(5); name.bottom -= px(2);
        SetTextColor(draw.hDC, color(selected ? host.model.palette.text_primary : host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.label, -1, &name, DT_CENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        RECT sample = card; sample.left = sample.right - px(54); sample.right -= px(14); sample.top += px(14); sample.bottom = sample.top + px(30);
        COLORREF sampleFill = blend(item.background, item.accent, 0.20f);
        roundedFill(draw.hDC, sample, sampleFill, sampleFill, px(10));
        SetTextColor(draw.hDC, item.foreground);
        DrawTextW(draw.hDC, L"~/", -1, &sample, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        return;
    }
    RECT row = draw.rcItem; row.top += px(2); row.bottom -= px(2); row.left += px(2); row.right -= px(3);
    const bool hot = host.hovered == draw.hwndItem && host.hotRow == (int)draw.itemID;
    COLORREF fill = selected ? selectionColor() : hot ? fieldColor() : surfaceColor();
    roundedFill(draw.hDC, row, fill, fill, px(14));
    RECT text = row; text.left += px(12); text.right -= px(12);
    if (host.model.kind == 2) {
        RECT shortcut = text;
        SetTextColor(draw.hDC, color(selected ? host.model.palette.text_primary : host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.detail, -1, &shortcut, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        const int shortcutWidth = shortcut.right - shortcut.left;
        shortcut = text; shortcut.left = shortcut.right - shortcutWidth;
        DrawTextW(draw.hDC, item.detail, -1, &shortcut, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        if (shortcutWidth) text.right = shortcut.left - px(20);
        SetTextColor(draw.hDC, color(host.model.palette.text_primary));
        DrawTextW(draw.hDC, item.label, -1, &text, DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    } else {
        if (host.model.kind == 5) {
            RECT icon = row; icon.left += px(10); icon.right = icon.left + px(34); icon.top += px(8); icon.bottom -= px(8);
            COLORREF tile = blend(fill, color(host.model.palette.accent), 0.12f);
            roundedFill(draw.hDC, icon, tile, tile, px(16));
            drawSymbol(draw.hDC, L"\xE756", icon, color(host.model.palette.accent));
            text.left += px(44);
        }
        text.top += px(8);
        SetTextColor(draw.hDC, color(host.model.palette.text_primary));
        DrawTextW(draw.hDC, item.label, -1, &text, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        text.top += px(21); SetTextColor(draw.hDC, color(host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.detail, -1, &text, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    }
    if (GetFocus() == draw.hwndItem && selected) {
        RECT mark = row; mark.left += px(2); mark.right = mark.left + px(2); mark.top += px(9); mark.bottom -= px(9);
        roundedFill(draw.hDC, mark, color(host.model.palette.accent), color(host.model.palette.accent), px(2));
    }
}
void drawDialogButton(const DRAWITEMSTRUCT& draw) {
    const int id = (int)draw.CtlID;
    const bool navigation = id >= firstPageId && id < firstPageId + 4;
    const bool mode = id >= firstModeId && id < firstModeId + 4;
    const bool checked = navigation ? (int)host.page == id - firstPageId : mode ? host.model.mode == (uint32_t)(id - firstModeId) : id == blinkId && host.draft.cursor_blink;
    const bool pressed = (draw.itemState & ODS_SELECTED) != 0;
    RECT bounds = draw.rcItem; InflateRect(&bounds, -1, -1);
    FillRect(draw.hDC, &draw.rcItem, id == blinkId ? host.field : host.background);
    const bool hot = host.hovered == draw.hwndItem;
    COLORREF surface = checked || pressed ? fieldColor() : hot ? fieldColor() : surfaceColor();
    if (id == blinkId) surface = color(host.model.palette.pane_bg);
    COLORREF border = navigation && checked ? color(host.model.palette.border) : surface;
    if (GetFocus() == draw.hwndItem) border = color(host.model.palette.accent);
    const bool primary = id == IDOK;
    if (primary) {
        surface = host.model.kind == 6 ? RGB(174, 66, 48) : selectionColor();
        if (hot || pressed) surface = blend(surface, color(host.model.palette.text_primary), pressed ? 0.16f : 0.08f);
        border = surface;
    }
    roundedFill(draw.hDC, bounds, surface, border, px(navigation ? 22 : 14));
    SetBkMode(draw.hDC, TRANSPARENT); SelectObject(draw.hDC, host.font);
    SetTextColor(draw.hDC, primary && host.model.kind == 6 ? RGB(255, 245, 241) : color(checked && navigation ? host.model.palette.accent : host.model.palette.text_primary));
    wchar_t label[96]; GetWindowTextW(draw.hwndItem, label, 96);
    if (navigation) {
        const wchar_t* icons[] = { L"\xE713", L"\xE790", L"\xE756", L"\xE946" };
        RECT icon = bounds; icon.bottom = icon.top + px(host.height < 430 ? 24 : 34);
        drawSymbol(draw.hDC, icons[id - firstPageId], icon, color(checked ? host.model.palette.accent : host.model.palette.text_muted));
        bounds.top = icon.bottom;
        SetTextColor(draw.hDC, color(checked ? host.model.palette.accent : host.model.palette.text_muted));
    }
    if (id == blinkId) {
        RECT toggle = bounds; toggle.left = toggle.right - px(48); toggle.right -= px(8); toggle.top += px(6); toggle.bottom -= px(6);
        roundedFill(draw.hDC, toggle, color(checked ? host.model.palette.accent : host.model.palette.border), color(checked ? host.model.palette.accent : host.model.palette.border), px(22));
        RECT knob = toggle; knob.left += checked ? px(20) : px(3); knob.right = knob.left + px(14); knob.top += px(3); knob.bottom -= px(3);
        roundedFill(draw.hDC, knob, RGB(245,245,245), RGB(245,245,245), px(16));
        bounds.right -= px(52);
    }
    if (id == blinkId) bounds.left += px(12);
    DrawTextW(draw.hDC, label, -1, &bounds, DT_SINGLELINE | DT_VCENTER | (id == blinkId ? DT_LEFT : DT_CENTER) | DT_NOPREFIX);

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
float dialogWidth() { return host.model.kind == 4 ? 760.0f : host.model.kind == 1 ? 580.0f : host.model.kind == 6 ? 460.0f : 480.0f; }
float dialogHeight() {
    if (host.model.kind == 2) return 112.0f + (float)(host.model.count < 2 ? 2 : host.model.count > 14 ? 14 : host.model.count) * 34.0f;
    return host.model.kind == 6 ? 240.0f : host.model.kind == 4 ? 620.0f : 520.0f;
}
void showPage() {
    const bool general = host.page == SettingsPage::general, appearance = host.page == SettingsPage::appearance, terminal = host.page == SettingsPage::terminal;
    const int generalIds[] = { shellId, 122, 123 };
    const int appearanceIds[] = { themeGridId, 124 };
    const int terminalIds[] = { sizeId, blinkId, 121, 126 };
    for (int id : generalIds) ShowWindow(GetDlgItem(host.window, id), general ? SW_SHOW : SW_HIDE);
    for (int id : appearanceIds) ShowWindow(GetDlgItem(host.window, id), appearance ? SW_SHOW : SW_HIDE);
    for (int id : terminalIds) ShowWindow(GetDlgItem(host.window, id), terminal ? SW_SHOW : SW_HIDE);
    const wchar_t* names[] = { L"General", L"Appearance", L"Terminal", L"About" };
    const wchar_t* descriptions[] = { L"Defaults for new terminal sessions.", L"Color themes for every terminal pane.", L"Font size and cursor behavior.", L"Vex native terminal\r\nWindows + WSL · ConPTY · libghostty" };
    SetDlgItemTextW(host.window, pageTitleId, names[(int)host.page]);
    SetDlgItemTextW(host.window, pageDetailId, descriptions[(int)host.page]);
    for (int i = 0; i < 4; ++i) InvalidateRect(GetDlgItem(host.window, firstPageId + i), nullptr, FALSE);
    InvalidateRect(host.window, nullptr, TRUE);
}
void paintDialogSurface(HDC dc) {
    RECT bounds; GetClientRect(host.window, &bounds);
    FillRect(dc, &bounds, host.background);
    roundedFill(dc, bounds, surfaceColor(), color(host.model.palette.border), px(24));
    if (host.model.kind == 4 && host.page != SettingsPage::about) {
        const bool compact = host.height < 430;
        RECT card = { px(28), px(compact ? (host.page == SettingsPage::appearance ? 152 : 176) : 222), bounds.right - px(28), host.page == SettingsPage::appearance ? bounds.bottom - px(70) : px((host.page == SettingsPage::terminal ? 362 : 310) - (compact ? 46 : 0)) };
        roundedFill(dc, card, color(host.model.palette.pane_bg), color(host.model.palette.pane_bg), px(28));
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
        RECT footer = { px(20), bounds.bottom - px(38), bounds.right - px(20), bounds.bottom - px(8) };
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color(host.model.palette.text_muted)); SelectObject(dc, host.font);
        DrawTextW(dc, L"↑ ↓  Navigate", -1, &footer, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        DrawTextW(dc, L"Enter  Run     Esc  Close", -1, &footer, DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    }
    if (host.model.kind == 6) {
        RECT icon = { px(24), px(26), px(66), px(68) };
        COLORREF tint = blend(surfaceColor(), RGB(194, 101, 71), 0.16f);
        roundedFill(dc, icon, tint, tint, px(24));
        drawSymbol(dc, L"\xE7BA", icon, host.model.palette.is_dark ? RGB(229, 164, 124) : RGB(155, 75, 40));
    }
}
bool createControls(float width, float height) {
    const bool settings = host.model.kind == 4, commands = host.model.kind == 2, closing = host.model.kind == 6;
    bool ok = true;
    if (!commands) ok = control(L"EDIT", host.model.title, ES_READONLY | ES_AUTOHSCROLL | (settings ? ES_CENTER : 0), titleId, settings ? 120 : closing ? 82 : 24, settings ? 18 : closing ? 28 : 22, settings ? width - 240 : closing ? width - 110 : width - 80, 28) != nullptr;
    if (settings) {
        const wchar_t* pages[] = { L"General", L"Appearance", L"Terminal", L"About" };
        for (int i = 0; i < 4; ++i) ok = control(L"BUTTON", pages[i], BS_OWNERDRAW | WS_TABSTOP, firstPageId + i, (width - 448) / 2 + i * 112.0f, 56, 108, 68) && ok;
        ok = control(L"EDIT", L"General", ES_READONLY | ES_AUTOHSCROLL, pageTitleId, 32, 146, width - 64, 28) && ok;
        ok = control(L"EDIT", L"Defaults for new terminal sessions.", ES_READONLY | ES_MULTILINE, pageDetailId, 32, 181, width - 64, 32) && ok;
        HWND theme = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_TABSTOP | WS_VSCROLL, themeId, 205, 100, width - 229, 220);
        ok = theme && ok;
        if (theme) { fillCombo(theme, host.model.items, host.model.count, host.model.settings.theme); ShowWindow(theme, SW_HIDE); }
        ok = control(L"STATIC", L"Color theme", 0, 124, 46, 238, width - 92, 24) && ok;
        HWND grid = control(L"LISTBOX", L"", WS_TABSTOP | WS_HSCROLL | LBS_MULTICOLUMN | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, themeGridId, 40, 276, width - 80, height - 356);
        ok = grid && ok;
        if (grid) { SendMessageW(grid, LB_SETITEMHEIGHT, 0, px(themeRowHeight())); SendMessageW(grid, LB_SETCOLUMNWIDTH, px(160), 0); fillList(themeGridId); }
        ok = control(L"STATIC", L"Font size", 0, 121, 46, 242, 180, 24) && ok;
        ok = control(L"STATIC", L"Terminal text size · 9–32 pt", 0, 126, 46, 268, 300, 24) && ok;
        wchar_t size[8]; swprintf(size, 8, L"%u", host.model.settings.font_size);
        HWND fontSize = control(L"EDIT", size, ES_NUMBER | ES_AUTOHSCROLL | ES_CENTER | WS_TABSTOP, sizeId, width - 126, 248, 64, 26);
        ok = fontSize && ok;
        if (fontSize) SendMessageW(fontSize, EM_SETLIMITTEXT, 2, 0);
        ok = control(L"BUTTON", L"Blink terminal cursor", BS_OWNERDRAW | WS_TABSTOP, blinkId, 34, 308, width - 68, 40) && ok;
        ok = control(L"STATIC", L"Default shell", 0, 122, 46, 242, 160, 24) && ok;
        ok = control(L"STATIC", L"Used for new tabs and splits.", 0, 123, 46, 268, 300, 24) && ok;
        HWND shell = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_TABSTOP | WS_VSCROLL, shellId, width - 300, 244, 248, 220);
        ok = shell && ok;
        if (shell) fillCombo(shell, host.model.shells, host.model.shellCount, host.model.settings.shell);
        showPage();
    } else {
        if (!commands) ok = control(L"EDIT", host.model.description, ES_READONLY | ES_MULTILINE, descriptionId, closing ? 82 : 24, closing ? 68 : 58, closing ? width - 110 : width - 48, closing ? height - 144 : 38) && ok;
    }
    if (!settings && host.model.kind != 6) {
        float top = 100;
        if (host.model.kind == 2) {
            HWND edit = control(L"EDIT", L"", ES_AUTOHSCROLL | WS_TABSTOP, queryId, 52, 22, width - 76, 22);
            ok = edit && ok;
            if (edit) {
                SendMessageW(edit, EM_SETLIMITTEXT, 128, 0);
                SendMessageW(edit, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"Search commands..."));
            }
            top = 62;
        } else if (host.model.kind == 5) {
            const wchar_t* modes[] = { L"New tab", L"Split right", L"Split down", L"Default" };
            for (int i = 0; i < 4; ++i) ok = control(L"BUTTON", modes[i], BS_OWNERDRAW | WS_TABSTOP, firstModeId + i, 16 + i * (width - 32) / 4, 98, (width - 32) / 4 - 2, 32) && ok;
            top = 142;
        }
        const bool themes = host.model.kind == 1;
        HWND list = control(L"LISTBOX", L"", WS_TABSTOP | (themes ? WS_HSCROLL | LBS_MULTICOLUMN : WS_VSCROLL) | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, listId, commands ? 10 : 16, top, width - (commands ? 20 : 32), height - top - (commands ? 48 : 70));
        ok = list && ok;
        if (list) {
            SendMessageW(list, LB_SETITEMHEIGHT, 0, px(themes ? themeRowHeight() : listRowHeight()));
            if (themes) SendMessageW(list, LB_SETCOLUMNWIDTH, px(160), 0);
            fillList();
        }
    }
    if (!commands) {
        if (!closing) ok = control(L"BUTTON", L"×", BS_OWNERDRAW | WS_TABSTOP, closeId, width - 42, 12, 28, 28) && ok;
        ok = control(L"BUTTON", L"Cancel", WS_TABSTOP | BS_OWNERDRAW, IDCANCEL, width - 244, height - 52, 96, 34) && ok;
        ok = control(L"BUTTON", host.model.accept, WS_TABSTOP | BS_OWNERDRAW, IDOK, width - 136, height - 52, 112, 34) && ok;
    }
    SendMessageW(host.window, DM_SETDEFID, host.model.kind == 6 ? IDCANCEL : IDOK, 0);
    return ok;
}
INT_PTR CALLBACK dialogProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_INITDIALOG: {
        host.window = window;
        SetDialogDpiChangeBehavior(window, DDC_DISABLE_ALL, DDC_DISABLE_ALL);
        SetWindowTextW(window, host.model.title);
        host.scale = GetDpiForWindow(host.owner) / 96.0f;
        host.font = CreateFontW(-px(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        host.heading = CreateFontW(-px(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        host.symbols = CreateFontW(-px(22), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe MDL2 Assets");
        host.background = CreateSolidBrush(surfaceColor());
        host.field = CreateSolidBrush(color(host.model.palette.pane_bg));
        RECT owner; GetClientRect(host.owner, &owner);
        POINT origin = { 0, 0 }; ClientToScreen(host.owner, &origin);
        const float width = fminf(dialogWidth(), (owner.right - owner.left) / host.scale - 32);
        const float height = fminf(dialogHeight(), (owner.bottom - owner.top) / host.scale - 40);
        host.width = width; host.height = height; host.baseWidth = dialogWidth(); host.baseHeight = dialogHeight();
        host.controlCount = 0;
        SetWindowPos(window, HWND_TOP, origin.x + (owner.right - px(width)) / 2, origin.y + (owner.bottom - px(height)) / 2, px(width), px(height), SWP_NOACTIVATE);
        HRGN rounded = CreateRoundRectRgn(0, 0, px(width) + 1, px(height) + 1, px(24), px(24));
        if (rounded && !SetWindowRgn(window, rounded, FALSE)) DeleteObject(rounded);
        DWORD corner = 2; DwmSetWindowAttribute(window, 33, &corner, sizeof(corner));
        if (!host.font || !host.heading || !host.symbols || !host.background || !host.field || !createControls(host.baseWidth, host.baseHeight)) { closeDialog(false); return FALSE; }
        HWND focus = GetDlgItem(window, host.model.kind == 4 ? shellId : host.model.kind == 6 ? IDCANCEL : host.model.kind == 2 ? queryId : listId);
        SetFocus(focus);
        return FALSE;
    }
    case WM_COMMAND:
        if (host.phase != DialogHost::Phase::active || host.synchronizing) return TRUE;
        if (LOWORD(wparam) == IDCANCEL || LOWORD(wparam) == closeId) { finish(0); return TRUE; }
        if (LOWORD(wparam) >= firstPageId && LOWORD(wparam) < firstPageId + 4) { host.page = (SettingsPage)(LOWORD(wparam) - firstPageId); showPage(); return TRUE; }
        if (LOWORD(wparam) == blinkId) { host.draft.cursor_blink = !host.draft.cursor_blink; InvalidateRect(GetDlgItem(window, blinkId), nullptr, FALSE); return TRUE; }
        if (LOWORD(wparam) == themeGridId && HIWORD(wparam) == LBN_SELCHANGE) {
            uint32_t value; if (listSelection(value, themeGridId)) { host.draft.theme = value; fillCombo(GetDlgItem(window, themeId), host.model.items, host.model.count, value); }
            return TRUE;
        }
        if (LOWORD(wparam) == IDOK) { acceptDialog(); return TRUE; }
        if (LOWORD(wparam) == queryId && HIWORD(wparam) == EN_CHANGE) { queryChanged(); return TRUE; }
        if (LOWORD(wparam) >= firstModeId && LOWORD(wparam) < firstModeId + 4) {
            host.model.mode = LOWORD(wparam) - firstModeId;
            for (int i = 0; i < 4; ++i) InvalidateRect(GetDlgItem(window, firstModeId + i), nullptr, FALSE);
            notify(5, host.model.mode);
            return TRUE;
        }
        if (LOWORD(wparam) == listId && HIWORD(wparam) == LBN_DBLCLK) { acceptDialog(); return TRUE; }
        if (LOWORD(wparam) == listId && HIWORD(wparam) == LBN_SELCHANGE) { uint32_t value; if (listSelection(value)) notify(2, value); return TRUE; }
        break;
    case WM_PAINT: { PAINTSTRUCT paint; HDC dc = BeginPaint(window, &paint); paintDialogSurface(dc); EndPaint(window, &paint); return TRUE; }
    case WM_PRINTCLIENT: if (wparam) { paintDialogSurface(reinterpret_cast<HDC>(wparam)); return TRUE; } break;
    case DM_GETDEFID:
        SetWindowLongPtrW(window, DWLP_MSGRESULT, MAKELRESULT(host.model.kind == 6 ? IDCANCEL : IDOK, DC_HASDEFID));
        return TRUE;
    case DM_SETDEFID: SetWindowLongPtrW(window, DWLP_MSGRESULT, TRUE); return TRUE;
    case WM_DRAWITEM:
        if (lparam) {
            const auto& draw = *reinterpret_cast<const DRAWITEMSTRUCT*>(lparam);
            if (draw.CtlType == ODT_LISTBOX) drawListItem(draw);
            else if (draw.CtlType == ODT_BUTTON) drawDialogButton(draw);
            else if (draw.CtlType == ODT_COMBOBOX) drawComboItem(draw);
            return TRUE;
        }
        break;
    case WM_MEASUREITEM:
        if (lparam) { auto& measure = *reinterpret_cast<MEASUREITEMSTRUCT*>(lparam); measure.itemHeight = px(30); return TRUE; }
        break;
    case WM_CTLCOLORDLG: return reinterpret_cast<INT_PTR>(host.background);
    case WM_CTLCOLORSTATIC: case WM_CTLCOLOREDIT: case WM_CTLCOLORLISTBOX: {
        HDC dc = reinterpret_cast<HDC>(wparam);
        const int id = GetDlgCtrlID(reinterpret_cast<HWND>(lparam));
        const bool muted = id == descriptionId || id == pageDetailId || id == 123 || id == 126;
        SetTextColor(dc, color(muted ? host.model.palette.text_muted : host.model.palette.text_primary));
        const bool card = host.model.kind == 4 && (id == sizeId || id == shellId || id == themeGridId || id == 121 || id == 122 || id == 123 || id == 124 || id == 126);
        if (id == queryId) {
            SetBkColor(dc, fieldColor());
            SetDCBrushColor(dc, fieldColor());
            return reinterpret_cast<INT_PTR>(GetStockObject(DC_BRUSH));
        }
        SetBkColor(dc, card ? color(host.model.palette.pane_bg) : surfaceColor());
        return reinterpret_cast<INT_PTR>(card ? host.field : host.background);
    }
    case WM_SETCURSOR:
        if (LOWORD(lparam) == HTCLIENT) {
            HWND child = reinterpret_cast<HWND>(wparam);
            wchar_t klass[32] = {}; GetClassNameW(child, klass, 32);
            int cursor = 32512;
            if (wcscmp(klass, L"Edit") == 0) cursor = 32513;
            else if (wcscmp(klass, L"Button") == 0 || wcscmp(klass, L"ComboBox") == 0) cursor = 32649;
            else if (child == GetDlgItem(window, listId) || child == GetDlgItem(window, themeGridId)) {
                POINT point; GetCursorPos(&point); ScreenToClient(child, &point);
                LRESULT hit = SendMessageW(child, LB_ITEMFROMPOINT, 0, MAKELPARAM(point.x, point.y));
                RECT item;
                if (!HIWORD(hit) && SendMessageW(child, LB_GETITEMRECT, LOWORD(hit), reinterpret_cast<LPARAM>(&item)) != LB_ERR && PtInRect(&item, point)) cursor = 32649;
            }
            SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(cursor)));
            SetWindowLongPtrW(window, DWLP_MSGRESULT, TRUE);
            return TRUE;
        }
        break;
    case WM_CLOSE: finish(0); return TRUE;
    case WM_DESTROY:
        if (host.phase != DialogHost::Phase::closing) closeDialog(false);
        return TRUE;
    }
    return FALSE;
}
}

void initializeTerminalDialogs(HWND owner, VexWindowCallbacks callbacks) {
    host.owner = owner; host.callbacks = callbacks;
    Gdiplus::GdiplusStartupInput startup;
    if (Gdiplus::GdiplusStartup(&host.graphicsToken, &startup, nullptr) != Gdiplus::Ok) host.graphicsToken = 0;
}
HWND terminalDialogWindow() { return host.window; }
bool routeTerminalDialogMessage(MSG& message) { return host.window && IsDialogMessageW(host.window, &message); }
void drawTerminalDialogBackdrop(UiRenderer& target, UiBrush& brush, float scale) {
    if (!IsWindow(host.window) || !IsWindow(host.owner)) return;
    const auto size = target.GetSize();
    brush.SetColor(D2D1::ColorF(0x000000, 0.48f));
    target.FillRectangle(D2D1::RectF(0, 0, size.width, size.height), &brush);
    RECT bounds; if (!GetWindowRect(host.window, &bounds)) return;
    POINT origin = { bounds.left, bounds.top }; if (!ScreenToClient(host.owner, &origin)) return;
    float x = origin.x / scale, y = origin.y / scale, w = (bounds.right - bounds.left) / scale, h = (bounds.bottom - bounds.top) / scale;
    for (int layer = 4; layer > 0; --layer) {
        float spread = layer * 5.0f;
        brush.SetColor(D2D1::ColorF(0x000000, 0.055f));
        target.FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x - spread, y - spread + 6, x + w + spread, y + h + spread + 6), 12 + spread, 12 + spread), &brush);
    }
}
void destroyTerminalDialogs() {
    closeDialog(false); host.owner = nullptr;
    if (host.graphicsToken) { Gdiplus::GdiplusShutdown(host.graphicsToken); host.graphicsToken = 0; }
}
void repositionTerminalDialogs() {
    if (host.phase != DialogHost::Phase::active || !IsWindow(host.owner) || !IsWindow(host.window)) return;
    RECT owner; POINT origin = { 0, 0 };
    if (!GetClientRect(host.owner, &owner) || !ClientToScreen(host.owner, &origin)) return;
    float scale = GetDpiForWindow(host.owner) / 96.0f;
    if (!isfinite(scale) || scale <= 0) return;
    const bool dpiChanged = host.scale != scale;
    host.scale = scale;
    host.width = fminf(dialogWidth(), (owner.right - owner.left) / scale - 32);
    host.height = fminf(dialogHeight(), (owner.bottom - owner.top) / scale - 40);
    if (host.width <= 0 || host.height <= 0) return;
    HFONT oldFont = nullptr, oldHeading = nullptr, oldSymbols = nullptr;
    if (dpiChanged) {
        HFONT font = CreateFontW(-px(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT heading = CreateFontW(-px(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT symbols = CreateFontW(-px(22), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe MDL2 Assets");
        if (font && heading && symbols) { oldFont = host.font; oldHeading = host.heading; oldSymbols = host.symbols; host.font = font; host.heading = heading; host.symbols = symbols; }
        else { if (font) DeleteObject(font); if (heading) DeleteObject(heading); if (symbols) DeleteObject(symbols); }
    }
    const int top = host.model.kind == 2 ? (int)fminf((float)px(64), (owner.bottom - px(host.height)) / 2.0f) : (owner.bottom - px(host.height)) / 2;
    SetWindowPos(host.window, HWND_TOP, origin.x + (owner.right - px(host.width)) / 2, origin.y + top, px(host.width), px(host.height), SWP_NOACTIVATE);
    HRGN rounded = CreateRoundRectRgn(0, 0, px(host.width) + 1, px(host.height) + 1, px(24), px(24));
    if (rounded && !SetWindowRgn(host.window, rounded, FALSE)) DeleteObject(rounded);
    const float dx = host.width - host.baseWidth, dy = host.height - host.baseHeight;
    const bool compact = host.model.kind == 4 && host.height < 430;
    for (size_t i = 0; i < host.controlCount; ++i) {
        const auto& c = host.controls[i];
        const int id = GetDlgCtrlID(c.window);
        float x = c.x, y = c.y, w = c.width, h = c.height;
        if (id >= firstPageId && id < firstPageId + 4) x += dx / 2;
        else if (id >= firstModeId && id < firstModeId + 4) { x += (id - firstModeId) * dx / 4; w += dx / 4; }
        else if (id == IDOK || id == IDCANCEL || id == closeId || id == sizeId || id == shellId) x += dx;
        else if (w > host.baseWidth * 0.5f) w += dx;
        if (id == IDOK || id == IDCANCEL) y += dy;
        if (id == listId || id == themeGridId) h += dy;
        if (id == 123 || id == 126) w = fminf(w, host.width - 350);
        if (compact) {
            if (id >= firstPageId && id < firstPageId + 4) { y = 44; h = 44; }
            if (id == pageTitleId || id == pageDetailId || id == 121 || id == 122 || id == 123 || id == 126 || id == sizeId || id == shellId || id == blinkId) y -= 46;
            if (id == pageDetailId) h = 30;
            if (id == 124) y = 166;
            if (id == themeGridId) { y = 198; h = host.height - 276; }
        }
        MoveWindow(c.window, px(x), px(y), px(fmaxf(w, 1)), px(fmaxf(h, 1)), FALSE);
        if (oldFont) SendMessageW(c.window, WM_SETFONT, reinterpret_cast<WPARAM>(id == pageTitleId || (id == titleId && host.model.kind != 4) ? host.heading : host.font), FALSE);
        if (id == listId || id == themeGridId) {
            SendMessageW(c.window, LB_SETITEMHEIGHT, 0, px(host.model.kind == 1 || id == themeGridId ? themeRowHeight() : listRowHeight()));
            if (host.model.kind == 1 || id == themeGridId) SendMessageW(c.window, LB_SETCOLUMNWIDTH, px(160), 0);
        }
        if (id == shellId || id == themeId) {
            SendMessageW(c.window, CB_SETITEMHEIGHT, (WPARAM)-1, px(30));
            SendMessageW(c.window, CB_SETITEMHEIGHT, 0, px(30));
        }
    }
    if (oldFont) DeleteObject(oldFont);
    if (oldHeading) DeleteObject(oldHeading);
    if (oldSymbols) DeleteObject(oldSymbols);
    RedrawWindow(host.window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_ERASE);
}
extern "C" void vex_dialog_close() { closeDialog(true); }
extern "C" uint32_t vex_dialog_first_visible() {
    HWND list = host.window ? GetDlgItem(host.window, listId) : nullptr;
    if (!list) return 0;
    LRESULT first = SendMessageW(list, LB_GETTOPINDEX, 0, 0);
    return first >= 0 && (uint64_t)first <= UINT32_MAX ? (uint32_t)first : 0;
}
extern "C" void vex_dialog_warning(VexDialogText message) {
    wchar_t text[512];
    if (decodeText(message, text, 512)) MessageBoxW(host.window ? host.window : host.owner, text, L"Vex settings", MB_OK | MB_ICONWARNING);
}
extern "C" int vex_dialog_open(VexDialogRequest request) {
    if (!host.graphicsToken) return ERROR_NOT_READY;
    if (!IsWindow(host.owner) || request.kind < 1 || request.kind > 6 || request.count > itemLimit || request.shell_count > shellLimit) return ERROR_INVALID_PARAMETER;
    static DialogModel next;
    memset(&next, 0, sizeof(next));
    if (!decodeText(request.title, next.title, 128) || !decodeText(request.description, next.description, 512) || !decodeText(request.accept_label, next.accept, 64) ||
        !copyItems(request.items, request.count, next.items, itemLimit) || !copyItems(request.shells, request.shell_count, next.shells, shellLimit)) return ERROR_INVALID_DATA;
    next.kind = request.kind; next.selected = request.selected; next.mode = request.mode; next.count = request.count; next.shellCount = request.shell_count; next.settings = request.settings; next.palette = request.palette;
    if (host.phase == DialogHost::Phase::active) {
        if (host.model.kind != request.kind) closeDialog(true);
    }
    if (host.phase == DialogHost::Phase::active) {
        host.synchronizing = true;
        host.model = next;
        HBRUSH background = CreateSolidBrush(surfaceColor());
        HBRUSH field = CreateSolidBrush(color(host.model.palette.pane_bg));
        if (background && field) { DeleteObject(host.background); DeleteObject(host.field); host.background = background; host.field = field; }
        else { if (background) DeleteObject(background); if (field) DeleteObject(field); }
        fillList();
        HWND list = GetDlgItem(host.window, listId);
        if (list) SetWindowTheme(list, host.model.palette.is_dark ? L"DarkMode_Explorer" : L"Explorer", nullptr);
        if (host.model.kind == 2) repositionTerminalDialogs();
        RedrawWindow(host.window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
        host.synchronizing = false;
        return 0;
    }
    if (host.phase != DialogHost::Phase::closed) return ERROR_BUSY;
    host.model = next;
    host.draft = request.settings;
    host.page = SettingsPage::general;
    host.phase = DialogHost::Phase::creating;
    host.previousFocus = GetFocus();
    struct Template { DLGTEMPLATE dialog; WORD menu, klass, title; } layout = {};
    layout.dialog.style = WS_POPUP | WS_CLIPCHILDREN;
    layout.dialog.dwExtendedStyle = WS_EX_TOOLWINDOW | WS_EX_CONTROLPARENT;
    HWND result = CreateDialogIndirectParamW(GetModuleHandleW(nullptr), &layout.dialog, host.owner, dialogProcedure, 0);
    if (!result || !IsWindow(result)) { closeDialog(false); return ERROR_NOT_ENOUGH_MEMORY; }
    host.window = result;
    host.phase = DialogHost::Phase::active;
    repositionTerminalDialogs();
    EnableWindow(host.owner, FALSE);
    ShowWindow(result, SW_SHOW);
    SetActiveWindow(result);
    HWND focus = GetDlgItem(result, request.kind == 4 ? shellId : request.kind == 6 ? IDCANCEL : request.kind == 2 ? queryId : listId);
    SetFocus(focus);
    return 0;
}
