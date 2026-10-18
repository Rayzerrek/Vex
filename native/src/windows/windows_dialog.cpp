#include "windows_dialog.h"
#include "ui_renderer.h"
#include "overlay_scrollbar.h"
#include <commctrl.h>
#include <gdiplus.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <wctype.h>

namespace {
constexpr int titleId = 100, descriptionId = 101, queryId = 102, listId = 103;
constexpr int fontId = 116, fontLabelId = 117;
constexpr int sizeId = 111, blinkId = 112, shellId = 113, themeGridId = 115;
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
struct ThemeCardCache {
    HBITMAP images[2] = {};
    int width = 0, height = 0;
    float scale = 0;
};
struct DialogHost {
    HWND owner, window, previousFocus;
    ULONG_PTR graphicsToken = 0;
    VexWindowCallbacks callbacks;
    DialogModel model;
    HFONT font, heading, symbols;
    HBRUSH background, field;
    uint64_t themeCardPaintCount = 0, dialogPaintCount = 0, surfacePaintCount = 0;
    enum class Phase { closed, creating, active, closing } phase = Phase::closed;
    bool synchronizing = false;
    ULONGLONG revealStarted = 0;
    SettingsPage page = SettingsPage::general;
    VexUserSettings draft = {};
    float scale = 1, width = 600, height = 470, baseWidth = 600, baseHeight = 470;
    ControlLayout controls[40];
    size_t controlCount = 0;
    ThemeCardCache themeCards[itemLimit];
};
DialogHost host;
struct DialogPaintBuffer {
    HDC dc, destination, memory = nullptr;
    HBITMAP bitmap = nullptr;
    HGDIOBJ previous = nullptr;
    RECT bounds;
    DialogPaintBuffer(HDC target, RECT area) : dc(target), destination(target), bounds(area) {
        memory = CreateCompatibleDC(target);
        if (memory) bitmap = CreateCompatibleBitmap(target, area.right - area.left, area.bottom - area.top);
        if (bitmap) {
            previous = SelectObject(memory, bitmap);
            SetWindowOrgEx(memory, area.left, area.top, nullptr);
            dc = memory;
        }
    }
    DialogPaintBuffer(const DialogPaintBuffer&) = delete;
    DialogPaintBuffer& operator=(const DialogPaintBuffer&) = delete;
    ~DialogPaintBuffer() {
        if (bitmap) {
            BitBlt(destination, bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top, memory, bounds.left, bounds.top, SRCCOPY);
            SelectObject(memory, previous);
            DeleteObject(bitmap);
        }
        if (memory) DeleteDC(memory);
    }
};
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
int themeRowHeight() {
    const float available = host.model.kind == 4 ? host.height - (host.height < 430 ? 198 : 246) : host.height - 170;
    return (int)fmaxf(104, floorf(available / 2));
}
int themeColumnWidth() { return (int)floorf((host.width - 32) / 3); }
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
void clearThemeCardCache() {
    for (auto& card : host.themeCards) for (auto& image : card.images) {
        if (image) { DeleteObject(image); image = nullptr; }
    }
}
void releasePaint() {
    clearThemeCardCache();
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
    if (IsWindow(dialog)) KillTimer(dialog, 7);
    host.revealStarted = 0;
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
void finish(uint32_t event, uint32_t value = 0) {
    closeDialog(true);
    notify(event, value);
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
struct ListOverlay {
    OverlayScrollbarVisibility visibility;
    bool hovered = false, dragging = false, horizontal = false, ownsCapture = false;
    float grab = 0;
    int unitsPerStep = 1;
};
constexpr UINT_PTR overlaySubclass = 8, overlayTimer = 8;
bool overlayAnimations() {
    BOOL enabled = TRUE;
    SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0);
    return enabled != FALSE;
}
OverlayScrollbar listOverlayGeometry(HWND control, ListOverlay& state) {
    RECT bounds; GetClientRect(control, &bounds);
    const int count = (int)SendMessageW(control, LB_GETCOUNT, 0, 0);
    const int row = (int)SendMessageW(control, LB_GETITEMHEIGHT, 0, 0);
    if (row <= 0 || count <= 0) return {};
    const int visibleRows = bounds.bottom / row;
    if (visibleRows <= 0) return {};
    int first = (int)SendMessageW(control, LB_GETTOPINDEX, 0, 0);
    state.unitsPerStep = 1;
    if (state.horizontal) {
        RECT item;
        if (SendMessageW(control, LB_GETITEMRECT, 0, reinterpret_cast<LPARAM>(&item)) == LB_ERR) return {};
        const int width = item.right - item.left;
        if (width <= 0) return {};
        state.unitsPerStep = visibleRows;
        return OverlayScrollbar::compute((count + visibleRows - 1) / visibleRows, bounds.right / width,
            first / visibleRows, (float)bounds.right, (float)px(24));
    }
    return OverlayScrollbar::compute(count, visibleRows, first, (float)bounds.bottom, (float)px(24));
}
bool listOverlayHit(HWND control, const ListOverlay& state, LPARAM point) {
    RECT bounds; GetClientRect(control, &bounds);
    const int x = (short)LOWORD(point), y = (short)HIWORD(point);
    return x >= 0 && y >= 0 && x < bounds.right && y < bounds.bottom &&
        (state.horizontal ? y >= bounds.bottom - px(14) : x >= bounds.right - px(14));
}
void wakeListOverlay(HWND control, ListOverlay& state) {
    state.visibility.reveal(GetTickCount64());
    SetTimer(control, overlayTimer, 900, nullptr);
    InvalidateRect(control, nullptr, FALSE);
}
void paintListOverlay(HWND control, HDC dc, ListOverlay& state) {
    const auto geometry = listOverlayGeometry(control, state);
    if (!geometry.maximum) return;
    const bool active = state.hovered || state.dragging;
    const float opacity = state.visibility.opacity(GetTickCount64(), active, overlayAnimations());
    if (opacity <= 0) return;
    RECT bounds; GetClientRect(control, &bounds);
    const float width = (float)px(active ? 5 : 3), inset = (float)px(4);
    const float x = state.horizontal ? geometry.thumbTop : bounds.right - inset - width;
    const float y = state.horizontal ? bounds.bottom - inset - width : geometry.thumbTop;
    const float w = state.horizontal ? geometry.thumbHeight : width;
    const float h = state.horizontal ? width : geometry.thumbHeight;
    const COLORREF ink = color(host.model.palette.text_primary);
    Gdiplus::Graphics graphics(dc);
    graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
    Gdiplus::SolidBrush brush(Gdiplus::Color((BYTE)lroundf(255 * (active ? 0.38f : 0.22f) * opacity), GetRValue(ink), GetGValue(ink), GetBValue(ink)));
    Gdiplus::GraphicsPath path;
    path.AddArc(x, y, width, width, 180, 90);
    path.AddArc(x + w - width, y, width, width, 270, 90);
    path.AddArc(x + w - width, y + h - width, width, width, 0, 90);
    path.AddArc(x, y + h - width, width, width, 90, 90);
    path.CloseFigure();
    graphics.FillPath(&brush, &path);
}
LRESULT CALLBACK listOverlayInput(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR data) {
    auto& state = *reinterpret_cast<ListOverlay*>(data);
    if (message == WM_NCDESTROY) {
        KillTimer(control, overlayTimer);
        RemoveWindowSubclass(control, listOverlayInput, overlaySubclass);
        HeapFree(GetProcessHeap(), 0, &state);
        return DefSubclassProc(control, message, wparam, lparam);
    }
    if (message == WM_STYLECHANGING && wparam == (WPARAM)GWL_STYLE)
        reinterpret_cast<STYLESTRUCT*>(lparam)->styleNew &= ~(WS_VSCROLL | WS_HSCROLL);
    // LISTBOX can re-enable scroll styles internally when its range changes.
    // Keep the entire window client-owned even when Win32 updates that range.
    if (message == WM_NCCALCSIZE || message == WM_NCPAINT) return 0;
    if (message == WM_NCHITTEST) return HTCLIENT;
    if (message == WM_PAINT) {
        PAINTSTRUCT paint;
        HDC dc = BeginPaint(control, &paint);
        if (dc) {
            DialogPaintBuffer buffer(dc, paint.rcPaint);
            FillRect(buffer.dc, &paint.rcPaint, host.background);
            DefSubclassProc(control, WM_PRINTCLIENT, reinterpret_cast<WPARAM>(buffer.dc), PRF_CLIENT);
            paintListOverlay(control, buffer.dc, state);
        }
        EndPaint(control, &paint);
        return 0;
    }
    if (message == WM_TIMER && wparam == overlayTimer) {
        const float opacity = state.visibility.opacity(GetTickCount64(), state.hovered || state.dragging, overlayAnimations());
        KillTimer(control, overlayTimer);
        if (opacity > 0 && !state.hovered && !state.dragging) SetTimer(control, overlayTimer, 16, nullptr);
        InvalidateRect(control, nullptr, FALSE);
        return 0;
    }
    if (message == WM_MOUSEMOVE) {
        const bool hovered = listOverlayGeometry(control, state).maximum && listOverlayHit(control, state, lparam);
        if (hovered != state.hovered) {
            state.hovered = hovered;
            wakeListOverlay(control, state);
        }
        TRACKMOUSEEVENT tracking = { sizeof(tracking), TME_LEAVE, control, 0 };
        TrackMouseEvent(&tracking);
        if (state.dragging) {
            const auto geometry = listOverlayGeometry(control, state);
            const float point = (float)(state.horizontal ? (short)LOWORD(lparam) : (short)HIWORD(lparam));
            SendMessageW(control, LB_SETTOPINDEX, geometry.target(point, state.grab) * state.unitsPerStep, 0);
            wakeListOverlay(control, state);
            return 0;
        }
    }
    if (message == WM_MOUSELEAVE && state.hovered) {
        state.hovered = false;
        wakeListOverlay(control, state);
    }
    if (message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK) {
        const auto geometry = listOverlayGeometry(control, state);
        if (geometry.maximum && listOverlayHit(control, state, lparam)) {
            const float point = (float)(state.horizontal ? (short)LOWORD(lparam) : (short)HIWORD(lparam));
            state.grab = point >= geometry.thumbTop && point <= geometry.thumbTop + geometry.thumbHeight ? point - geometry.thumbTop : geometry.thumbHeight / 2;
            state.dragging = true;
            // Combo drop-downs already own capture; do not close them on thumb release.
            state.ownsCapture = GetCapture() != control;
            SetCapture(control);
            SendMessageW(control, LB_SETTOPINDEX, geometry.target(point, state.grab) * state.unitsPerStep, 0);
            wakeListOverlay(control, state);
            return 0;
        }
    }
    if (message == WM_LBUTTONUP && state.dragging) {
        state.dragging = false;
        if (state.ownsCapture && GetCapture() == control) ReleaseCapture();
        wakeListOverlay(control, state);
        return 0;
    }
    if ((message == WM_CAPTURECHANGED || message == WM_CANCELMODE) && state.dragging) {
        state.dragging = false;
        wakeListOverlay(control, state);
    }
    const bool scroll = message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL || message == WM_KEYDOWN ||
        message == LB_SETTOPINDEX || message == LB_SETCURSEL || message == WM_VSCROLL || message == WM_HSCROLL;
    const LRESULT before = scroll ? SendMessageW(control, LB_GETTOPINDEX, 0, 0) : 0;
    LRESULT result = DefSubclassProc(control, message, wparam, lparam);
    if (scroll && before != SendMessageW(control, LB_GETTOPINDEX, 0, 0)) wakeListOverlay(control, state);
    return result;
}
bool installListOverlay(HWND control, bool horizontal) {
    auto* state = static_cast<ListOverlay*>(HeapAlloc(GetProcessHeap(), 0, sizeof(ListOverlay)));
    if (!state) return false;
    *state = {};
    state->horizontal = horizontal;
    if (!SetWindowSubclass(control, listOverlayInput, overlaySubclass, reinterpret_cast<DWORD_PTR>(state))) { HeapFree(GetProcessHeap(), 0, state); return false; }
    SetWindowLongPtrW(control, GWL_STYLE, GetWindowLongPtrW(control, GWL_STYLE) & ~(WS_VSCROLL | WS_HSCROLL));
    SetWindowPos(control, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    return true;
}
LRESULT CALLBACK dialogControlInput(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PAINT) {
        PAINTSTRUCT paint;
        HDC dc = BeginPaint(control, &paint);
        if (dc) {
            RECT bounds; GetClientRect(control, &bounds);
            DialogPaintBuffer buffer(dc, paint.rcPaint);
            FillRect(buffer.dc, &paint.rcPaint, host.background);
            DefSubclassProc(control, WM_PRINTCLIENT, reinterpret_cast<WPARAM>(buffer.dc), PRF_CLIENT);
        }
        EndPaint(control, &paint);
        return 0;
    }
    if (message == WM_KEYDOWN && GetDlgCtrlID(control) == listId && navigateCommandList(wparam)) return 0;
    // Owner-drawn controls have no hover state; preserve native dragging, not hot tracking.
    if ((message == WM_MOUSEMOVE && !(wparam & MK_LBUTTON)) || message == WM_MOUSELEAVE) return 0;
    if (message == WM_LBUTTONUP && GetDlgCtrlID(control) == listId && (host.model.kind == 2 || host.model.kind == 5)) {
        LRESULT hit = SendMessageW(control, LB_ITEMFROMPOINT, 0, lparam);
        RECT item;
        POINT point = { (short)LOWORD(lparam), (short)HIWORD(lparam) };
        if (!HIWORD(hit) && LOWORD(hit) < host.model.count &&
            SendMessageW(control, LB_GETITEMRECT, LOWORD(hit), reinterpret_cast<LPARAM>(&item)) != LB_ERR && PtInRect(&item, point)) {
            SendMessageW(control, LB_SETCURSEL, LOWORD(hit), 0);
            acceptDialog();
            return 0;
        }
    }
    return DefSubclassProc(control, message, wparam, lparam);
}
LRESULT CALLBACK themedCombo(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    if ((message == WM_MOUSEMOVE && !(wparam & MK_LBUTTON)) || message == WM_MOUSELEAVE) return 0;
    if (message == WM_PAINT || message == WM_PRINTCLIENT) {
        PAINTSTRUCT paint = {};
        HDC dc = message == WM_PAINT ? BeginPaint(control, &paint) : reinterpret_cast<HDC>(wparam);
        if (dc) {
            const int saved = SaveDC(dc);
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
            if (saved) RestoreDC(dc, saved);
        }
        if (message == WM_PAINT) EndPaint(control, &paint);
        return 0;
    }
    LRESULT result = DefSubclassProc(control, message, wparam, lparam);
    if (message == WM_SETFOCUS || message == WM_KILLFOCUS || message == CB_SETCURSEL) InvalidateRect(control, nullptr, FALSE);
    return result;
}
LRESULT CALLBACK selectableText(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR) {
    // Classic Win32 edits insert DEL for Ctrl+Backspace instead of deleting a word.
    if (GetDlgCtrlID(control) == queryId && message == WM_CHAR && wparam == 127) {
        DWORD start = 0, end = 0;
        SendMessageW(control, EM_GETSEL, reinterpret_cast<WPARAM>(&start), reinterpret_cast<LPARAM>(&end));
        if (start == end && start > 0) {
            wchar_t text[129];
            const int length = GetWindowTextW(control, text, 129);
            if (start > (DWORD)length) start = (DWORD)length;
            while (start > 0 && iswspace(text[start - 1])) --start;
            while (start > 0 && !iswspace(text[start - 1])) --start;
        }
        if (start != end) {
            SendMessageW(control, EM_SETSEL, start, end);
            SendMessageW(control, EM_REPLACESEL, TRUE, reinterpret_cast<LPARAM>(L""));
        }
        return 0;
    }
    if (message == WM_KEYDOWN && GetDlgCtrlID(control) == queryId && navigateCommandList(wparam)) return 0;
    if (message == WM_KEYDOWN && wparam == 'A' && GetKeyState(VK_CONTROL) < 0) {
        SendMessageW(control, EM_SETSEL, 0, -1);
        return 0;
    }
    if (message == WM_PAINT && GetDlgCtrlID(control) == queryId) {
        // Paint text and cue in one pass; drawing over a visible XOR caret leaves artifacts.
        const bool focused = GetFocus() == control;
        if (focused) HideCaret(control);
        PAINTSTRUCT paint;
        HDC dc = BeginPaint(control, &paint);
        if (dc) {
            DialogPaintBuffer buffer(dc, paint.rcPaint);
            DefSubclassProc(control, WM_PRINTCLIENT, reinterpret_cast<WPARAM>(buffer.dc), PRF_CLIENT);
            if (!GetWindowTextLengthW(control)) {
                RECT bounds; SendMessageW(control, EM_GETRECT, 0, reinterpret_cast<LPARAM>(&bounds));
                bounds.left += px(3);
                SelectObject(buffer.dc, host.font); SetBkMode(buffer.dc, TRANSPARENT);
                SetTextColor(buffer.dc, color(host.model.palette.text_muted));
                DrawTextW(buffer.dc, L"Search commands...", -1, &bounds, DT_SINGLELINE | DT_NOPREFIX);
            }
        }
        EndPaint(control, &paint);
        if (focused) ShowCaret(control);
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
    if ((wcscmp(klass, L"LISTBOX") == 0 || wcscmp(klass, L"BUTTON") == 0) && !SetWindowSubclass(result, dialogControlInput, 1, 0)) { DestroyWindow(result); return nullptr; }
    if (wcscmp(klass, L"LISTBOX") == 0 || wcscmp(klass, L"COMBOBOX") == 0)
        SetWindowTheme(result, host.model.palette.is_dark ? L"DarkMode_Explorer" : L"Explorer", nullptr);
    if (wcscmp(klass, L"LISTBOX") == 0 && !installListOverlay(result, (style & LBS_MULTICOLUMN) != 0)) { DestroyWindow(result); return nullptr; }
    if (wcscmp(klass, L"COMBOBOX") == 0) {
        COMBOBOXINFO info = {}; info.cbSize = sizeof(info);
        if (!GetComboBoxInfo(result, &info) || !installListOverlay(info.hwndList, false)) { DestroyWindow(result); return nullptr; }
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
int CALLBACK enumerateFont(const LOGFONTW* font, const TEXTMETRICW* metrics, DWORD, LPARAM context) {
    if ((metrics->tmPitchAndFamily & TMPF_FIXED_PITCH) || font->lfFaceName[0] == L'@') return 1;
    HWND combo = reinterpret_cast<HWND>(context);
    if (SendMessageW(combo, CB_FINDSTRINGEXACT, -1, reinterpret_cast<LPARAM>(font->lfFaceName)) == CB_ERR)
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(font->lfFaceName));
    return 1;
}
void fillFonts(HWND combo) {
    LOGFONTW query = {}; query.lfCharSet = DEFAULT_CHARSET;
    HDC dc = GetDC(host.window);
    if (dc) { EnumFontFamiliesExW(dc, &query, enumerateFont, reinterpret_cast<LPARAM>(combo), 0); ReleaseDC(host.window, dc); }
    wchar_t selected[128] = {};
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(host.draft.font_family), -1, selected, 128);
    LRESULT row = SendMessageW(combo, CB_FINDSTRINGEXACT, -1, reinterpret_cast<LPARAM>(selected));
    if (row == CB_ERR) row = SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(selected));
    SendMessageW(combo, CB_SETCURSEL, row, 0);
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
    if (host.model.kind == 2) SetWindowPos(list, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW | (host.model.count ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
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
void applySettingsFontSize(bool restoreInvalid) {
    BOOL valid = FALSE;
    const UINT size = GetDlgItemInt(host.window, sizeId, &valid, FALSE);
    if (!valid || size < 9 || size > 32) {
        if (restoreInvalid) {
            wchar_t text[8]; swprintf(text, 8, L"%u", host.draft.font_size);
            host.synchronizing = true;
            SetDlgItemTextW(host.window, sizeId, text);
            host.synchronizing = false;
        }
        if (host.page == SettingsPage::terminal) SetDlgItemTextW(host.window, pageDetailId, L"Enter a font size between 9 and 32.");
        return;
    }
    if (host.page == SettingsPage::terminal) SetDlgItemTextW(host.window, pageDetailId, L"Font size and cursor behavior.");
    if (size != host.draft.font_size) {
        host.draft.font_size = size;
        notify(4, 0, nullptr, 0, host.draft);
    }
}
void acceptDialog() {
    if (host.model.kind == 4) applySettingsFontSize(false);
    else if (host.model.kind == 6) finish(1);
    else {
        uint32_t value;
        if (listSelection(value)) finish(1, value);
    }
}
void queryChanged() {
    wchar_t wide[129], normalized[129];
    const int count = GetDlgItemTextW(host.window, queryId, wide, 129);
    DWORD selectionStart = 0, selectionEnd = 0, normalizedStart = 0, normalizedEnd = 0;
    SendDlgItemMessageW(host.window, queryId, EM_GETSEL, reinterpret_cast<WPARAM>(&selectionStart), reinterpret_cast<LPARAM>(&selectionEnd));
    int normalizedUnits = 0;
    for (int i = 0; i < count; ++i) {
        const wchar_t ch = wide[i];
        if (ch == L'\r' || ch == L'\n' || ch == L'\t') {
            if (ch != L'\n' || i == 0 || wide[i - 1] != L'\r') normalized[normalizedUnits++] = L' ';
        } else if (ch >= 32 && ch != 127) normalized[normalizedUnits++] = ch;
        if ((DWORD)i < selectionStart) normalizedStart = (DWORD)normalizedUnits;
        if ((DWORD)i < selectionEnd) normalizedEnd = (DWORD)normalizedUnits;
    }
    normalized[normalizedUnits] = 0;
    if (wcscmp(wide, normalized) != 0) {
        host.synchronizing = true;
        SetDlgItemTextW(host.window, queryId, normalized);
        SendDlgItemMessageW(host.window, queryId, EM_SETSEL, normalizedStart, normalizedEnd);
        host.synchronizing = false;
        memcpy(wide, normalized, ((size_t)normalizedUnits + 1) * sizeof(wchar_t));
    }
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
void drawListItem(const DRAWITEMSTRUCT& draw) {
    if (draw.itemID == (UINT)-1 || draw.itemID >= host.model.count) return;
    const DialogItem& item = host.model.items[draw.itemID];
    const bool selected = (draw.itemState & ODS_SELECTED) != 0;
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
    RECT row = draw.rcItem; row.top += px(2); row.bottom -= px(2); row.left += px(2); row.right -= px(3);
    COLORREF fill = selected ? selectionColor() : surfaceColor();
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
    COLORREF surface = checked || pressed ? fieldColor() : surfaceColor();
    if (id == blinkId) surface = color(host.model.palette.pane_bg);
    COLORREF border = navigation && checked ? color(host.model.palette.border) : surface;
    if (GetFocus() == draw.hwndItem) border = color(host.model.palette.accent);
    const bool primary = id == IDOK;
    if (primary) {
        surface = host.model.kind == 6 ? RGB(174, 66, 48) : selectionColor();
        if (pressed) surface = blend(surface, color(host.model.palette.text_primary), 0.16f);
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
    if (host.model.kind == 2) return 112.0f + 14.0f * (float)px(listRowHeight()) / host.scale;
    if (host.model.kind == 5) {
        const size_t rows = host.model.count > shellLimit ? shellLimit : host.model.count;
        return 170.0f + (float)px(listRowHeight()) / host.scale * (float)rows;
    }
    return host.model.kind == 6 ? 240.0f : host.model.kind == 4 ? 620.0f : 520.0f;
}
void setPageControlVisibility(int id, bool visible) {
    HWND child = GetDlgItem(host.window, id);
    const bool wasVisible = (GetWindowLongPtrW(child, GWL_STYLE) & WS_VISIBLE) != 0;
    if (wasVisible != visible) SetWindowPos(child, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
}
void showPage(SettingsPage previousPage) {
    const bool general = host.page == SettingsPage::general, appearance = host.page == SettingsPage::appearance, terminal = host.page == SettingsPage::terminal;
    const struct PageControl { int id; bool visible; } controls[] = {
        { shellId, general }, { 122, general }, { 123, general }, { themeGridId, appearance },
        { sizeId, terminal }, { blinkId, terminal }, { 121, terminal }, { 126, terminal }, { fontId, terminal }, { fontLabelId, terminal },
    };
    constexpr UINT visibilityFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW;
    HDWP batch = BeginDeferWindowPos(8);
    for (const auto& entry : controls) {
        HWND child = GetDlgItem(host.window, entry.id);
        if (batch && ((GetWindowLongPtrW(child, GWL_STYLE) & WS_VISIBLE) != 0) != entry.visible)
            batch = DeferWindowPos(batch, child, nullptr, 0, 0, 0, 0, visibilityFlags | (entry.visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    }
    if (!batch || !EndDeferWindowPos(batch)) for (const auto& entry : controls) setPageControlVisibility(entry.id, entry.visible);
    const wchar_t* names[] = { L"General", L"Theme Studio", L"Terminal", L"About" };
    const wchar_t* descriptions[] = { L"Defaults for new terminal sessions.", host.model.palette.is_dark ? L"Dark collection · Click a theme to apply. Changes save automatically." : L"Light collection · Click a theme to apply. Changes save automatically.", L"Font size and cursor behavior.", L"Vex native terminal\r\nWindows + WSL · ConPTY · libghostty" };
    const bool active = host.phase == DialogHost::Phase::active;
    if (active) {
        SendDlgItemMessageW(host.window, pageTitleId, WM_SETREDRAW, FALSE, 0);
        SendDlgItemMessageW(host.window, pageDetailId, WM_SETREDRAW, FALSE, 0);
    }
    SetDlgItemTextW(host.window, pageTitleId, names[(int)host.page]);
    SetDlgItemTextW(host.window, pageDetailId, descriptions[(int)host.page]);
    if (active) {
        SendDlgItemMessageW(host.window, pageTitleId, WM_SETREDRAW, TRUE, 0);
        SendDlgItemMessageW(host.window, pageDetailId, WM_SETREDRAW, TRUE, 0);
        if (previousPage != host.page) {
            InvalidateRect(GetDlgItem(host.window, firstPageId + (int)previousPage), nullptr, FALSE);
            InvalidateRect(GetDlgItem(host.window, firstPageId + (int)host.page), nullptr, FALSE);
        }
        RECT content = { 0, px(host.height < 430 ? 92 : 136), px(host.width), px(host.height - 16) };
        RedrawWindow(host.window, &content, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
}
void paintDialogSurface(HDC dc) {
    RECT bounds; GetClientRect(host.window, &bounds);
    FillRect(dc, &bounds, host.background);
    roundedFill(dc, bounds, surfaceColor(), color(host.model.palette.border), px(24));
    if (host.model.kind == 4 && (host.page == SettingsPage::general || host.page == SettingsPage::terminal)) {
        const bool compact = host.height < 430;
        RECT card = { px(28), px(compact ? 176 : 222), bounds.right - px(28), px((host.page == SettingsPage::terminal ? 422 : 310) - (compact ? 46 : 0)) };
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
        HWND grid = control(L"LISTBOX", L"", WS_TABSTOP | WS_HSCROLL | LBS_MULTICOLUMN | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, themeGridId, 16, 222, width - 32, height - 246);
        ok = grid && ok;
        if (grid) { SendMessageW(grid, LB_SETITEMHEIGHT, 0, px(themeRowHeight())); SendMessageW(grid, LB_SETCOLUMNWIDTH, px(themeColumnWidth()), 0); fillList(themeGridId); }
        ok = control(L"STATIC", L"Font size", 0, 121, 46, 242, 180, 24) && ok;
        ok = control(L"STATIC", L"Terminal text size · 9–32 pt", 0, 126, 46, 268, 300, 24) && ok;
        wchar_t size[8]; swprintf(size, 8, L"%u", host.model.settings.font_size);
        HWND fontSize = control(L"EDIT", size, ES_NUMBER | ES_AUTOHSCROLL | ES_CENTER | WS_TABSTOP, sizeId, width - 126, 248, 64, 26);
        ok = fontSize && ok;
        if (fontSize) SendMessageW(fontSize, EM_SETLIMITTEXT, 2, 0);
        ok = control(L"STATIC", L"Font family", 0, fontLabelId, 46, 312, 180, 24) && ok;
        HWND fonts = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | CBS_SORT | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_TABSTOP | WS_VSCROLL, fontId, width - 340, 308, 288, 240);
        ok = fonts && ok;
        if (fonts) fillFonts(fonts);
        ok = control(L"BUTTON", L"Blink terminal cursor", BS_OWNERDRAW | WS_TABSTOP, blinkId, 34, 368, width - 68, 40) && ok;
        ok = control(L"STATIC", L"Default shell", 0, 122, 46, 242, 160, 24) && ok;
        ok = control(L"STATIC", L"Used for new tabs and splits.", 0, 123, 46, 268, 300, 24) && ok;
        HWND shell = control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_TABSTOP | WS_VSCROLL, shellId, width - 300, 244, 248, 220);
        ok = shell && ok;
        if (shell) fillCombo(shell, host.model.shells, host.model.shellCount, host.model.settings.shell);
        showPage(host.page);
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
                SendMessageW(edit, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, MAKELPARAM(px(2), px(2)));
            }
            top = 62;
        }
        const bool themes = host.model.kind == 1;
        HWND list = control(L"LISTBOX", L"", WS_TABSTOP | (themes ? WS_HSCROLL | LBS_MULTICOLUMN : WS_VSCROLL) | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, listId, commands ? 10 : 16, top, width - (commands ? 20 : 32), height - top - (commands ? 48 : 70));
        ok = list && ok;
        if (list) {
            SendMessageW(list, LB_SETITEMHEIGHT, 0, px(themes ? themeRowHeight() : listRowHeight()));
            if (themes) SendMessageW(list, LB_SETCOLUMNWIDTH, px(themeColumnWidth()), 0);
            fillList();
        }
    }
    if (!commands) {
        if (!closing) ok = control(L"BUTTON", L"×", BS_OWNERDRAW | WS_TABSTOP, closeId, width - 42, 12, 28, 28) && ok;
        if (!settings) {
            ok = control(L"BUTTON", L"Cancel", WS_TABSTOP | BS_OWNERDRAW, IDCANCEL, width - 244, height - 52, 96, 34) && ok;
            ok = control(L"BUTTON", host.model.accept, WS_TABSTOP | BS_OWNERDRAW, IDOK, width - 136, height - 52, 112, 34) && ok;
        }
    }
    if (!settings) SendMessageW(host.window, DM_SETDEFID, host.model.kind == 6 ? IDCANCEL : IDOK, 0);
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
        if (LOWORD(wparam) >= firstPageId && LOWORD(wparam) < firstPageId + 4) {
            const auto page = (SettingsPage)(LOWORD(wparam) - firstPageId);
            if (page != host.page) { const auto previousPage = host.page; host.page = page; showPage(previousPage); }
            return TRUE;
        }
        if (LOWORD(wparam) == blinkId) {
            host.draft.cursor_blink = !host.draft.cursor_blink;
            notify(4, 0, nullptr, 0, host.draft);
            InvalidateRect(GetDlgItem(window, blinkId), nullptr, FALSE);
            return TRUE;
        }
        if (LOWORD(wparam) == sizeId && (HIWORD(wparam) == EN_CHANGE || HIWORD(wparam) == EN_KILLFOCUS)) {
            applySettingsFontSize(HIWORD(wparam) == EN_KILLFOCUS);
            return TRUE;
        }
        if (LOWORD(wparam) == fontId && HIWORD(wparam) == CBN_SELCHANGE) {
            wchar_t family[128] = {};
            GetDlgItemTextW(window, fontId, family, 128);
            uint8_t utf8[128] = {};
            if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, family, -1, reinterpret_cast<char*>(utf8), 128, nullptr, nullptr)) {
                memcpy(host.draft.font_family, utf8, sizeof(utf8));
                notify(4, 0, nullptr, 0, host.draft);
            }
            return TRUE;
        }
        if (LOWORD(wparam) == shellId && HIWORD(wparam) == CBN_SELCHANGE) {
            uint32_t value;
            if (comboSelection(shellId, value) && value != host.draft.shell) {
                host.draft.shell = value;
                notify(4, 0, nullptr, 0, host.draft);
            }
            return TRUE;
        }
        if (LOWORD(wparam) == themeGridId && HIWORD(wparam) == LBN_SELCHANGE) {
            uint32_t value;
            if (listSelection(value, themeGridId) && value != host.draft.theme) {
                host.draft.theme = value;
                notify(4, 0, nullptr, 0, host.draft);
            }
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
    case WM_APP + 2: case WM_APP + 3: {
        wchar_t enabled[2];
        if (GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", enabled, 2) != 1 || enabled[0] != L'1') return FALSE;
        if (message == WM_APP + 2) {
            SetWindowLongPtrW(window, DWLP_MSGRESULT, (LONG_PTR)(wparam == 2 ? host.surfacePaintCount : wparam == 1 ? host.dialogPaintCount : host.themeCardPaintCount));
            return TRUE;
        }
        HWND child = GetDlgItem(window, (int)wparam);
        if (!child || !IsWindowVisible(child)) { SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        // Replay atomically so desktop focus and real pointer messages cannot interleave the measurement.
        UpdateWindow(window); UpdateWindow(child);
        const uint64_t before = host.dialogPaintCount;
        for (int i = 0; i < 20; ++i) {
            SendMessageW(child, WM_MOUSEMOVE, 0, MAKELPARAM(px(12 + i), px(12)));
            UpdateWindow(child); UpdateWindow(window);
        }
        RECT bounds; GetClientRect(child, &bounds);
        SendMessageW(child, WM_MOUSEMOVE, 0, MAKELPARAM(bounds.right / 2, px(12)));
        UpdateWindow(child); UpdateWindow(window);
        SendMessageW(child, WM_MOUSELEAVE, 0, 0);
        UpdateWindow(child); UpdateWindow(window);
        SetWindowLongPtrW(window, DWLP_MSGRESULT, (LONG_PTR)(host.dialogPaintCount - before));
        return TRUE;
    }
    case WM_APP + 4: {
        wchar_t enabled[2];
        if (GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", enabled, 2) != 1 || enabled[0] != L'1') return FALSE;
        RedrawWindow(window, nullptr, nullptr, RDW_UPDATENOW | RDW_ALLCHILDREN);
        SetWindowLongPtrW(window, DWLP_MSGRESULT, 1);
        return TRUE;
    }
    case WM_APP + 5: {
        wchar_t enabled[2];
        if (GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", enabled, 2) != 1 || enabled[0] != L'1') return FALSE;
        HWND child = GetDlgItem(window, (int)wparam);
        if (!child || !IsWindowVisible(child)) { SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        wchar_t klass[32] = {}; GetClassNameW(child, klass, 32);
        if (wcscmp(klass, L"ListBox") != 0 && wcscmp(klass, L"ComboBox") != 0 && wcscmp(klass, L"Button") != 0) { SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        DRAWITEMSTRUCT draw = {}; draw.CtlID = (UINT)wparam; draw.hwndItem = child;
        draw.CtlType = wcscmp(klass, L"ListBox") == 0 ? ODT_LISTBOX : wcscmp(klass, L"ComboBox") == 0 ? ODT_COMBOBOX : ODT_BUTTON;
        draw.itemAction = ODA_DRAWENTIRE; draw.itemState = ODS_SELECTED | ODS_FOCUS;
        if (draw.CtlType == ODT_LISTBOX) SendMessageW(child, LB_GETITEMRECT, 0, reinterpret_cast<LPARAM>(&draw.rcItem));
        else GetClientRect(child, &draw.rcItem);
        HDC reference = GetDC(window);
        HDC dc = reference ? CreateCompatibleDC(reference) : nullptr;
        HBITMAP bitmap = dc ? CreateCompatibleBitmap(reference, draw.rcItem.right, draw.rcItem.bottom) : nullptr;
        if (reference) ReleaseDC(window, reference);
        if (!bitmap) { if (dc) DeleteDC(dc); SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        HGDIOBJ previous = SelectObject(dc, bitmap);
        HGDIOBJ font = GetStockObject(SYSTEM_FONT); SelectObject(dc, font);
        SetBkMode(dc, OPAQUE); SetTextColor(dc, RGB(12, 34, 56)); SetBkColor(dc, RGB(78, 90, 123));
        draw.hDC = dc;
        SendMessageW(window, WM_DRAWITEM, wparam, reinterpret_cast<LPARAM>(&draw));
        const bool restored = GetCurrentObject(dc, OBJ_FONT) == font && GetBkMode(dc) == OPAQUE &&
            GetTextColor(dc) == RGB(12, 34, 56) && GetBkColor(dc) == RGB(78, 90, 123);
        SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc);
        SetWindowLongPtrW(window, DWLP_MSGRESULT, restored ? 1 : 0);
        return TRUE;
    }
    case WM_TIMER:
        if (wparam == 7 && host.revealStarted) {
            const float t = fminf(1.0f, (float)(GetTickCount64() - host.revealStarted) / 120.0f);
            if (t >= 1.0f || !vex_window_animations_enabled()) {
                KillTimer(window, 7);
                host.revealStarted = 0;
                SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA);
                SetWindowLongPtrW(window, GWL_EXSTYLE, GetWindowLongPtrW(window, GWL_EXSTYLE) & ~WS_EX_LAYERED);
            } else {
                const float ease = 1.0f - powf(1.0f - t, 3.0f);
                SetLayeredWindowAttributes(window, 0, (BYTE)lroundf(220.0f + 35.0f * ease), LWA_ALPHA);
            }
            return TRUE;
        }
        break;
    case WM_ERASEBKGND: return TRUE;
    case WM_PAINT: {
        ++host.surfacePaintCount; ++host.dialogPaintCount;
        PAINTSTRUCT paint; HDC dc = BeginPaint(window, &paint);
        if (dc) { DialogPaintBuffer buffer(dc, paint.rcPaint); paintDialogSurface(buffer.dc); }
        EndPaint(window, &paint);
        return TRUE;
    }
    case WM_PRINTCLIENT: if (wparam) {
        HDC dc = reinterpret_cast<HDC>(wparam);
        const int saved = SaveDC(dc);
        paintDialogSurface(dc);
        if (saved) RestoreDC(dc, saved);
        return TRUE;
    } break;
    case DM_GETDEFID:
        SetWindowLongPtrW(window, DWLP_MSGRESULT, host.model.kind == 4 ? 0 : MAKELRESULT(host.model.kind == 6 ? IDCANCEL : IDOK, DC_HASDEFID));
        return TRUE;
    case DM_SETDEFID: SetWindowLongPtrW(window, DWLP_MSGRESULT, TRUE); return TRUE;
    case WM_DRAWITEM:
        if (lparam) {
            ++host.dialogPaintCount;
            const auto& draw = *reinterpret_cast<const DRAWITEMSTRUCT*>(lparam);
            const int saved = SaveDC(draw.hDC);
            if (draw.CtlType == ODT_LISTBOX) drawListItem(draw);
            else if (draw.CtlType == ODT_BUTTON) drawDialogButton(draw);
            else if (draw.CtlType == ODT_COMBOBOX) drawComboItem(draw);
            if (saved) RestoreDC(draw.hDC, saved);
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
        const bool card = host.model.kind == 4 && (id == sizeId || id == shellId || id == fontId || id == fontLabelId || id == 121 || id == 122 || id == 123 || id == 126);
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
        else if (id == IDOK || id == IDCANCEL || id == closeId || id == sizeId || id == shellId || id == fontId) x += dx;
        else if (w > host.baseWidth * 0.5f) w += dx;
        if (id == IDOK || id == IDCANCEL) y += dy;
        if (id == listId || id == themeGridId) h += dy;
        if (id == 123 || id == 126) w = fminf(w, host.width - 350);
        if (compact) {
            if (id >= firstPageId && id < firstPageId + 4) { y = 44; h = 44; }
            if (id == pageTitleId || id == pageDetailId || id == 121 || id == 122 || id == 123 || id == 126 || id == sizeId || id == shellId || id == fontId || id == fontLabelId || id == blinkId) y -= 46;
            if (id == pageDetailId) h = 30;
            if (id == themeGridId) { y = 174; h = host.height - 198; }
        }
        if (id == listId && host.model.kind != 1) {
            const int rowPixels = px(listRowHeight());
            const int rows = px(h) / rowPixels;
            if (rows > 0) h = (float)(rows * rowPixels) / host.scale;
        }
        MoveWindow(c.window, px(x), px(y), px(fmaxf(w, 1)), px(fmaxf(h, 1)), FALSE);
        if (oldFont) SendMessageW(c.window, WM_SETFONT, reinterpret_cast<WPARAM>(id == pageTitleId || (id == titleId && host.model.kind != 4) ? host.heading : host.font), FALSE);
        if (id == listId || id == themeGridId) {
            SendMessageW(c.window, LB_SETITEMHEIGHT, 0, px(host.model.kind == 1 || id == themeGridId ? themeRowHeight() : listRowHeight()));
            if (host.model.kind == 1 || id == themeGridId) SendMessageW(c.window, LB_SETCOLUMNWIDTH, px(themeColumnWidth()), 0);
        }
        if (id == shellId || id == fontId) {
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
extern "C" int vex_dialog_open(VexDialogRequest request) {
    if (!IsWindow(host.owner) || request.kind < 1 || request.kind > 6 || request.count > itemLimit || request.shell_count > shellLimit) return ERROR_INVALID_PARAMETER;
    if (!host.graphicsToken) {
        Gdiplus::GdiplusStartupInput startup;
        if (Gdiplus::GdiplusStartup(&host.graphicsToken, &startup, nullptr) != Gdiplus::Ok) {
            host.graphicsToken = 0;
            return ERROR_NOT_READY;
        }
    }
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
        const auto& oldPalette = host.model.palette;
        const auto& newPalette = next.palette;
        const bool appearanceChanged = oldPalette.is_dark != newPalette.is_dark;
        const bool paletteChanged = oldPalette.pane_bg != newPalette.pane_bg || oldPalette.text_primary != newPalette.text_primary ||
            oldPalette.text_muted != newPalette.text_muted || oldPalette.border != newPalette.border || oldPalette.accent != newPalette.accent || appearanceChanged;
        bool itemsChanged = host.model.count != next.count;
        for (size_t i = 0; !itemsChanged && i < next.count; ++i) {
            const auto& oldItem = host.model.items[i];
            const auto& newItem = next.items[i];
            itemsChanged = oldItem.value != newItem.value || wcscmp(oldItem.label, newItem.label) != 0 || wcscmp(oldItem.detail, newItem.detail) != 0 ||
                oldItem.background != newItem.background || oldItem.foreground != newItem.foreground || oldItem.accent != newItem.accent || memcmp(oldItem.swatches, newItem.swatches, sizeof(oldItem.swatches)) != 0;
        }
        const bool emptyChanged = (host.model.count == 0) != (next.count == 0);
        const bool blinkChanged = host.model.settings.cursor_blink != next.settings.cursor_blink;
        if (itemsChanged || paletteChanged) clearThemeCardCache();
        host.model = next;
        if (host.model.kind == 4) {
            host.draft = next.settings;
            BOOL valid = FALSE;
            const UINT size = GetDlgItemInt(host.window, sizeId, &valid, FALSE);
            if (!valid || size != host.draft.font_size) {
                wchar_t text[8]; swprintf(text, 8, L"%u", host.draft.font_size);
                SetDlgItemTextW(host.window, sizeId, text);
            }
            uint32_t selectedShell;
            if (!comboSelection(shellId, selectedShell) || selectedShell != host.draft.shell)
                fillCombo(GetDlgItem(host.window, shellId), host.model.shells, host.model.shellCount, host.draft.shell);
            if (blinkChanged) InvalidateRect(GetDlgItem(host.window, blinkId), nullptr, FALSE);
        }
        const int listControlId = host.model.kind == 4 ? themeGridId : listId;
        HWND list = GetDlgItem(host.window, listControlId);
        if (paletteChanged) {
            HBRUSH background = CreateSolidBrush(surfaceColor());
            HBRUSH field = CreateSolidBrush(color(host.model.palette.pane_bg));
            if (background && field) { DeleteObject(host.background); DeleteObject(host.field); host.background = background; host.field = field; }
            else { if (background) DeleteObject(background); if (field) DeleteObject(field); }
            if (appearanceChanged && list) SetWindowTheme(list, host.model.palette.is_dark ? L"DarkMode_Explorer" : L"Explorer", nullptr);
        }
        if (itemsChanged || paletteChanged) fillList(listControlId);
        else if (list) {
            uint32_t selected;
            if (!listSelection(selected, listControlId) || selected != host.model.selected) {
                for (size_t i = 0; i < host.model.count; ++i) {
                    if (host.model.items[i].value == host.model.selected) { SendMessageW(list, LB_SETCURSEL, i, 0); break; }
                }
            }
        }
        if (paletteChanged) RedrawWindow(host.window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
        else if (host.model.kind == 2 && emptyChanged) {
            RECT results = { px(10), px(62), px(host.width - 10), px(host.height - 48) };
            InvalidateRect(host.window, &results, FALSE);
        }
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
    // Page/control transitions need one composed surface; command search keeps its cheaper, stable-layout dirty list.
    if (request.kind != 2) layout.dialog.dwExtendedStyle |= WS_EX_COMPOSITED;
    HWND result = CreateDialogIndirectParamW(GetModuleHandleW(nullptr), &layout.dialog, host.owner, dialogProcedure, 0);
    if (!result || !IsWindow(result)) { closeDialog(false); return ERROR_NOT_ENOUGH_MEMORY; }
    host.window = result;
    host.phase = DialogHost::Phase::active;
    repositionTerminalDialogs();
    // Theme thumbnails are cached on first visible paint, never on the opening path.
    if (vex_window_animations_enabled()) {
        const LONG_PTR style = GetWindowLongPtrW(result, GWL_EXSTYLE);
        SetWindowLongPtrW(result, GWL_EXSTYLE, style | WS_EX_LAYERED);
        if (SetLayeredWindowAttributes(result, 0, 220, LWA_ALPHA)) {
            host.revealStarted = GetTickCount64();
            if (!SetTimer(result, 7, 16, nullptr)) {
                host.revealStarted = 0;
                SetWindowLongPtrW(result, GWL_EXSTYLE, style);
            }
        } else SetWindowLongPtrW(result, GWL_EXSTYLE, style);
    }
    EnableWindow(host.owner, FALSE);
    ShowWindow(result, SW_SHOW);
    SetActiveWindow(result);
    HWND focus = GetDlgItem(result, request.kind == 4 ? shellId : request.kind == 6 ? IDCANCEL : request.kind == 2 ? queryId : listId);
    SetFocus(focus);
    RedrawWindow(result, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    return 0;
}
