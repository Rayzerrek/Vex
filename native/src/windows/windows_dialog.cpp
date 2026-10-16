#include "windows_dialog.h"
#include "ui_renderer.h"
#include <commctrl.h>
#include <dwmapi.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

namespace {
constexpr int titleId = 100, descriptionId = 101, queryId = 102, listId = 103;
constexpr int themeId = 110, sizeId = 111, blinkId = 112, shellId = 113, modeId = 114, themeGridId = 115;
constexpr int pageTitleId = 104, pageDetailId = 105, closeId = 125, firstPageId = 130;
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
    VexWindowCallbacks callbacks;
    DialogModel model;
    HFONT font, heading;
    HBRUSH background, field;
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
LRESULT CALLBACK commandListKeys(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if (message == WM_KEYDOWN && navigateCommandList(wparam)) return 0;
    return DefSubclassProc(control, message, wparam, lparam);
}
LRESULT CALLBACK selectableText(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if (message == WM_KEYDOWN && GetDlgCtrlID(control) == queryId && navigateCommandList(wparam)) return 0;
    if (message == WM_KEYDOWN && wparam == 'A' && GetKeyState(VK_CONTROL) < 0) {
        SendMessageW(control, EM_SETSEL, 0, -1);
        return 0;
    }
    return DefSubclassProc(control, message, wparam, lparam);
}
HWND control(const wchar_t* klass, const wchar_t* text, DWORD style, int id, float x, float y, float width, float height) {
    if (host.controlCount >= 40) return nullptr;
    HWND result = CreateWindowExW(0, klass, text, WS_CHILD | WS_VISIBLE | style, px(x), px(y), px(width), px(height), host.window, reinterpret_cast<HMENU>((INT_PTR)id), GetModuleHandleW(nullptr), nullptr);
    if (!result) return nullptr;
    SendMessageW(result, WM_SETFONT, reinterpret_cast<WPARAM>(id == pageTitleId || (id == titleId && host.model.kind != 4) ? host.heading : host.font), FALSE);
    if (wcscmp(klass, L"EDIT") == 0 && !SetWindowSubclass(result, selectableText, 1, 0)) { DestroyWindow(result); return nullptr; }
    if (wcscmp(klass, L"LISTBOX") == 0 && host.model.kind == 2 && !SetWindowSubclass(result, commandListKeys, 1, 0)) { DestroyWindow(result); return nullptr; }
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
void roundedFill(HDC dc, RECT bounds, COLORREF fill, COLORREF border, int radius) {
    HBRUSH brush = CreateSolidBrush(fill);
    HPEN pen = CreatePen(PS_SOLID, 1, border);
    if (!brush || !pen) { if (brush) DeleteObject(brush); if (pen) DeleteObject(pen); return; }
    HGDIOBJ oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, pen);
    RoundRect(dc, bounds.left, bounds.top, bounds.right, bounds.bottom, radius, radius);
    SelectObject(dc, oldBrush); SelectObject(dc, oldPen);
    DeleteObject(brush); DeleteObject(pen);
}
void drawListItem(const DRAWITEMSTRUCT& draw) {
    if (draw.itemID == (UINT)-1 || draw.itemID >= host.model.count) return;
    const DialogItem& item = host.model.items[draw.itemID];
    const bool selected = (draw.itemState & ODS_SELECTED) != 0;
    const bool theme = host.model.kind == 1 || draw.CtlID == themeGridId;
    FillRect(draw.hDC, &draw.rcItem, host.background);
    SetBkMode(draw.hDC, TRANSPARENT);
    SelectObject(draw.hDC, host.font);
    if (theme) {
        RECT card = draw.rcItem; card.left += px(5); card.right -= px(7); card.top += px(4); card.bottom = card.top + px(72);
        roundedFill(draw.hDC, card, item.background, selected ? color(host.model.palette.accent) : color(host.model.palette.border), px(18));
        RECT preview = card; preview.left += px(12); preview.top += px(7); preview.bottom -= px(24);
        SelectObject(draw.hDC, host.heading); SetTextColor(draw.hDC, item.foreground);
        DrawTextW(draw.hDC, L"Aa", 2, &preview, DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(draw.hDC, host.font);
        for (int i = 0; i < 5; ++i) {
            HBRUSH dot = CreateSolidBrush(item.swatches[i]);
            if (!dot) continue;
            HGDIOBJ old = SelectObject(draw.hDC, dot), oldPen = SelectObject(draw.hDC, GetStockObject(NULL_PEN));
            int x = card.left + px(12 + i * 14), y = card.bottom - px(20);
            Ellipse(draw.hDC, x, y, x + px(10), y + px(10));
            SelectObject(draw.hDC, oldPen); SelectObject(draw.hDC, old); DeleteObject(dot);
        }
        RECT name = draw.rcItem; name.top = card.bottom + px(5); name.bottom -= px(2);
        SetTextColor(draw.hDC, color(selected ? host.model.palette.text_primary : host.model.palette.text_muted));
        DrawTextW(draw.hDC, item.label, -1, &name, DT_CENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        if (draw.itemState & ODS_FOCUS) DrawFocusRect(draw.hDC, &card);
        return;
    }
    RECT row = draw.rcItem; row.top += px(2); row.bottom -= px(2); row.left += px(2); row.right -= px(3);
    roundedFill(draw.hDC, row, color(selected ? host.model.palette.active_tab_bg : host.model.palette.titlebar_bg), color(selected ? host.model.palette.active_tab_border : host.model.palette.titlebar_bg), px(12));
    RECT text = row; text.left += px(12); text.top += px(5); text.right -= px(10);
    SetTextColor(draw.hDC, color(host.model.palette.text_primary));
    DrawTextW(draw.hDC, item.label, -1, &text, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    text.top += px(19); SetTextColor(draw.hDC, color(host.model.palette.text_muted));
    DrawTextW(draw.hDC, item.detail, -1, &text, DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    if (draw.itemState & ODS_FOCUS) DrawFocusRect(draw.hDC, &row);
}
void drawDialogButton(const DRAWITEMSTRUCT& draw) {
    const int id = (int)draw.CtlID;
    const bool navigation = id >= firstPageId && id < firstPageId + 4;
    const bool checked = navigation ? (int)host.page == id - firstPageId : id == blinkId && host.draft.cursor_blink;
    const bool pressed = (draw.itemState & ODS_SELECTED) != 0;
    RECT bounds = draw.rcItem; InflateRect(&bounds, -1, -1);
    FillRect(draw.hDC, &draw.rcItem, host.background);
    uint32_t surface = checked || pressed ? host.model.palette.active_tab_bg : host.model.palette.titlebar_bg;
    uint32_t border = checked || id == IDOK ? host.model.palette.active_tab_border : host.model.palette.border;
    if (id == closeId) border = surface;
    roundedFill(draw.hDC, bounds, color(surface), color(border), px(navigation ? 12 : 10));
    SetBkMode(draw.hDC, TRANSPARENT); SelectObject(draw.hDC, host.font);
    SetTextColor(draw.hDC, color(checked || id == IDOK ? host.model.palette.accent : host.model.palette.text_primary));
    wchar_t label[96]; GetWindowTextW(draw.hwndItem, label, 96);
    if (id == blinkId) {
        RECT toggle = bounds; toggle.left = toggle.right - px(48); toggle.right -= px(8); toggle.top += px(6); toggle.bottom -= px(6);
        roundedFill(draw.hDC, toggle, color(checked ? host.model.palette.accent : host.model.palette.border), color(checked ? host.model.palette.accent : host.model.palette.border), px(22));
        RECT knob = toggle; knob.left += checked ? px(20) : px(3); knob.right = knob.left + px(14); knob.top += px(3); knob.bottom -= px(3);
        roundedFill(draw.hDC, knob, RGB(245,245,245), RGB(245,245,245), px(16));
        bounds.right -= px(52);
    }
    if (id == blinkId) bounds.left += px(12);
    DrawTextW(draw.hDC, label, -1, &bounds, DT_SINGLELINE | DT_VCENTER | (id == blinkId ? DT_LEFT : DT_CENTER) | DT_NOPREFIX);
    if (draw.itemState & ODS_FOCUS) { InflateRect(&bounds, -px(3), -px(3)); DrawFocusRect(draw.hDC, &bounds); }
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
    InvalidateRect(host.window, nullptr, TRUE);
}
void paintDialogSurface(HDC dc) {
    RECT bounds; GetClientRect(host.window, &bounds);
    FillRect(dc, &bounds, host.background);
    if (host.model.kind == 4) {
        RECT header = { 0, 0, bounds.right, px(46) }; FillRect(dc, &header, host.field);
        if (host.page != SettingsPage::about) {
            const bool compact = host.height < 430;
            RECT card = { px(24), px(compact ? 128 : 152), bounds.right - px(24), host.page == SettingsPage::appearance ? bounds.bottom - px(64) : px((host.page == SettingsPage::terminal ? 286 : 236) - (compact ? 20 : 0)) };
            roundedFill(dc, card, color(host.model.palette.pane_bg), color(host.model.palette.border), px(16));
        }
    }
    HBRUSH border = CreateSolidBrush(color(host.model.palette.border));
    if (border) { FrameRect(dc, &bounds, border); DeleteObject(border); }
}
bool createControls(float width, float height) {
    const bool settings = host.model.kind == 4;
    bool ok = control(L"EDIT", host.model.title, ES_READONLY | ES_AUTOHSCROLL, titleId, settings ? 16 : 22, settings ? 12 : 16, settings ? 86 : width - 80, settings ? 24 : 32) != nullptr;
    if (settings) {
        const wchar_t* pages[] = { L"General", L"Appearance", L"Terminal", L"About" };
        for (int i = 0; i < 4; ++i) ok = control(L"BUTTON", pages[i], BS_OWNERDRAW | WS_TABSTOP, firstPageId + i, 112.0f + i * 104.0f, 8, 100, 30) && ok;
        ok = control(L"EDIT", L"General", ES_READONLY | ES_AUTOHSCROLL, pageTitleId, 28, 72, width - 56, 32) && ok;
        ok = control(L"EDIT", L"Defaults for new terminal sessions.", ES_READONLY | ES_MULTILINE, pageDetailId, 28, 112, width - 56, 36) && ok;
        HWND theme = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP | WS_VSCROLL, themeId, 205, 100, width - 229, 220);
        ok = theme && ok;
        if (theme) { fillCombo(theme, host.model.items, host.model.count, host.model.settings.theme); ShowWindow(theme, SW_HIDE); }
        ok = control(L"STATIC", L"Color theme", 0, 124, 40, 166, width - 80, 22) && ok;
        HWND grid = control(L"LISTBOX", L"", WS_TABSTOP | WS_HSCROLL | LBS_MULTICOLUMN | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, themeGridId, 36, 198, width - 72, height - 286);
        ok = grid && ok;
        if (grid) { SendMessageW(grid, LB_SETITEMHEIGHT, 0, px(104)); SendMessageW(grid, LB_SETCOLUMNWIDTH, px(132), 0); fillList(themeGridId); }
        ok = control(L"STATIC", L"Font size", 0, 121, 40, 174, 180, 24) && ok;
        ok = control(L"STATIC", L"Point size of terminal text (9-32).", 0, 126, 40, 199, 320, 24) && ok;
        wchar_t size[8]; swprintf(size, 8, L"%u", host.model.settings.font_size);
        HWND fontSize = control(L"EDIT", size, ES_NUMBER | ES_AUTOHSCROLL | WS_BORDER | WS_TABSTOP, sizeId, width - 132, 177, 80, 28);
        ok = fontSize && ok;
        if (fontSize) SendMessageW(fontSize, EM_SETLIMITTEXT, 2, 0);
        ok = control(L"BUTTON", L"Blink terminal cursor", BS_OWNERDRAW | WS_TABSTOP, blinkId, 38, 238, width - 76, 36) && ok;
        ok = control(L"STATIC", L"Default shell", 0, 122, 40, 174, 160, 24) && ok;
        ok = control(L"STATIC", L"Existing sessions are not restarted.", 0, 123, 40, 199, 320, 24) && ok;
        HWND shell = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP | WS_VSCROLL, shellId, width - 296, 177, 244, 220);
        ok = shell && ok;
        if (shell) fillCombo(shell, host.model.shells, host.model.shellCount, host.model.settings.shell);
        showPage();
    } else {
        ok = control(L"EDIT", host.model.description, ES_READONLY | ES_MULTILINE, descriptionId, 22, 56, width - 44, host.model.kind == 6 ? 92 : 38) && ok;
    }
    if (!settings && host.model.kind != 6) {
        float top = 100;
        if (host.model.kind == 2) {
            HWND edit = control(L"EDIT", L"", ES_AUTOHSCROLL | WS_TABSTOP | WS_BORDER, queryId, 22, 98, width - 44, 30);
            ok = edit && ok;
            if (edit) SendMessageW(edit, EM_SETLIMITTEXT, 128, 0);
            top = 140;
        } else if (host.model.kind == 5) {
            HWND mode = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP, modeId, 22, 96, width - 44, 200);
            ok = mode && ok;
            if (mode) {
                const wchar_t* modes[] = { L"New tab", L"Split right", L"Split down", L"Set default shell" };
                for (int i = 0; i < 4; ++i) { SendMessageW(mode, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(modes[i])); SendMessageW(mode, CB_SETITEMDATA, i, i); }
                SendMessageW(mode, CB_SETCURSEL, host.model.mode, 0);
            }
            top = 138;
        }
        const bool themes = host.model.kind == 1;
        HWND list = control(L"LISTBOX", L"", WS_TABSTOP | (themes ? WS_HSCROLL | LBS_MULTICOLUMN : WS_VSCROLL) | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, listId, 22, top, width - 44, height - top - 68);
        ok = list && ok;
        if (list) {
            SendMessageW(list, LB_SETITEMHEIGHT, 0, px(themes ? 104 : 48));
            if (themes) SendMessageW(list, LB_SETCOLUMNWIDTH, px(132), 0);
            fillList();
        }
    }
    ok = control(L"BUTTON", L"×", BS_OWNERDRAW | WS_TABSTOP, closeId, width - 42, 8, 28, 28) && ok;
    ok = control(L"BUTTON", L"Cancel", WS_TABSTOP | BS_OWNERDRAW, IDCANCEL, width - 242, height - 50, 96, 30) && ok;
    ok = control(L"BUTTON", host.model.accept, WS_TABSTOP | BS_OWNERDRAW, IDOK, width - 134, height - 50, 112, 30) && ok;
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
        host.font = CreateFontW(-px(13), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        host.heading = CreateFontW(-px(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        host.background = CreateSolidBrush(color(host.model.palette.titlebar_bg));
        host.field = CreateSolidBrush(color(host.model.palette.pane_bg));
        RECT owner; GetClientRect(host.owner, &owner);
        POINT origin = { 0, 0 }; ClientToScreen(host.owner, &origin);
        const float width = fminf(host.model.kind == 4 ? 880.0f : host.model.kind == 1 ? 580.0f : 600.0f, (owner.right - owner.left) / host.scale - 32);
        const float height = fminf(host.model.kind == 6 ? 240.0f : host.model.kind == 4 ? 560.0f : 480.0f, (owner.bottom - owner.top) / host.scale - 40);
        host.width = width; host.height = height; host.baseWidth = width; host.baseHeight = height;
        host.controlCount = 0;
        SetWindowPos(window, HWND_TOP, origin.x + (owner.right - px(width)) / 2, origin.y + (owner.bottom - px(height)) / 2, px(width), px(height), SWP_NOACTIVATE);
        HRGN rounded = CreateRoundRectRgn(0, 0, px(width) + 1, px(height) + 1, px(24), px(24));
        if (rounded && !SetWindowRgn(window, rounded, FALSE)) DeleteObject(rounded);
        DWORD corner = 2; DwmSetWindowAttribute(window, 33, &corner, sizeof(corner));
        if (!host.font || !host.heading || !host.background || !host.field || !createControls(width, height)) { closeDialog(false); return FALSE; }
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
        if (LOWORD(wparam) == modeId && HIWORD(wparam) == CBN_SELCHANGE) { uint32_t mode; if (comboSelection(modeId, mode)) notify(5, mode); return TRUE; }
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
            return TRUE;
        }
        break;
    case WM_CTLCOLORDLG: return reinterpret_cast<INT_PTR>(host.background);
    case WM_CTLCOLORSTATIC: case WM_CTLCOLOREDIT: case WM_CTLCOLORLISTBOX: {
        HDC dc = reinterpret_cast<HDC>(wparam);
        SetTextColor(dc, color(host.model.palette.text_primary));
        const int id = GetDlgCtrlID(reinterpret_cast<HWND>(lparam));
        const bool card = host.model.kind == 4 && (id == sizeId || id == shellId || id == 121 || id == 122 || id == 123 || id == 124 || id == 126);
        SetBkColor(dc, color(card ? host.model.palette.pane_bg : host.model.palette.titlebar_bg));
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

void initializeTerminalDialogs(HWND owner, VexWindowCallbacks callbacks) { host.owner = owner; host.callbacks = callbacks; }
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
void destroyTerminalDialogs() { closeDialog(false); host.owner = nullptr; }
void repositionTerminalDialogs() {
    if (host.phase != DialogHost::Phase::active || !IsWindow(host.owner) || !IsWindow(host.window)) return;
    RECT owner; POINT origin = { 0, 0 };
    if (!GetClientRect(host.owner, &owner) || !ClientToScreen(host.owner, &origin)) return;
    float scale = GetDpiForWindow(host.owner) / 96.0f;
    if (!isfinite(scale) || scale <= 0) return;
    const bool dpiChanged = host.scale != scale;
    host.scale = scale;
    host.width = fminf(host.model.kind == 4 ? 880.0f : host.model.kind == 1 ? 580.0f : 600.0f, (owner.right - owner.left) / scale - 32);
    host.height = fminf(host.model.kind == 6 ? 240.0f : host.model.kind == 4 ? 560.0f : 480.0f, (owner.bottom - owner.top) / scale - 40);
    if (host.width <= 0 || host.height <= 0) return;
    HFONT oldFont = nullptr, oldHeading = nullptr;
    if (dpiChanged) {
        HFONT font = CreateFontW(-px(13), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT heading = CreateFontW(-px(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        if (font && heading) { oldFont = host.font; oldHeading = host.heading; host.font = font; host.heading = heading; }
        else { if (font) DeleteObject(font); if (heading) DeleteObject(heading); }
    }
    SetWindowPos(host.window, HWND_TOP, origin.x + (owner.right - px(host.width)) / 2, origin.y + (owner.bottom - px(host.height)) / 2, px(host.width), px(host.height), SWP_NOACTIVATE);
    HRGN rounded = CreateRoundRectRgn(0, 0, px(host.width) + 1, px(host.height) + 1, px(24), px(24));
    if (rounded && !SetWindowRgn(host.window, rounded, FALSE)) DeleteObject(rounded);
    const float dx = host.width - host.baseWidth, dy = host.height - host.baseHeight;
    const bool compact = host.model.kind == 4 && host.height < 430;
    for (size_t i = 0; i < host.controlCount; ++i) {
        const auto& c = host.controls[i];
        const int id = GetDlgCtrlID(c.window);
        float x = c.x, y = c.y, w = c.width, h = c.height;
        if (id == IDOK || id == IDCANCEL || id == closeId || id == sizeId || id == shellId) x += dx;
        else if (w > host.baseWidth * 0.5f) w += dx;
        if (id == IDOK || id == IDCANCEL) y += dy;
        if (id == listId || id == themeGridId) h += dy;
        if (id == 123) w = fminf(w, host.width - 348);
        if (compact) {
            if (id == pageTitleId) y -= 12;
            if (id == pageDetailId) { y -= 14; h = 28; }
            if (id == 124 || id == themeGridId) y -= 24;
            if (id == themeGridId) h = fmaxf(h + 24, 122);
            if (id == 121 || id == 122 || id == 123 || id == 126 || id == sizeId || id == shellId || id == blinkId) y -= 20;
        }
        MoveWindow(c.window, px(x), px(y), px(fmaxf(w, 1)), px(fmaxf(h, 1)), FALSE);
        if (oldFont) SendMessageW(c.window, WM_SETFONT, reinterpret_cast<WPARAM>(id == pageTitleId || (id == titleId && host.model.kind != 4) ? host.heading : host.font), FALSE);
        if (id == listId || id == themeGridId) {
            SendMessageW(c.window, LB_SETITEMHEIGHT, 0, px(host.model.kind == 1 || id == themeGridId ? 104 : 48));
            if (host.model.kind == 1 || id == themeGridId) SendMessageW(c.window, LB_SETCOLUMNWIDTH, px(132), 0);
        }
    }
    if (oldFont) DeleteObject(oldFont);
    if (oldHeading) DeleteObject(oldHeading);
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
        HBRUSH background = CreateSolidBrush(color(host.model.palette.titlebar_bg));
        HBRUSH field = CreateSolidBrush(color(host.model.palette.pane_bg));
        if (background && field) { DeleteObject(host.background); DeleteObject(host.field); host.background = background; host.field = field; }
        else { if (background) DeleteObject(background); if (field) DeleteObject(field); }
        fillList();
        InvalidateRect(host.window, nullptr, TRUE);
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
