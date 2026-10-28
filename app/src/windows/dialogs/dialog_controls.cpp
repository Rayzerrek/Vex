#include "dialog_internal.h"
#include "overlay_scrollbar.h"
#include "pixel_scroll.h"
#include <gdiplus.h>
#include <uxtheme.h>
#include <math.h>
#include <stdio.h>
#include <wctype.h>

namespace vex::dialog {
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
    const bool button = (GetWindowLongPtrW(control, GWL_STYLE) & BS_TYPEMASK) == BS_OWNERDRAW;
    // Owner-drawn buttons do not advertise a default-pushbutton style to IsDialogMessage.
    // Claim Enter so keyboard navigation activates the focused button, like Space does.
    if (button && message == WM_GETDLGCODE && lparam) {
        const auto& key = *reinterpret_cast<const MSG*>(lparam);
        if ((key.message == WM_KEYDOWN || key.message == WM_CHAR) && key.wParam == VK_RETURN)
            return DefSubclassProc(control, message, wparam, lparam) | DLGC_WANTMESSAGE;
    }
    if (button && wparam == VK_RETURN) {
        if (message == WM_KEYDOWN) { SendMessageW(control, BM_CLICK, 0, 0); return 0; }
        if (message == WM_CHAR) return 0;
    }
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
    const int id = GetDlgCtrlID(control);
    if ((id == queryId || id == shellNameId || id == shellProgramId || id == shellArgumentsId) && message == WM_CHAR && wparam == 127) {
        DWORD start = 0, end = 0;
        SendMessageW(control, EM_GETSEL, reinterpret_cast<WPARAM>(&start), reinterpret_cast<LPARAM>(&end));
        if (start == end && start > 0) {
            wchar_t text[32768];
            const int length = GetWindowTextW(control, text, 32768);
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
    // Home/End (including Shift selection) belong to the search caret, not the results.
    if (message == WM_KEYDOWN && id == queryId && wparam != VK_HOME && wparam != VK_END && navigateCommandList(wparam)) return 0;
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
    if ((id == sizeId || id == shellNameId || id == shellProgramId || id == shellArgumentsId) && (message == WM_SETFOCUS || message == WM_KILLFOCUS)) {
        RECT bounds; GetWindowRect(control, &bounds);
        MapWindowPoints(nullptr, host.window, reinterpret_cast<POINT*>(&bounds), 2);
        InflateRect(&bounds, px(8), px(6));
        InvalidateRect(host.window, &bounds, FALSE);
    }
    return result;
}
HFONT controlFont(int id) { return id == titleId ? host.heading : host.font; }
HWND control(const wchar_t* klass, const wchar_t* text, DWORD style, int id, float x, float y, float width, float height) {
    if (host.controlCount >= 40) return nullptr;
    HWND result = CreateWindowExW(0, klass, text, WS_CHILD | WS_VISIBLE | style, px(x), px(y), px(width), px(height), host.window, reinterpret_cast<HMENU>((INT_PTR)id), GetModuleHandleW(nullptr), nullptr);
    if (!result) return nullptr;
    SendMessageW(result, WM_SETFONT, reinterpret_cast<WPARAM>(controlFont(id)), FALSE);
    const bool edit = wcscmp(klass, L"EDIT") == 0, list = wcscmp(klass, L"LISTBOX") == 0;
    const bool button = wcscmp(klass, L"BUTTON") == 0, combo = wcscmp(klass, L"COMBOBOX") == 0;
    if (edit && !SetWindowSubclass(result, selectableText, 1, 0)) { DestroyWindow(result); return nullptr; }
    if ((list || button) && !SetWindowSubclass(result, dialogControlInput, 1, 0)) { DestroyWindow(result); return nullptr; }
    if (list || combo) SetWindowTheme(result, host.model.palette.is_dark ? L"DarkMode_Explorer" : L"Explorer", nullptr);
    if (list && !installListOverlay(result, (style & LBS_MULTICOLUMN) != 0)) { DestroyWindow(result); return nullptr; }
    if (combo) {
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
    SendMessageW(combo, WM_SETREDRAW, FALSE, 0);
    LOGFONTW query = {}; query.lfCharSet = DEFAULT_CHARSET;
    HDC dc = GetDC(host.window);
    if (dc) { EnumFontFamiliesExW(dc, &query, enumerateFont, reinterpret_cast<LPARAM>(combo), 0); ReleaseDC(host.window, dc); }
    wchar_t selected[128] = {};
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(host.draft.font_family), -1, selected, 128);
    LRESULT row = SendMessageW(combo, CB_FINDSTRINGEXACT, -1, reinterpret_cast<LPARAM>(selected));
    if (row == CB_ERR) row = SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(selected));
    SendMessageW(combo, CB_SETCURSEL, row, 0);
    SendMessageW(combo, WM_SETREDRAW, TRUE, 0);
    InvalidateRect(combo, nullptr, FALSE);
}
void fillList(int id) {
    HWND list = GetDlgItem(host.window, id);
    if (!list) return;
    SendMessageW(list, WM_SETREDRAW, FALSE, 0);
    SendMessageW(list, LB_RESETCONTENT, 0, 0);
    int selected = -1;
    for (size_t i = 0; i < host.model.count; ++i) {
        const auto& item = listItem(i);
        LRESULT row = SendMessageW(list, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(item.label));
        if (row >= 0) {
            SendMessageW(list, LB_SETITEMDATA, (WPARAM)row, item.value);
            if (item.value == (id == themeGridId ? host.draft.theme : host.model.selected)) selected = (int)row;
        }
    }
    SendMessageW(list, LB_SETCURSEL, selected, 0);
    SendMessageW(list, WM_SETREDRAW, TRUE, 0);
    if (host.model.kind == 2) SetWindowPos(list, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW | (host.model.count ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    InvalidateRect(list, nullptr, FALSE);
}
bool listSelection(uint32_t& value, int id) {
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
float dialogWidth() {
    switch (host.model.kind) {
    case 1: return 580;
    case 2: return 560;
    case 4: return 760;
    case 5: return 472;
    case 6: return 440;
    case 8: return 352;
    case 9: return 480;
    case 7: return 600;
    default: return 480;
    }
}
float dialogHeight() {
    if (host.model.kind == 9) return 186.0f + 64.0f * (float)(host.model.count < 5 ? host.model.count : 5);
    if (host.model.kind == 7) return 500.0f;
    if (host.model.kind == 2) return 112.0f + 12.0f * (float)px(listRowHeight()) / host.scale;
    if (host.model.kind == 5) {
        const size_t rows = host.model.count > shellLimit ? shellLimit : host.model.count;
        return 120.0f + (float)px(listRowHeight()) / host.scale * (float)rows;
    }
    return host.model.kind == 8 ? 184.0f : host.model.kind == 6 ? 208.0f : host.model.kind == 4 ? 580.0f : 520.0f;
}
bool settingsRect(int id, float& x, float& y, float& w, float& h) {
    if (host.model.kind != 4) return false;
    const auto general = settingsGeometry(SettingsPage::general);
    const auto terminal = settingsGeometry(SettingsPage::terminal);
    const auto appearance = settingsGeometry(SettingsPage::appearance);
    const bool compact = general.compact;
    const float rowHeight = general.rowHeight, inner = 20;
    const auto rowTop = [&](const SettingsGeometry& g, int card, int row) { return g.cards[card].top + (float)row * rowHeight; };
    // Labels end before the row's trailing control so long translations or fonts never overlap it.
    const auto label = [&](const SettingsGeometry& g, int card, int row, float trailingWidth, bool detail) {
        x = g.left + inner; w = g.right - inner - trailingWidth - 16 - x;
        y = rowTop(g, card, row) + (compact ? (rowHeight - 22) / 2 : detail ? 31.0f : 10.0f);
        h = detail ? 20.0f : 22.0f;
    };
    const auto trailing = [&](const SettingsGeometry& g, int card, int row, float width, float height, float closed, float reserve) {
        w = width; h = height; x = g.right - inner - reserve - width; y = rowTop(g, card, row) + (rowHeight - closed) / 2;
    };
    // Toggle rows keep a gutter so their hit area never paints over the card's rounded corners or dividers.
    const auto toggle = [&](const SettingsGeometry& g, int card, int row) {
        x = g.left + 8; y = rowTop(g, card, row) + 4; w = g.right - g.left - 16; h = rowHeight - 8;
    };
    if (id >= firstPageId && id < firstPageId + 4) { x = (host.width - 448) / 2 + (float)(id - firstPageId) * 112; y = compact ? 44.0f : 56.0f; w = 108; h = compact ? 44.0f : 68.0f; return true; }
    switch (id) {
    case titleId: x = 120; y = compact ? 12.0f : 18.0f; w = host.width - 240; h = 28; return true;
    case closeId: x = host.width - 42; y = 12; w = 28; h = 28; return true;
    case themeGridId: x = 16; y = appearance.gridTop; w = host.width - 32; h = appearance.gridHeight; return true;
    case shellLabelId: label(general, 0, 0, 248, false); return true;
    case shellDetailId: label(general, 0, 0, 248, true); return true;
    case shellId: trailing(general, 0, 0, 248, 220, 30, 0); return true;
    case customShellLabelId: label(general, 0, 1, 168, false); return true;
    case customShellDetailId: label(general, 0, 1, 168, true); return true;
    case addShellId: trailing(general, 0, 1, 168, 30, 30, 0); return true;
    case copyOnSelectId: toggle(general, 1, 0); return true;
    case confirmCloseId: toggle(general, 1, 1); return true;
    case fontLabelId: label(terminal, 0, 0, 288, false); return true;
    case fontDetailId: label(terminal, 0, 0, 288, true); return true;
    case fontId: trailing(terminal, 0, 0, 288, 240, 30, 0); return true;
    case fontSizeLabelId: label(terminal, 0, 1, 96, false); return true;
    case fontSizeDetailId: label(terminal, 0, 1, 96, true); return true;
    // The edit sits inside a painted field that extends 8 dip past it on each side.
    case sizeId: trailing(terminal, 0, 1, 64, 26, 26, 8); return true;
    case blinkId: toggle(terminal, 1, 0); return true;
    }
    return false;
}
void setPageControlVisibility(int id, bool visible) {
    HWND child = GetDlgItem(host.window, id);
    const bool wasVisible = (GetWindowLongPtrW(child, GWL_STYLE) & WS_VISIBLE) != 0;
    if (wasVisible != visible) SetWindowPos(child, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
}
void showPage(SettingsPage previousPage) {
    const bool general = host.page == SettingsPage::general, appearance = host.page == SettingsPage::appearance, terminal = host.page == SettingsPage::terminal;
    const bool compact = host.height < 430, detail = !compact;
    if (terminal && SendDlgItemMessageW(host.window, fontId, CB_GETCOUNT, 0, 0) == 0) {
        // General/Appearance need no system font enumeration on the dialog's opening path.
        fillFonts(GetDlgItem(host.window, fontId));
    }
    const struct PageControl { int id; bool visible; } controls[] = {
        { shellId, general }, { shellLabelId, general }, { shellDetailId, general && detail }, { customShellLabelId, general }, { customShellDetailId, general && detail },
        { addShellId, general }, { copyOnSelectId, general }, { confirmCloseId, general }, { themeGridId, appearance },
        { sizeId, terminal }, { blinkId, terminal }, { fontSizeLabelId, terminal }, { fontSizeDetailId, terminal && detail }, { fontId, terminal }, { fontLabelId, terminal }, { fontDetailId, terminal && detail },
    };
    constexpr UINT visibilityFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW;
    HDWP batch = BeginDeferWindowPos((int)(sizeof(controls) / sizeof(controls[0])));
    for (const auto& entry : controls) {
        HWND child = GetDlgItem(host.window, entry.id);
        if (batch && ((GetWindowLongPtrW(child, GWL_STYLE) & WS_VISIBLE) != 0) != entry.visible)
            batch = DeferWindowPos(batch, child, nullptr, 0, 0, 0, 0, visibilityFlags | (entry.visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    }
    if (!batch || !EndDeferWindowPos(batch)) for (const auto& entry : controls) setPageControlVisibility(entry.id, entry.visible);
    // The window title follows the selected page, as the section tabs already name it.
    const wchar_t* names[] = { L"General", L"Appearance", L"Terminal", L"About" };
    const bool active = host.phase == DialogHost::Phase::active;
    if (active) SendDlgItemMessageW(host.window, titleId, WM_SETREDRAW, FALSE, 0);
    SetDlgItemTextW(host.window, titleId, names[(int)host.page]);
    if (active) {
        SendDlgItemMessageW(host.window, titleId, WM_SETREDRAW, TRUE, 0);
        InvalidateRect(GetDlgItem(host.window, titleId), nullptr, FALSE);
        if (previousPage != host.page) {
            InvalidateRect(GetDlgItem(host.window, firstPageId + (int)previousPage), nullptr, FALSE);
            InvalidateRect(GetDlgItem(host.window, firstPageId + (int)host.page), nullptr, FALSE);
        }
        RECT content = { 0, px(compact ? 88 : 124), px(host.width), px(host.height) };
        RedrawWindow(host.window, &content, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
    }
}
bool createControls(float width, float height) {
    const bool settings = host.model.kind == 4, commands = host.model.kind == 2, shells = host.model.kind == 5, closing = destructiveDialog();
    const bool exiting = host.model.kind == 9;
    bool ok = true;
    // Settings geometry comes from one description shared with painting and relayout.
    const auto place = [](const wchar_t* klass, const wchar_t* text, DWORD style, int id) {
        float x = 0, y = 0, w = 1, h = 1;
        settingsRect(id, x, y, w, h);
        return control(klass, text, style, id, x, y, w, h);
    };
    if (settings) ok = place(L"STATIC", L"General", SS_NOPREFIX | SS_CENTER, titleId) != nullptr;
    else if (!commands && !shells) ok = control(L"STATIC", host.model.title, SS_NOPREFIX | SS_LEFT | (closing ? SS_ENDELLIPSIS : 0), titleId, closing && !exiting ? 68 : 24, closing ? 24 : 22, closing && !exiting ? width - 88 : width - 48, 28) != nullptr;
    if (settings) {
        const wchar_t* pages[] = { L"General", L"Appearance", L"Terminal", L"About" };
        for (int i = 0; i < 4; ++i) ok = place(L"BUTTON", pages[i], BS_OWNERDRAW | WS_TABSTOP, firstPageId + i) && ok;
        HWND grid = place(L"LISTBOX", L"", WS_TABSTOP | WS_HSCROLL | LBS_MULTICOLUMN | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, themeGridId);
        ok = grid && ok;
        if (grid) { SendMessageW(grid, LB_SETITEMHEIGHT, 0, px(themeRowHeight())); SendMessageW(grid, LB_SETCOLUMNWIDTH, px(themeColumnWidth()), 0); fillList(themeGridId); }
        ok = place(L"STATIC", L"Font size", SS_NOPREFIX | SS_ENDELLIPSIS, fontSizeLabelId) && ok;
        ok = place(L"STATIC", L"Terminal text size · 9–32 pt", SS_NOPREFIX | SS_ENDELLIPSIS, fontSizeDetailId) && ok;
        wchar_t size[8]; swprintf(size, 8, L"%u", host.model.settings.font_size);
        HWND fontSize = place(L"EDIT", size, ES_NUMBER | ES_AUTOHSCROLL | ES_CENTER | WS_TABSTOP, sizeId);
        ok = fontSize && ok;
        if (fontSize) SendMessageW(fontSize, EM_SETLIMITTEXT, 2, 0);
        ok = place(L"STATIC", L"Font family", SS_NOPREFIX | SS_ENDELLIPSIS, fontLabelId) && ok;
        ok = place(L"STATIC", L"Monospace fonts installed on this PC", SS_NOPREFIX | SS_ENDELLIPSIS, fontDetailId) && ok;
        HWND fonts = place(L"COMBOBOX", L"", CBS_DROPDOWNLIST | CBS_SORT | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_TABSTOP | WS_VSCROLL, fontId);
        ok = fonts && ok;
        ok = place(L"BUTTON", L"Blink terminal cursor", BS_OWNERDRAW | WS_TABSTOP, blinkId) && ok;
        ok = place(L"STATIC", L"Default shell", SS_NOPREFIX | SS_ENDELLIPSIS, shellLabelId) && ok;
        ok = place(L"STATIC", L"Used for new tabs and splits", SS_NOPREFIX | SS_ENDELLIPSIS, shellDetailId) && ok;
        HWND shell = place(L"COMBOBOX", L"", CBS_DROPDOWNLIST | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_TABSTOP | WS_VSCROLL, shellId);
        ok = shell && ok;
        if (shell) fillCombo(shell, host.model.shells, host.model.shellCount, host.model.settings.shell);
        ok = place(L"STATIC", L"Custom shells", SS_NOPREFIX | SS_ENDELLIPSIS, customShellLabelId) && ok;
        ok = place(L"STATIC", L"Add a program or WSL command to the list", SS_NOPREFIX | SS_ENDELLIPSIS, customShellDetailId) && ok;
        ok = place(L"BUTTON", L"Add custom shell...", BS_OWNERDRAW | WS_TABSTOP, addShellId) && ok;
        ok = place(L"BUTTON", L"Copy on selection", BS_OWNERDRAW | WS_TABSTOP, copyOnSelectId) && ok;
        ok = place(L"BUTTON", L"Confirm before closing active programs", BS_OWNERDRAW | WS_TABSTOP, confirmCloseId) && ok;
        showPage(host.page);
    } else {
        if (!commands && !shells) ok = control(L"STATIC", host.model.description, SS_NOPREFIX, descriptionId, 24, exiting ? 58 : closing ? 68 : 58, width - 48, exiting ? 40 : closing ? height - 132 : 38) && ok;
    }
    if (host.model.kind == 7) {
        ok = control(L"STATIC", L"Name", SS_NOPREFIX, 211, 32, 102, width - 64, 22) && ok;
        HWND name = control(L"EDIT", L"", ES_AUTOHSCROLL | WS_TABSTOP, shellNameId, 40, 130, width - 80, 26);
        ok = name && ok;
        if (name) SendMessageW(name, EM_SETLIMITTEXT, 255, 0);
        ok = control(L"STATIC", L"Program", SS_NOPREFIX, 212, 32, 178, width - 64, 22) && ok;
        HWND program = control(L"EDIT", L"", ES_AUTOHSCROLL | WS_TABSTOP, shellProgramId, 40, 206, width - 190, 26);
        ok = program && ok;
        if (program) SendMessageW(program, EM_SETLIMITTEXT, 32766, 0);
        ok = control(L"BUTTON", L"Browse...", BS_OWNERDRAW | WS_TABSTOP, shellBrowseId, width - 130, 202, 98, 34) && ok;
        ok = control(L"STATIC", L"An executable path, a PATH command, or wsl:/absolute/program for a Linux shell in your default WSL distribution.", SS_NOPREFIX, 213, 32, 248, width - 64, 42) && ok;
        ok = control(L"STATIC", L"Arguments (optional)", SS_NOPREFIX, 214, 32, 304, width - 64, 22) && ok;
        HWND args = control(L"EDIT", L"", ES_AUTOHSCROLL | WS_TABSTOP, shellArgumentsId, 40, 332, width - 80, 26);
        ok = args && ok;
        if (args) SendMessageW(args, EM_SETLIMITTEXT, 32766, 0);
        ok = control(L"STATIC", L"", SS_NOPREFIX, shellErrorId, 32, 376, width - 64, 54) && ok;
    }
    if (exiting) {
        HWND list = control(L"LISTBOX", L"", WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | LBS_NOINTEGRALHEIGHT, listId, 16, 110, width - 32, height - 186);
        ok = list && ok;
        if (list) { SendMessageW(list, LB_SETITEMHEIGHT, 0, px(listRowHeight())); fillList(); }
    }
    if (!settings && !closing && host.model.kind != 7) {
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
            ok = control(L"BUTTON", exiting ? L"Keep Vex open" : closing ? L"Keep open" : L"Cancel", WS_TABSTOP | BS_OWNERDRAW, IDCANCEL, width - (exiting ? 284 : 260), height - 54, exiting ? 128 : 104, 34) && ok;
            ok = control(L"BUTTON", host.model.accept, WS_TABSTOP | BS_OWNERDRAW, IDOK, width - 144, height - 54, 120, 34) && ok;
        }
    }
    if (!settings) SendMessageW(host.window, DM_SETDEFID, closing ? IDCANCEL : IDOK, 0);
    return ok;
}

INT_PTR inspectListPainting(HWND window, WPARAM wparam, LPARAM) {
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
INT_PTR inspectListScroll(HWND window, WPARAM wparam, LPARAM lparam) {
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
void drawDialogItem(const DRAWITEMSTRUCT& draw) {
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
        return;
    }
    ++host.dialogPaintCount;
    const int saved = SaveDC(draw.hDC);
    if (draw.CtlType == ODT_LISTBOX) drawListItem(draw);
    else if (draw.CtlType == ODT_BUTTON) drawDialogButton(draw);
    else if (draw.CtlType == ODT_COMBOBOX) drawComboItem(draw);
    if (saved) RestoreDC(draw.hDC, saved);
}
}
