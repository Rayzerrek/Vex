#include "windows_dialog.h"
#include "ui_renderer.h"
#include "overlay_scrollbar.h"
#include "dialog_motion.h"
#include "chrome_motion.h"
#include "pixel_scroll.h"
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
    HWND owner, window, backdrop, previousFocus;
    ULONG_PTR graphicsToken = 0;
    VexWindowCallbacks callbacks;
    DialogModel model;
    HFONT font, heading, symbols;
    HBRUSH background, field;
    uint64_t themeCardPaintCount = 0, dialogPaintCount = 0, surfacePaintCount = 0;
    enum class Phase { closed, creating, active, closing } phase = Phase::closed;
    bool synchronizing = false;
    DialogMotion motion;
    ChromeMotion cursorBlinkMotion;
    uint32_t pendingEvent = 0, pendingValue = 0;
    bool layered = false, layoutReady = false;
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
int listRowHeight() { return host.model.kind == 2 ? 32 : 58; }
int themeRowHeight() {
    const float available = (host.model.kind == 4 ? host.height - (host.height < 430 ? 198 : 246) : host.height - 170) - 16;
    return (int)fmaxf(104, floorf(available / 2));
}
int themeColumnWidth() { return (int)floorf((host.width - 32) / 3); }
void roundedFill(HDC dc, RECT bounds, COLORREF fill, COLORREF border, int radius);
void acceptDialog();
void drawListItem(const DRAWITEMSTRUCT& draw);
void drawComboItem(const DRAWITEMSTRUCT& draw);
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
    if (host.phase == DialogHost::Phase::closed) return;
    host.phase = DialogHost::Phase::closing;
    HWND dialog = host.window;
    if (IsWindow(dialog)) KillTimer(dialog, 7);
    host.motion = {};
    host.layered = false;
    host.window = nullptr;
    if (IsWindow(host.backdrop)) {
        if (GetCapture() == host.backdrop) ReleaseCapture();
        DestroyWindow(host.backdrop);
    }
    host.backdrop = nullptr;
    // Destroying the active popup while its owner is disabled can activate another app.
    if (IsWindow(host.owner)) EnableWindow(host.owner, TRUE);
    if (IsWindow(dialog)) DestroyWindow(dialog);
    releasePaint();
    host.phase = DialogHost::Phase::closed;
    host.controlCount = 0;
    if (IsWindow(host.owner)) {
        vex_window_invalidate();
        if (restoreFocus) {
            SetActiveWindow(host.owner);
            SetFocus(IsWindow(host.previousFocus) ? host.previousFocus : host.owner);
        }
    }
}
void finish(uint32_t event, uint32_t value = 0) {
    if (host.phase != DialogHost::Phase::active) return;
    // Dispatch commands immediately; a closing animation must never delay an action or the next picker.
    if (event == 0 && host.model.kind != 2 && host.layered && vex_window_animations_enabled()) {
        host.pendingEvent = event; host.pendingValue = value;
        host.phase = DialogHost::Phase::closing;
        host.motion.start(0, GetTickCount64(), true);
        if (SetTimer(host.window, 7, 16, nullptr)) return;
    }
    // Timer failure and reduced motion must have the same completion semantics.
    host.phase = DialogHost::Phase::active;
    closeDialog(true);
    notify(event, value);
}
LRESULT CALLBACK backdropProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    case WM_LBUTTONDOWN:
        SetCapture(window);
        return 0;
    case WM_LBUTTONUP:
        if (GetCapture() == window) {
            // Consume the entire click before restoring input to the terminal underneath.
            ReleaseCapture();
            finish(0);
        }
        return 0;
    case WM_CANCELMODE:
        if (GetCapture() == window) ReleaseCapture();
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}
bool createDialogBackdrop() {
    WNDCLASSW klass = {};
    klass.lpfnWndProc = backdropProcedure;
    klass.hInstance = GetModuleHandleW(nullptr);
    klass.lpszClassName = L"VexDialogBackdrop";
    klass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    klass.hbrBackground = reinterpret_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    if (!RegisterClassW(&klass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
    // The owner stays disabled. A nearly transparent owned surface receives only backdrop clicks;
    // the renderer still draws the dimming and shadow, and the popup remains above this surface.
    host.backdrop = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
        klass.lpszClassName, L"", WS_POPUP, 0, 0, 1, 1, host.owner, nullptr, klass.hInstance, nullptr);
    return host.backdrop && SetLayeredWindowAttributes(host.backdrop, 0, 1, LWA_ALPHA);
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
    RECT viewport; GetClientRect(list, &viewport);
    const int rowHeight = (int)SendMessageW(list, LB_GETITEMHEIGHT, 0, 0);
    const LRESULT pageRows = rowHeight > 0 ? (viewport.bottom / rowHeight > 0 ? viewport.bottom / rowHeight : 1) : 1;
    if (key == VK_UP) selected = selected == 0 ? count - 1 : selected - 1;
    else if (key == VK_DOWN) selected = selected + 1 == count ? 0 : selected + 1;
    else if (key == VK_HOME) selected = 0;
    else if (key == VK_END) selected = count - 1;
    else if (key == VK_PRIOR) selected = selected > pageRows ? selected - pageRows : 0;
    else selected = selected + pageRows < count ? selected + pageRows : count - 1;
    SendMessageW(list, LB_SETCURSEL, selected, 0);
    LRESULT value = SendMessageW(list, LB_GETITEMDATA, selected, 0);
    if (value >= 0 && (uint64_t)value <= UINT32_MAX) notify(2, (uint32_t)value);
    return true;
}
struct ListOverlay {
    OverlayScrollbarVisibility visibility;
    PixelScroll scroll;
    HWND combo = nullptr;
    bool hovered = false, dragging = false, horizontal = false, ownsCapture = false, pointerInput = false, releasingCapture = false;
    float grab = 0;
    int unitsPerStep = 1, pixelsPerStep = 1;
    unsigned nativeUpdateDepth = 0;
    bool redrawEnabled = true, nativeDirty = false;
};
constexpr int listGutter = 16;

void setNativeListRedraw(HWND control, bool enabled) {
    const LONG_PTR visible = GetWindowLongPtrW(control, GWL_STYLE) & WS_VISIBLE;
    DefSubclassProc(control, WM_SETREDRAW, enabled, 0);
    // WM_SETREDRAW changes WS_VISIBLE. Preserve logical visibility for page/layout callbacks.
    const LONG_PTR style = GetWindowLongPtrW(control, GWL_STYLE);
    if ((style & WS_VISIBLE) != visible) SetWindowLongPtrW(control, GWL_STYLE, (style & ~WS_VISIBLE) | visible);
}
void invalidateListContent(HWND control, ListOverlay& state) {
    state.nativeDirty = true;
    if (state.redrawEnabled && !state.nativeUpdateDepth) {
        InvalidateRect(control, nullptr, FALSE);
        state.nativeDirty = false;
    }
}
bool nativeListMutation(UINT message) {
    switch (message) {
    case LB_SETCURSEL: case LB_SETTOPINDEX: case LB_RESETCONTENT: case LB_ADDSTRING:
    case LB_INSERTSTRING: case LB_DELETESTRING: case LB_SETITEMDATA: case LB_SETITEMHEIGHT: case LB_SETCOLUMNWIDTH:
    case WM_KEYDOWN: case WM_CHAR: case WM_SETFOCUS: case WM_KILLFOCUS: case WM_SETFONT:
    case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK: case WM_MOUSEMOVE: case WM_MOUSELEAVE:
    case WM_VSCROLL: case WM_HSCROLL: case WM_TIMER: case WM_SIZE: case WM_WINDOWPOSCHANGED:
    case WM_SHOWWINDOW: case WM_ENABLE: case WM_CAPTURECHANGED: case WM_CANCELMODE: return true;
    default: return false;
    }
}
LRESULT nativeListMessage(HWND control, ListOverlay& state, UINT message, WPARAM wparam, LPARAM lparam) {
    if (!nativeListMutation(message)) return DefSubclassProc(control, message, wparam, lparam);
    const auto identity = reinterpret_cast<HANDLE>(&state);
    const LRESULT selected = DefSubclassProc(control, LB_GETCURSEL, 0, 0);
    const LRESULT first = DefSubclassProc(control, LB_GETTOPINDEX, 0, 0);
    if (state.nativeUpdateDepth++ == 0 && state.redrawEnabled) setNativeListRedraw(control, false);
    // Native LISTBOX still owns selection/keyboard/accessibility, never pixels or ScrollWindow blits.
    const LRESULT result = DefSubclassProc(control, message, wparam, lparam);
    if (GetPropW(control, L"VexListOverlay") != identity) return result;
    const bool pointerOrKey = message == WM_MOUSEMOVE || message == WM_MOUSELEAVE || message == WM_LBUTTONDOWN ||
        message == WM_LBUTTONUP || message == WM_LBUTTONDBLCLK || message == WM_KEYDOWN || message == WM_CHAR ||
        message == LB_SETCURSEL || message == LB_SETTOPINDEX || message == WM_TIMER;
    state.nativeDirty |= !pointerOrKey || selected != DefSubclassProc(control, LB_GETCURSEL, 0, 0) ||
        first != DefSubclassProc(control, LB_GETTOPINDEX, 0, 0);
    if (--state.nativeUpdateDepth == 0 && state.redrawEnabled) {
        setNativeListRedraw(control, true);
        if (state.nativeDirty) invalidateListContent(control, state);
    }
    return result;
}
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
    if (row <= 0 || count <= 0 || bounds.bottom <= 0 || bounds.right <= 0) {
        state.scroll.range(0, GetTickCount64()); return {};
    }
    int extent = count * row, viewport = bounds.bottom;
    state.unitsPerStep = 1; state.pixelsPerStep = row;
    if (state.horizontal) {
        RECT item;
        if (SendMessageW(control, LB_GETITEMRECT, 0, reinterpret_cast<LPARAM>(&item)) == LB_ERR) return {};
        const int width = item.right - item.left;
        if (width <= 0) return {};
        state.unitsPerStep = bounds.bottom / row > 0 ? bounds.bottom / row : 1;
        state.pixelsPerStep = width;
        extent = ((count + state.unitsPerStep - 1) / state.unitsPerStep) * width;
        viewport = bounds.right;
    }
    const auto now = GetTickCount64();
    state.scroll.range((float)(extent - viewport), now);
    auto geometry = OverlayScrollbar::compute(extent, viewport, 0, (float)viewport, (float)px(24));
    if (geometry.maximum) geometry.thumbTop = (geometry.height - geometry.thumbHeight) * state.scroll.sample(now) / geometry.maximum;
    return geometry;
}
bool listOverlayHit(HWND control, const ListOverlay& state, LPARAM point) {
    RECT bounds; GetClientRect(control, &bounds);
    const int x = (short)LOWORD(point), y = (short)HIWORD(point);
    return x >= 0 && y >= 0 && x < bounds.right && y < bounds.bottom &&
        (state.horizontal ? y >= bounds.bottom - px(listGutter) : x >= bounds.right - px(listGutter));
}
void wakeListOverlay(HWND control, ListOverlay& state) {
    state.visibility.reveal(GetTickCount64());
    SetTimer(control, overlayTimer, state.scroll.pending(GetTickCount64()) ? 16 : 900, nullptr);
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
int syncListPosition(HWND control, ListOverlay& state) {
    const int offset = (int)lroundf(state.scroll.sample(GetTickCount64()));
    const int first = (offset / state.pixelsPerStep) * state.unitsPerStep;
    // Native selection/hit testing needs a row index; painting and dragging share a pixel offset.
    if (DefSubclassProc(control, LB_GETTOPINDEX, 0, 0) != first) nativeListMessage(control, state, LB_SETTOPINDEX, first, 0);
    return offset;
}
void paintListContent(HWND control, HDC dc, ListOverlay& state) {
    RECT bounds; GetClientRect(control, &bounds);
    listOverlayGeometry(control, state);
    // LB_SETTOPINDEX can repaint synchronously. Never mutate native scrolling during a paint.
    const int offset = (int)lroundf(state.scroll.sample(GetTickCount64()));
    const int row = (int)SendMessageW(control, LB_GETITEMHEIGHT, 0, 0);
    if (row <= 0) return;
    const int count = (int)SendMessageW(control, LB_GETCOUNT, 0, 0);
    const int selected = (int)SendMessageW(control, LB_GETCURSEL, 0, 0);
    const int first = (offset / state.pixelsPerStep) * state.unitsPerStep;
    const int saved = SaveDC(dc);
    if (state.horizontal) IntersectClipRect(dc, 0, 0, bounds.right, bounds.bottom - px(listGutter));
    else IntersectClipRect(dc, 0, 0, bounds.right - px(listGutter), bounds.bottom);
    for (int i = first; i < count; ++i) {
        DRAWITEMSTRUCT draw = {};
        draw.CtlType = state.combo ? ODT_COMBOBOX : ODT_LISTBOX;
        draw.CtlID = GetDlgCtrlID(state.combo ? state.combo : control);
        draw.hwndItem = state.combo ? state.combo : control;
        draw.itemID = i; draw.itemAction = ODA_DRAWENTIRE;
        draw.itemState = i == selected ? ODS_SELECTED : 0;
        if (GetFocus() == control && i == selected) draw.itemState |= ODS_FOCUS;
        draw.hDC = dc;
        if (state.horizontal) {
            const int x = (i / state.unitsPerStep) * state.pixelsPerStep - offset;
            if (x >= bounds.right) break;
            const int y = (i % state.unitsPerStep) * row;
            draw.rcItem = { x, y, x + state.pixelsPerStep, y + row };
        } else {
            const int y = i * row - offset;
            if (y >= bounds.bottom) break;
            draw.rcItem = { 0, y, bounds.right - px(listGutter), y + row };
        }
        const int savedItem = SaveDC(dc);
        ++host.dialogPaintCount;
        if (state.combo) drawComboItem(draw); else drawListItem(draw);
        if (savedItem) RestoreDC(dc, savedItem);
    }
    if (saved) RestoreDC(dc, saved);
}
LRESULT CALLBACK listOverlayInput(HWND control, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR data) {
    auto& state = *reinterpret_cast<ListOverlay*>(data);
    if (message == WM_NCDESTROY) {
        KillTimer(control, overlayTimer);
        RemoveWindowSubclass(control, listOverlayInput, overlaySubclass);
        RemovePropW(control, L"VexListOverlay");
        HeapFree(GetProcessHeap(), 0, &state);
        return DefSubclassProc(control, message, wparam, lparam);
    }
    if (message == WM_STYLECHANGING && wparam == (WPARAM)GWL_STYLE)
        reinterpret_cast<STYLESTRUCT*>(lparam)->styleNew &= ~(WS_VSCROLL | WS_HSCROLL);
    // LISTBOX can re-enable scroll styles internally when its range changes.
    // Keep the entire window client-owned even when Win32 updates that range.
    if (message == WM_NCCALCSIZE || message == WM_NCPAINT) return 0;
    if (message == WM_NCHITTEST) return HTCLIENT;
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_SETREDRAW) {
        state.redrawEnabled = wparam != 0;
        if (!state.nativeUpdateDepth) {
            setNativeListRedraw(control, state.redrawEnabled);
            if (state.redrawEnabled && state.nativeDirty) invalidateListContent(control, state);
        }
        return 0;
    }
    if (message == WM_PAINT) {
        PAINTSTRUCT paint;
        HDC dc = BeginPaint(control, &paint);
        if (dc) {
            DialogPaintBuffer buffer(dc, paint.rcPaint);
            FillRect(buffer.dc, &paint.rcPaint, host.background);
            paintListContent(control, buffer.dc, state);
            paintListOverlay(control, buffer.dc, state);
        }
        EndPaint(control, &paint);
        return 0;
    }
    if ((message == WM_PRINT || message == WM_PRINTCLIENT) && wparam) {
        HDC dc = reinterpret_cast<HDC>(wparam);
        const int saved = SaveDC(dc);
        RECT bounds; GetClientRect(control, &bounds);
        FillRect(dc, &bounds, host.background);
        paintListContent(control, dc, state); paintListOverlay(control, dc, state);
        if (saved) RestoreDC(dc, saved);
        return 0;
    }
    if (message == WM_TIMER && wparam == overlayTimer) {
        const auto now = GetTickCount64();
        if (!overlayAnimations()) state.scroll.moveTo(state.scroll.destination, now, false);
        const float opacity = state.visibility.opacity(now, state.hovered || state.dragging, overlayAnimations());
        KillTimer(control, overlayTimer);
        if (state.scroll.pending(now) || (opacity > 0 && !state.hovered && !state.dragging)) SetTimer(control, overlayTimer, 16, nullptr);
        InvalidateRect(control, nullptr, FALSE);
        return 0;
    }
    if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL) {
        listOverlayGeometry(control, state);
        UINT lines = 3;
        SystemParametersInfoW(message == WM_MOUSEHWHEEL ? SPI_GETWHEELSCROLLCHARS : SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
        RECT bounds; GetClientRect(control, &bounds);
        const float step = lines == WHEEL_PAGESCROLL ? (float)(state.horizontal ? bounds.right : bounds.bottom) :
            (float)lines * (state.horizontal ? px(32) : state.pixelsPerStep);
        const float delta = (float)(short)HIWORD(wparam) / WHEEL_DELTA;
        state.scroll.moveTo(state.scroll.destination + (message == WM_MOUSEHWHEEL ? delta : -delta) * step, GetTickCount64(), overlayAnimations());
        wakeListOverlay(control, state);
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
            state.scroll.moveTo((float)geometry.target(point, state.grab), GetTickCount64(), false);
            syncListPosition(control, state);
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
            const HWND capture = GetCapture();
            state.ownsCapture = capture != control && (!state.combo || capture != state.combo);
            if (state.ownsCapture) SetCapture(control);
            state.scroll.moveTo((float)geometry.target(point, state.grab), GetTickCount64(), false);
            syncListPosition(control, state);
            wakeListOverlay(control, state);
            return 0;
        }
    }
    if (message == WM_LBUTTONUP && state.dragging) {
        state.dragging = false;
        if (state.ownsCapture && GetCapture() == control) {
            // ComboLBox treats native capture loss as dismissal, even for our own thumb release.
            state.releasingCapture = true;
            ReleaseCapture();
            state.releasingCapture = false;
        }
        state.ownsCapture = false;
        wakeListOverlay(control, state);
        return 0;
    }
    if (message == WM_CAPTURECHANGED && state.releasingCapture) return 0;
    if ((message == WM_CAPTURECHANGED || message == WM_CANCELMODE) && state.dragging) {
        state.dragging = false;
        wakeListOverlay(control, state);
    }
    if (message == LB_GETTOPINDEX) return (int)(state.scroll.sample(GetTickCount64()) / state.pixelsPerStep) * state.unitsPerStep;
    if (message == LB_RESETCONTENT) state.scroll = {};
    if (message == LB_SETTOPINDEX) {
        listOverlayGeometry(control, state);
        state.scroll.moveTo((float)((int)wparam / state.unitsPerStep * state.pixelsPerStep), GetTickCount64(), false);
        syncListPosition(control, state); wakeListOverlay(control, state);
        return 0;
    }
    const bool pointer = message == WM_MOUSEMOVE || message == WM_LBUTTONDOWN || message == WM_LBUTTONUP || message == WM_LBUTTONDBLCLK;
    if (pointer) {
        if (listOverlayHit(control, state, lparam)) return 0;
        listOverlayGeometry(control, state);
        const int remainder = syncListPosition(control, state) % state.pixelsPerStep;
        int x = (short)LOWORD(lparam), y = (short)HIWORD(lparam);
        if (state.horizontal) x += remainder; else y += remainder;
        state.pointerInput = true;
        // Clicking a result can synchronously destroy the control and open another picker.
        const LRESULT result = nativeListMessage(control, state, message, wparam, MAKELPARAM(x, y));
        if (GetPropW(control, L"VexListOverlay") == reinterpret_cast<HANDLE>(data)) state.pointerInput = false;
        return result;
    }
    LRESULT result = nativeListMessage(control, state, message, wparam, lparam);
    if (GetPropW(control, L"VexListOverlay") != reinterpret_cast<HANDLE>(data)) return result;
    const bool showing = message == WM_SHOWWINDOW && wparam;
    if ((message == LB_SETCURSEL || message == WM_KEYDOWN || showing) && !state.pointerInput) {
        const int selected = (int)DefSubclassProc(control, LB_GETCURSEL, 0, 0);
        if (selected >= 0) {
            listOverlayGeometry(control, state);
            RECT bounds; GetClientRect(control, &bounds);
            const float top = (float)(selected / state.unitsPerStep * state.pixelsPerStep);
            state.scroll.reveal(top, top + state.pixelsPerStep, (float)(state.horizontal ? bounds.right : bounds.bottom), GetTickCount64(), !showing && overlayAnimations());
            syncListPosition(control, state); wakeListOverlay(control, state);
        }
    } else if (message == WM_VSCROLL || message == WM_HSCROLL) {
        const int first = (int)DefSubclassProc(control, LB_GETTOPINDEX, 0, 0);
        listOverlayGeometry(control, state);
        state.scroll.moveTo((float)(first / state.unitsPerStep * state.pixelsPerStep), GetTickCount64(), false);
        wakeListOverlay(control, state);
    }
    return result;
}
bool installListOverlay(HWND control, bool horizontal, HWND combo = nullptr) {
    auto* state = static_cast<ListOverlay*>(HeapAlloc(GetProcessHeap(), 0, sizeof(ListOverlay)));
    if (!state) return false;
    *state = {};
    state->horizontal = horizontal; state->combo = combo;
    if (!SetPropW(control, L"VexListOverlay", state)) { HeapFree(GetProcessHeap(), 0, state); return false; }
    if (!SetWindowSubclass(control, listOverlayInput, overlaySubclass, reinterpret_cast<DWORD_PTR>(state))) {
        RemovePropW(control, L"VexListOverlay"); HeapFree(GetProcessHeap(), 0, state); return false;
    }
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
    // Only command and shell picker rows follow hover; keep native dragging and avoid repainting unchanged rows.
    wchar_t selftest[2];
    const bool allowShellHover = host.model.kind == 5 && (GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", selftest, 2) != 1 || selftest[0] != L'1');
    if (message == WM_MOUSEMOVE && !(wparam & MK_LBUTTON) && (host.model.kind == 2 || allowShellHover) && host.phase == DialogHost::Phase::active && GetDlgCtrlID(control) == listId) {
        const LRESULT hit = SendMessageW(control, LB_ITEMFROMPOINT, 0, lparam);
        RECT item;
        const POINT point = { (short)LOWORD(lparam), (short)HIWORD(lparam) };
        if (!HIWORD(hit) && LOWORD(hit) < host.model.count && SendMessageW(control, LB_GETITEMRECT, LOWORD(hit), reinterpret_cast<LPARAM>(&item)) != LB_ERR &&
            PtInRect(&item, point) && SendMessageW(control, LB_GETCURSEL, 0, 0) != LOWORD(hit)) {
            SendMessageW(control, LB_SETCURSEL, LOWORD(hit), 0);
            notify(2, host.model.items[LOWORD(hit)].value);
        }
        return 0;
    }
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
    if (message == WM_MOUSEMOVE || message == WM_LBUTTONUP) {
        COMBOBOXINFO info = {}; info.cbSize = sizeof(info);
        if (GetComboBoxInfo(control, &info)) {
            auto* state = static_cast<ListOverlay*>(GetPropW(info.hwndList, L"VexListOverlay"));
            if (state && state->dragging) {
                // Native combo popups can capture through the combo HWND rather than ComboLBox.
                POINT point = { (short)LOWORD(lparam), (short)HIWORD(lparam) };
                MapWindowPoints(control, info.hwndList, &point, 1);
                return SendMessageW(info.hwndList, message, wparam, MAKELPARAM(point.x, point.y));
            }
        }
    }
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
    if (message == WM_KEYDOWN || message == CB_SETCURSEL || (message == CB_SHOWDROPDOWN && wparam)) {
        COMBOBOXINFO info = {}; info.cbSize = sizeof(info);
        if (GetComboBoxInfo(control, &info)) {
            const LRESULT selected = SendMessageW(control, CB_GETCURSEL, 0, 0);
            if (selected >= 0) SendMessageW(info.hwndList, LB_SETCURSEL, selected, 0);
        }
    }
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
    const LRESULT result = DefSubclassProc(control, message, wparam, lparam);
    if (GetDlgCtrlID(control) == sizeId && (message == WM_SETFOCUS || message == WM_KILLFOCUS)) {
        RECT bounds; GetWindowRect(control, &bounds);
        MapWindowPoints(nullptr, host.window, reinterpret_cast<POINT*>(&bounds), 2);
        InflateRect(&bounds, px(8), px(6));
        InvalidateRect(host.window, &bounds, FALSE);
    }
    return result;
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
        if (!GetComboBoxInfo(result, &info) || !installListOverlay(info.hwndList, false, result)) { DestroyWindow(result); return nullptr; }
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
        const auto now = GetTickCount64();
        float position = host.cursorBlinkMotion.update(checked ? 1.0f : 0.0f, now, vex_window_animations_enabled());
        if (host.cursorBlinkMotion.pending(now) && !SetTimer(host.window, 8, 16, nullptr))
            position = host.cursorBlinkMotion.update(checked ? 1.0f : 0.0f, now, false);
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
float dialogWidth() { return host.model.kind == 4 ? 760.0f : host.model.kind == 1 ? 580.0f : host.model.kind == 2 ? 560.0f : host.model.kind == 5 ? 472.0f : host.model.kind == 6 ? 460.0f : 480.0f; }
float dialogHeight() {
    if (host.model.kind == 2) return 112.0f + 12.0f * (float)px(listRowHeight()) / host.scale;
    if (host.model.kind == 5) {
        const size_t rows = host.model.count > shellLimit ? shellLimit : host.model.count;
        return 120.0f + (float)px(listRowHeight()) / host.scale * (float)rows;
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
        if (host.page == SettingsPage::terminal) {
            HWND edit = GetDlgItem(host.window, sizeId);
            RECT field; GetWindowRect(edit, &field);
            MapWindowPoints(nullptr, host.window, reinterpret_cast<POINT*>(&field), 2);
            InflateRect(&field, px(8), px(6));
            roundedFill(dc, field, fieldColor(), color(GetFocus() == edit ? host.model.palette.accent : host.model.palette.border), px(12));
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
    if (host.model.kind == 6) {
        RECT icon = { px(24), px(26), px(66), px(68) };
        COLORREF tint = blend(surfaceColor(), RGB(194, 101, 71), 0.16f);
        roundedFill(dc, icon, tint, tint, px(24));
        drawSymbol(dc, L"\xE7BA", icon, host.model.palette.is_dark ? RGB(229, 164, 124) : RGB(155, 75, 40));
    }
}
bool createControls(float width, float height) {
    const bool settings = host.model.kind == 4, commands = host.model.kind == 2, shells = host.model.kind == 5, closing = host.model.kind == 6;
    bool ok = true;
    if (!commands && !shells) ok = control(L"STATIC", host.model.title, SS_NOPREFIX | (settings ? SS_CENTER : SS_LEFT), titleId, settings ? 120 : closing ? 82 : 24, settings ? 18 : closing ? 28 : 22, settings ? width - 240 : closing ? width - 110 : width - 80, 28) != nullptr;
    if (settings) {
        const wchar_t* pages[] = { L"General", L"Appearance", L"Terminal", L"About" };
        for (int i = 0; i < 4; ++i) ok = control(L"BUTTON", pages[i], BS_OWNERDRAW | WS_TABSTOP, firstPageId + i, (width - 448) / 2 + i * 112.0f, 56, 108, 68) && ok;
        ok = control(L"STATIC", L"General", SS_NOPREFIX, pageTitleId, 32, 146, width - 64, 28) && ok;
        ok = control(L"STATIC", L"Defaults for new terminal sessions.", SS_NOPREFIX, pageDetailId, 32, 181, width - 64, 32) && ok;
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
        if (!commands && !shells) ok = control(L"STATIC", host.model.description, SS_NOPREFIX, descriptionId, closing ? 82 : 24, closing ? 68 : 58, closing ? width - 110 : width - 48, closing ? height - 144 : 38) && ok;
    }
    if (!settings && host.model.kind != 6) {
        float top = shells ? 70.0f : 100.0f;
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
        HWND list = control(L"LISTBOX", L"", WS_TABSTOP | (themes ? WS_HSCROLL | LBS_MULTICOLUMN : WS_VSCROLL) | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, listId, (commands || shells) ? 12.0f : 16.0f, top, width - ((commands || shells) ? 24.0f : 32.0f), height - top - ((commands || shells) ? 50.0f : 70.0f));
        ok = list && ok;
        if (list) {
            SendMessageW(list, LB_SETITEMHEIGHT, 0, px(themes ? themeRowHeight() : listRowHeight()));
            if (themes) SendMessageW(list, LB_SETCOLUMNWIDTH, px(themeColumnWidth()), 0);
            fillList();
        }
    }
    if (!commands) {
        if (!closing) ok = control(L"BUTTON", L"Close", BS_OWNERDRAW | WS_TABSTOP, closeId, width - 42, shells ? 16.0f : 12.0f, 28, 28) && ok;
        if (!settings && !shells) {
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
            const wchar_t* modeDescriptions[] = {
                L"",
                L"Split the active pane to the right.",
                L"Split the active pane downward.",
                L"Choose the default shell for new sessions.",
            };
            const wchar_t* modeAccept[] = { L"Open tab", L"Split right", L"Split down", L"Set default" };
            SetDlgItemTextW(window, descriptionId, modeDescriptions[host.model.mode]);
            SetDlgItemTextW(window, IDOK, modeAccept[host.model.mode]);
            for (int i = 0; i < 4; ++i) InvalidateRect(GetDlgItem(window, firstModeId + i), nullptr, FALSE);
            InvalidateRect(GetDlgItem(window, descriptionId), nullptr, FALSE);
            InvalidateRect(GetDlgItem(window, IDOK), nullptr, FALSE);
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
    case WM_APP + 7: {
        wchar_t enabled[2];
        if (GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", enabled, 2) != 1 || enabled[0] != L'1') return FALSE;
        HWND list = GetDlgItem(window, (int)wparam);
        auto* state = static_cast<ListOverlay*>(GetPropW(list, L"VexListOverlay"));
        if (!state || state->horizontal) { SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        UpdateWindow(window); UpdateWindow(list);
        RECT bounds; GetClientRect(list, &bounds);
        const int row = (int)SendMessageW(list, LB_GETITEMHEIGHT, 0, 0);
        const int rows = row > 0 ? (int)fminf(64, (float)(bounds.bottom / row)) : 0;
        HDC dc = rows > 0 ? GetDC(list) : nullptr;
        if (!dc) { SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        COLORREF baseline[64];
        const int x = bounds.right - px(12);
        for (int i = 0; i < rows; ++i) {
            baseline[i] = GetPixel(dc, x, i * row + row / 2);
            if (baseline[i] == CLR_INVALID) {
                ReleaseDC(list, dc); SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE;
            }
        }
        int changed = 0;
        // Observe immediately after native selection, without WM_PAINT/PrintWindow masking the defect.
        for (int i = 0; i < rows; ++i) {
            SendMessageW(list, WM_MOUSEMOVE, 0, MAKELPARAM(px(20), i * row + row / 2));
            for (int j = 0; j < rows; ++j) if (GetPixel(dc, x, j * row + row / 2) != baseline[j]) ++changed;
        }
        ReleaseDC(list, dc);
        SetWindowLongPtrW(window, DWLP_MSGRESULT, changed);
        return TRUE;
    }
    case WM_APP + 6: {
        wchar_t enabled[2];
        if (GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", enabled, 2) != 1 || enabled[0] != L'1') return FALSE;
        HWND control = GetDlgItem(window, (int)wparam);
        auto* state = static_cast<ListOverlay*>(GetPropW(control, L"VexListOverlay"));
        if (!state) {
            COMBOBOXINFO info = {}; info.cbSize = sizeof(info);
            if (GetComboBoxInfo(control, &info)) {
                control = info.hwndList;
                state = static_cast<ListOverlay*>(GetPropW(control, L"VexListOverlay"));
            }
        }
        if (!state) { SetWindowLongPtrW(window, DWLP_MSGRESULT, -1); return TRUE; }
        const auto geometry = listOverlayGeometry(control, *state);
        const auto now = GetTickCount64();
        const float value = lparam == 1 ? state->scroll.destination : lparam == 2 ? (float)state->scroll.pending(now) :
            lparam == 3 ? geometry.thumbTop : lparam == 4 ? (float)px(listGutter) : lparam == 5 ? state->scroll.maximum : state->scroll.sample(now);
        SetWindowLongPtrW(window, DWLP_MSGRESULT, (LONG_PTR)lroundf(value * 1000));
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
        if (wparam == 8) {
            const auto now = GetTickCount64();
            host.cursorBlinkMotion.update(host.draft.cursor_blink ? 1.0f : 0.0f, now, vex_window_animations_enabled());
            if (!host.cursorBlinkMotion.pending(now) || host.page != SettingsPage::terminal) KillTimer(window, 8);
            InvalidateRect(GetDlgItem(window, blinkId), nullptr, FALSE);
            return TRUE;
        }
        if (wparam == 7 && host.layered) {
            const auto now = GetTickCount64();
            const bool done = !host.motion.pending(now) || !vex_window_animations_enabled();
            if (done) KillTimer(window, 7);
            if (done && host.phase == DialogHost::Phase::closing) {
                const auto event = host.pendingEvent, value = host.pendingValue;
                host.phase = DialogHost::Phase::active;
                closeDialog(true);
                notify(event, value);
            } else {
                SetLayeredWindowAttributes(window, 0, done ? 255 : (BYTE)lroundf(255 * host.motion.sample(now)), LWA_ALPHA);
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
            const auto& draw = *reinterpret_cast<const DRAWITEMSTRUCT*>(lparam);
            HWND list = draw.CtlType == ODT_LISTBOX ? draw.hwndItem : nullptr;
            if (draw.CtlType == ODT_COMBOBOX) {
                COMBOBOXINFO info = {}; info.cbSize = sizeof(info);
                if (GetComboBoxInfo(draw.hwndItem, &info)) list = info.hwndList;
            }
            auto* state = list ? static_cast<ListOverlay*>(GetPropW(list, L"VexListOverlay")) : nullptr;
            if (state) {
                // Native selection paints use full-width, whole-row rectangles. Only our viewport may draw lists.
                invalidateListContent(list, *state);
                if (draw.CtlType == ODT_COMBOBOX) InvalidateRect(draw.hwndItem, nullptr, FALSE);
                return TRUE;
            }
            ++host.dialogPaintCount;
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
        if (id == queryId || id == sizeId) {
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
    const float dimAlpha = host.model.kind == 2
        ? (host.model.palette.is_dark ? 0.18f : 0.08f)
        : (host.model.palette.is_dark ? 0.32f : 0.16f);
    brush.SetColor(D2D1::ColorF(0x000000, dimAlpha));
    target.FillRectangle(D2D1::RectF(0, 0, size.width, size.height), &brush);
    RECT bounds; if (!GetWindowRect(host.window, &bounds)) return;
    POINT origin = { bounds.left, bounds.top }; if (!ScreenToClient(host.owner, &origin)) return;
    float x = origin.x / scale, y = origin.y / scale, w = (bounds.right - bounds.left) / scale, h = (bounds.bottom - bounds.top) / scale;
    for (int layer = 5; layer > 0; --layer) {
        float spread = layer * 5.0f;
        brush.SetColor(D2D1::ColorF(0x000000, 0.065f));
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
    const int top = (host.model.kind == 2 || host.model.kind == 5) ? (int)fminf((float)px(64), (owner.bottom - px(host.height)) / 2.0f) : (owner.bottom - px(host.height)) / 2;
    const int left = origin.x + (owner.right - px(host.width)) / 2;
    RECT backdropBounds;
    if (IsWindow(host.backdrop) && (!GetWindowRect(host.backdrop, &backdropBounds) ||
        backdropBounds.left != origin.x || backdropBounds.top != origin.y ||
        backdropBounds.right - backdropBounds.left != owner.right || backdropBounds.bottom - backdropBounds.top != owner.bottom))
        SetWindowPos(host.backdrop, nullptr, origin.x, origin.y, owner.right, owner.bottom, SWP_NOACTIVATE | SWP_NOZORDER);
    RECT current;
    // Activation/z-order changes also send WM_WINDOWPOSCHANGED to the owner.
    // They must not relayout and repaint every child of an otherwise stationary popup.
    if (host.layoutReady && !dpiChanged && GetWindowRect(host.window, &current) &&
        current.left == left && current.top == origin.y + top &&
        current.right - current.left == px(host.width) && current.bottom - current.top == px(host.height)) return;
    HFONT oldFont = nullptr, oldHeading = nullptr, oldSymbols = nullptr;
    if (dpiChanged) {
        HFONT font = CreateFontW(-px(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT heading = CreateFontW(-px(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT symbols = CreateFontW(-px(22), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe MDL2 Assets");
        if (font && heading && symbols) { oldFont = host.font; oldHeading = host.heading; oldSymbols = host.symbols; host.font = font; host.heading = heading; host.symbols = symbols; }
        else { if (font) DeleteObject(font); if (heading) DeleteObject(heading); if (symbols) DeleteObject(symbols); }
    }
    SetWindowPos(host.window, HWND_TOP, left, origin.y + top, px(host.width), px(host.height), SWP_NOACTIVATE);
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
    host.layoutReady = true;
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
    if (!IsWindow(host.owner) || request.kind < 1 || request.kind == 3 || request.kind > 6 || request.count > itemLimit || request.shell_count > shellLimit) return ERROR_INVALID_PARAMETER;
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
    host.cursorBlinkMotion = {};
    host.cursorBlinkMotion.update(host.draft.cursor_blink ? 1.0f : 0.0f, GetTickCount64(), false);
    host.page = SettingsPage::general;
    host.layoutReady = false;
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
    if (!createDialogBackdrop()) { closeDialog(true); return ERROR_NOT_ENOUGH_MEMORY; }
    host.phase = DialogHost::Phase::active;
    repositionTerminalDialogs();
    // Theme thumbnails are cached on first visible paint, never on the opening path.
    if (vex_window_animations_enabled()) {
        const LONG_PTR style = GetWindowLongPtrW(result, GWL_EXSTYLE);
        SetWindowLongPtrW(result, GWL_EXSTYLE, style | WS_EX_LAYERED);
        if (SetLayeredWindowAttributes(result, 0, 220, LWA_ALPHA)) {
            // Keep the compositor path stable at full opacity; toggling it repaints child HWNDs.
            host.layered = true;
            host.motion = {};
            host.motion.start(1, GetTickCount64(), false);
            if (!SetTimer(result, 7, 16, nullptr)) SetLayeredWindowAttributes(result, 0, 255, LWA_ALPHA);
        } else SetWindowLongPtrW(result, GWL_EXSTYLE, style);
    }
    EnableWindow(host.owner, FALSE);
    // The backdrop is part of the owner, not the popup's GDI surface.
    vex_window_invalidate();
    SetWindowPos(host.backdrop, result, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    ShowWindow(result, SW_SHOW);
    SetActiveWindow(result);
    HWND focus = GetDlgItem(result, request.kind == 4 ? shellId : request.kind == 6 ? IDCANCEL : request.kind == 2 ? queryId : listId);
    SetFocus(focus);
    RedrawWindow(result, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    return 0;
}
