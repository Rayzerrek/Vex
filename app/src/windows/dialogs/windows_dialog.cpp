#include "dialog_internal.h"
#include "selection_input.h"
#include "ui_renderer.h"
#include <commctrl.h>
#include <gdiplus.h>
#include <uxtheme.h>
#include <dwmapi.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <wctype.h>
#include <shobjidl.h>
#include <stdlib.h>

namespace vex::dialog {
DialogHost host;
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
        output[i].icon = input[i].icon;
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
    free(host.exitItems); host.exitItems = nullptr;
}
void notify(uint32_t event, uint32_t value, const uint8_t* text, size_t length, VexUserSettings settings) {
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
    if (event == 0 && host.model.kind != 2 && host.model.kind != 8 && host.layered && vex_window_animations_enabled()) {
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
bool navigateDialogList(HWND list, WPARAM key) {
    const bool themes = host.model.kind == 1 || GetDlgCtrlID(list) == themeGridId;
    if ((!themes && host.model.kind != 2) || GetKeyState(VK_CONTROL) < 0 || GetKeyState(VK_MENU) < 0 || !list) return false;
    const bool horizontal = key == VK_LEFT || key == VK_RIGHT;
    if (key != VK_UP && key != VK_DOWN && key != VK_HOME && key != VK_END && key != VK_PRIOR && key != VK_NEXT && !(themes && horizontal)) return false;
    suspendListHover(list);
    const int count = (int)SendMessageW(list, LB_GETCOUNT, 0, 0);
    if (count <= 0) return true;
    int selected = (int)SendMessageW(list, LB_GETCURSEL, 0, 0);
    if (selected < 0) selected = 0;
    const int previous = selected;
    RECT viewport; GetClientRect(list, &viewport);
    const int rowHeight = (int)SendMessageW(list, LB_GETITEMHEIGHT, 0, 0);
    const int pageRows = rowHeight > 0 && viewport.bottom >= rowHeight ? viewport.bottom / rowHeight : 1;
    if (key == VK_HOME) selected = 0;
    else if (key == VK_END) selected = count - 1;
    else if (themes) {
        int dx = key == VK_LEFT ? -1 : key == VK_RIGHT ? 1 : 0;
        if (key == VK_PRIOR || key == VK_NEXT) {
            RECT item = {};
            SendMessageW(list, LB_GETITEMRECT, 0, reinterpret_cast<LPARAM>(&item));
            const int width = item.right - item.left;
            const int columns = width > 0 && viewport.right >= width ? viewport.right / width : 1;
            dx = key == VK_PRIOR ? -columns : columns;
        }
        selected = themeGridNeighbor(selected, count, pageRows, dx, key == VK_UP ? -1 : key == VK_DOWN ? 1 : 0);
    } else if (key == VK_UP) selected = selected == 0 ? count - 1 : selected - 1;
    else if (key == VK_DOWN) selected = selected + 1 == count ? 0 : selected + 1;
    else if (key == VK_PRIOR) selected = selected > pageRows ? selected - pageRows : 0;
    else selected = selected + pageRows < count ? selected + pageRows : count - 1;
    if (selected == previous) return true;
    SendMessageW(list, LB_SETCURSEL, selected, 0);
    const LRESULT value = SendMessageW(list, LB_GETITEMDATA, selected, 0);
    if (value >= 0 && (uint64_t)value <= UINT32_MAX) {
        if (GetDlgCtrlID(list) == themeGridId) {
            host.draft.theme = (uint32_t)value;
            notify(4, 0, nullptr, 0, host.draft);
        } else notify(2, (uint32_t)value);
    }
    return true;
}
void setFontSizeInvalid(bool invalid) {
    if (host.fontSizeInvalid == invalid) return;
    host.fontSizeInvalid = invalid;
    SetDlgItemTextW(host.window, fontSizeDetailId, invalid ? L"Enter a size between 9 and 32 pt" : L"Terminal text size · 9–32 pt");
    // The field frame belongs to the dialog surface, so the surface must repaint to change its border.
    RECT field; GetWindowRect(GetDlgItem(host.window, sizeId), &field);
    MapWindowPoints(nullptr, host.window, reinterpret_cast<POINT*>(&field), 2);
    InflateRect(&field, px(8), px(6));
    InvalidateRect(host.window, &field, FALSE);
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
        setFontSizeInvalid(!restoreInvalid);
        return;
    }
    setFontSizeInvalid(false);
    if (size != host.draft.font_size) {
        host.draft.font_size = size;
        notify(4, 0, nullptr, 0, host.draft);
    }
}
void shellFormError(const wchar_t* message) {
    SetDlgItemTextW(host.window, shellErrorId, message);
    InvalidateRect(GetDlgItem(host.window, shellErrorId), nullptr, FALSE);
}
void browseShellProgram() {
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) { shellFormError(L"The file chooser could not be opened. Enter the path manually."); return; }
    IFileOpenDialog* dialog = nullptr;
    HRESULT result = CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog));
    if (SUCCEEDED(result)) {
        const COMDLG_FILTERSPEC filters[] = { { L"Programs", L"*.exe;*.com" }, { L"All files", L"*.*" } };
        dialog->SetFileTypes(2, filters);
        dialog->SetTitle(L"Choose a shell executable");
        dialog->SetOptions(FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM | FOS_DONTADDTORECENT);
        result = dialog->Show(host.window);
        if (SUCCEEDED(result)) {
            IShellItem* item = nullptr;
            result = dialog->GetResult(&item);
            if (SUCCEEDED(result)) {
                PWSTR path = nullptr;
                result = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
                if (SUCCEEDED(result)) { SetDlgItemTextW(host.window, shellProgramId, path); CoTaskMemFree(path); }
                item->Release();
            }
        }
        dialog->Release();
    }
    if (SUCCEEDED(initialized)) CoUninitialize();
    if (FAILED(result) && result != HRESULT_FROM_WIN32(ERROR_CANCELLED)) shellFormError(L"The file chooser could not be opened. Enter the path manually.");
}
void submitCustomShell() {
    constexpr int capacity = 256 * 1024;
    auto* bytes = static_cast<uint8_t*>(malloc(capacity));
    if (!bytes) { shellFormError(L"Not enough memory to save this shell. Try again."); return; }
    const int ids[] = { shellNameId, shellProgramId, shellArgumentsId };
    size_t length = 0;
    wchar_t wide[32768];
    bool valid = true;
    for (int i = 0; i < 3; ++i) {
        const int units = GetDlgItemTextW(host.window, ids[i], wide, 32768);
        if (units) {
            const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide, units, reinterpret_cast<char*>(bytes + length), capacity - (int)length - 2, nullptr, nullptr);
            if (!count) { valid = false; break; }
            length += (size_t)count;
        }
        if (i < 2) bytes[length++] = 0;
    }
    if (valid) notify(7, 0, bytes, length);
    else shellFormError(L"The fields are too long or contain incomplete Unicode characters.");
    free(bytes);
}
void acceptDialog() {
    if (host.model.kind == 7) { submitCustomShell(); return; }
    if (host.model.kind == 4) applySettingsFontSize(false);
    else if (destructiveDialog()) finish(1);
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
INT_PTR CALLBACK dialogProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_INITDIALOG: {
        host.window = window;
        SetDialogDpiChangeBehavior(window, DDC_DISABLE_ALL, DDC_DISABLE_ALL);
        SetWindowTextW(window, host.model.title);
        host.scale = GetDpiForWindow(host.owner) / 96.0f;
        host.font = CreateFontW(-px(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        host.heading = CreateFontW(-px(headingSize()), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
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
        HWND focus = GetDlgItem(window, host.model.kind == 7 ? shellNameId : host.model.kind == 4 ? shellId : destructiveDialog() ? IDCANCEL : host.model.kind == 2 ? queryId : listId);
        SetFocus(focus);
        return FALSE;
    }
    case WM_ACTIVATE:
        if (host.model.kind == 8 && LOWORD(wparam) == WA_INACTIVE && host.phase == DialogHost::Phase::active) {
            // Let the click reach the owner without a closing animation or focus theft.
            closeDialog(false);
            notify(0);
            return TRUE;
        }
        break;
    case WM_COMMAND:
        if (host.phase != DialogHost::Phase::active || host.synchronizing) return TRUE;
        if (LOWORD(wparam) == IDCANCEL || LOWORD(wparam) == closeId) { finish(0); return TRUE; }
        if (LOWORD(wparam) >= firstPageId && LOWORD(wparam) < firstPageId + 4) {
            const auto page = (SettingsPage)(LOWORD(wparam) - firstPageId);
            if (page != host.page) { const auto previousPage = host.page; host.page = page; showPage(previousPage); }
            return TRUE;
        }
        if (LOWORD(wparam) == blinkId || LOWORD(wparam) == copyOnSelectId || LOWORD(wparam) == confirmCloseId || LOWORD(wparam) == activePaneBorderId) {
            const int id = LOWORD(wparam);
            if (id == blinkId) host.draft.cursor_blink = !host.draft.cursor_blink;
            else if (id == copyOnSelectId) host.draft.copy_on_select = !host.draft.copy_on_select;
            else if (id == activePaneBorderId) host.draft.active_pane_border = !host.draft.active_pane_border;
            else host.draft.confirm_close = !host.draft.confirm_close;
            notify(4, 0, nullptr, 0, host.draft);
            InvalidateRect(GetDlgItem(window, id), nullptr, FALSE);
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
        if (LOWORD(wparam) == densityId && HIWORD(wparam) == CBN_SELCHANGE) {
            uint32_t value;
            if (comboSelection(densityId, value) && value <= 2 && value != host.draft.ui_density) {
                host.draft.ui_density = value;
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
        if (host.model.kind == 7 && HIWORD(wparam) == EN_CHANGE &&
            (LOWORD(wparam) == shellNameId || LOWORD(wparam) == shellProgramId || LOWORD(wparam) == shellArgumentsId)) {
            if (GetWindowTextLengthW(GetDlgItem(window, shellErrorId))) shellFormError(L"");
            return TRUE;
        }
        if (LOWORD(wparam) == addShellId && host.model.kind == 4) { notify(6); return TRUE; }
        if (LOWORD(wparam) == shellBrowseId && host.model.kind == 7) { browseShellProgram(); return TRUE; }
        if (LOWORD(wparam) == IDOK) { acceptDialog(); return TRUE; }
        if (LOWORD(wparam) == queryId && HIWORD(wparam) == EN_CHANGE) { queryChanged(); return TRUE; }
        if (LOWORD(wparam) == listId && HIWORD(wparam) == LBN_DBLCLK && host.model.kind != 9) { acceptDialog(); return TRUE; }
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
    case WM_APP + 7: return inspectListPainting(window, wparam, lparam);
    case WM_APP + 6: return inspectListScroll(window, wparam, lparam);
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
            host.copyOnSelectMotion.update(host.draft.copy_on_select ? 1.0f : 0.0f, now, vex_window_animations_enabled());
            host.confirmCloseMotion.update(host.draft.confirm_close ? 1.0f : 0.0f, now, vex_window_animations_enabled());
            host.activePaneBorderMotion.update(host.draft.active_pane_border ? 1.0f : 0.0f, now, vex_window_animations_enabled());
            if ((!host.cursorBlinkMotion.pending(now) && !host.copyOnSelectMotion.pending(now) && !host.confirmCloseMotion.pending(now) && !host.activePaneBorderMotion.pending(now)) ||
                (host.page != SettingsPage::terminal && host.page != SettingsPage::general)) KillTimer(window, 8);
            InvalidateRect(GetDlgItem(window, blinkId), nullptr, FALSE);
            InvalidateRect(GetDlgItem(window, copyOnSelectId), nullptr, FALSE);
            InvalidateRect(GetDlgItem(window, confirmCloseId), nullptr, FALSE);
            InvalidateRect(GetDlgItem(window, activePaneBorderId), nullptr, FALSE);
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
        SetWindowLongPtrW(window, DWLP_MSGRESULT, host.model.kind == 4 ? 0 : MAKELRESULT(destructiveDialog() ? IDCANCEL : IDOK, DC_HASDEFID));
        return TRUE;
    case DM_SETDEFID: SetWindowLongPtrW(window, DWLP_MSGRESULT, TRUE); return TRUE;
    case WM_DRAWITEM:
        if (lparam) {
            const auto& draw = *reinterpret_cast<const DRAWITEMSTRUCT*>(lparam);
            drawDialogItem(draw);
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
        const bool muted = id == descriptionId || id == shellDetailId || id == customShellDetailId || id == fontDetailId || id == fontSizeDetailId || id == densityDetailId || id == 213;
        SetTextColor(dc, id == shellErrorId || (id == fontSizeDetailId && host.fontSizeInvalid) ? (host.model.palette.is_dark ? RGB(229, 164, 124) : RGB(155, 75, 40)) : color(muted ? host.model.palette.text_muted : host.model.palette.text_primary));
        const bool card = host.model.kind == 4 && (id == sizeId || id == shellId || id == fontId || id == fontLabelId || id == fontDetailId || id == fontSizeLabelId || id == fontSizeDetailId ||
        id == shellLabelId || id == shellDetailId || id == customShellLabelId || id == customShellDetailId);
        if (id == queryId || id == sizeId || id == shellNameId || id == shellProgramId || id == shellArgumentsId) {
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

using namespace vex::dialog;

void initializeTerminalDialogs(HWND owner, VexWindowCallbacks callbacks) {
    host.owner = owner; host.callbacks = callbacks;
}
HWND terminalDialogWindow() { return host.window; }
bool routeTerminalDialogMessage(MSG& message) { return host.window && IsDialogMessageW(host.window, &message); }
void drawTerminalDialogBackdrop(UiRenderer& target, UiBrush& brush, float scale) {
    if (!IsWindow(host.window) || !IsWindow(host.owner) || host.model.kind == 8) return;
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
    const bool wasCompact = host.height < 430;
    host.scale = scale;
    host.width = fminf(dialogWidth(), (owner.right - owner.left) / scale - 32);
    host.height = fminf(dialogHeight(), (owner.bottom - owner.top) / scale - 40);
    if (host.width <= 0 || host.height <= 0) return;
    const int top = host.model.kind == 8 ? (int)fmaxf(0, fminf((float)px(host.model.anchorY + 4), (float)(owner.bottom - px(host.height))))
        : (host.model.kind == 2 || host.model.kind == 5) ? (int)fminf((float)px(64), (owner.bottom - px(host.height)) / 2.0f) : (owner.bottom - px(host.height)) / 2;
    const int left = origin.x + (host.model.kind == 8 ? (int)fmaxf((float)px(8), fminf((float)px(host.model.anchorX), (float)(owner.right - px(host.width) - px(8)))) : (owner.right - px(host.width)) / 2);
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
        HFONT heading = CreateFontW(-px(headingSize()), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HFONT symbols = CreateFontW(-px(22), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe MDL2 Assets");
        if (font && heading && symbols) { oldFont = host.font; oldHeading = host.heading; oldSymbols = host.symbols; host.font = font; host.heading = heading; host.symbols = symbols; }
        else { if (font) DeleteObject(font); if (heading) DeleteObject(heading); if (symbols) DeleteObject(symbols); }
    }
    SetWindowPos(host.window, HWND_TOP, left, origin.y + top, px(host.width), px(host.height), SWP_NOACTIVATE);
    HRGN rounded = CreateRoundRectRgn(0, 0, px(host.width) + 1, px(host.height) + 1, px(24), px(24));
    if (rounded && !SetWindowRgn(host.window, rounded, FALSE)) DeleteObject(rounded);
    const float dx = host.width - host.baseWidth, dy = host.height - host.baseHeight;
    for (size_t i = 0; i < host.controlCount; ++i) {
        const auto& c = host.controls[i];
        const int id = GetDlgCtrlID(c.window);
        float x = c.x, y = c.y, w = c.width, h = c.height;
        if (!settingsRect(id, x, y, w, h)) {
            if (id == IDOK || id == IDCANCEL || id == closeId || id == shellBrowseId) x += dx;
            else if (w > host.baseWidth * 0.5f) w += dx;
            if (id == IDOK || id == IDCANCEL) y += dy;
            if (id == listId) h += dy;
        }
        if (id == listId && host.model.kind != 1) {
            const int rowPixels = px(listRowHeight());
            const int rows = px(h) / rowPixels;
            if (rows > 0) h = (float)(rows * rowPixels) / host.scale;
        }
        if (host.model.kind == 7) {
            const bool small = host.height < 440;
            if (id == descriptionId || id == 213) setPageControlVisibility(id, !small);
            if (id == titleId) { x = 24; y = 22; w = host.width - 80; }
            if (id == descriptionId) { x = 24; y = 58; w = host.width - 48; }
            if (id == 211 || id == 212 || id == 214 || id == 213 || id == shellErrorId) { x = 32; w = host.width - 64; }
            if (id == 211) y = small ? 68 : 102;
            if (id == shellNameId) { x = 40; y = small ? 94 : 130; w = host.width - 80; }
            if (id == 212) y = small ? 132 : 178;
            if (id == shellProgramId) { x = 40; y = small ? 158 : 206; w = host.width - 190; }
            if (id == shellBrowseId) { x = host.width - 130; y = small ? 154 : 202; }
            if (id == 214) y = small ? 206 : 304;
            if (id == shellArgumentsId) { x = 40; y = small ? 232 : 332; w = host.width - 80; }
            if (id == shellErrorId) { y = small ? 266 : 376; h = small ? 36 : 54; }
            if (id == IDOK || id == IDCANCEL) { x = host.width - (id == IDOK ? 136 : 244); y = host.height - 52; }
            if (id == closeId) x = host.width - 42;
        }
        MoveWindow(c.window, px(x), px(y), px(fmaxf(w, 1)), px(fmaxf(h, 1)), FALSE);
        if (oldFont) SendMessageW(c.window, WM_SETFONT, reinterpret_cast<WPARAM>(controlFont(id)), FALSE);
        if (id == listId || id == themeGridId) {
            SendMessageW(c.window, LB_SETITEMHEIGHT, 0, px(host.model.kind == 1 || id == themeGridId ? themeRowHeight() : listRowHeight()));
            if (host.model.kind == 1 || id == themeGridId) SendMessageW(c.window, LB_SETCOLUMNWIDTH, px(themeColumnWidth()), 0);
        }
        if (id == shellId || id == fontId || id == densityId) {
            SendMessageW(c.window, CB_SETITEMHEIGHT, (WPARAM)-1, px(30));
            SendMessageW(c.window, CB_SETITEMHEIGHT, 0, px(30));
        }
    }
    if (oldFont) DeleteObject(oldFont);
    if (oldHeading) DeleteObject(oldHeading);
    if (oldSymbols) DeleteObject(oldSymbols);
    // Crossing the compact threshold changes which row descriptions have room to show.
    if (host.model.kind == 4 && wasCompact != (host.height < 430)) showPage(host.page);
    host.layoutReady = true;
    RedrawWindow(host.window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_ERASE);
}
extern "C" void vex_dialog_form_error(VexDialogText message) {
    if (host.model.kind != 7 || !IsWindow(host.window)) return;
    wchar_t text[512];
    if (decodeText(message, text, 512)) shellFormError(text);
}
extern "C" void vex_dialog_close() { closeDialog(true); }
extern "C" uint32_t vex_dialog_first_visible() {
    HWND list = host.window ? GetDlgItem(host.window, listId) : nullptr;
    if (!list) return 0;
    LRESULT first = SendMessageW(list, LB_GETTOPINDEX, 0, 0);
    return first >= 0 && (uint64_t)first <= UINT32_MAX ? (uint32_t)first : 0;
}
extern "C" int vex_dialog_open(VexDialogRequest request) {
    if (!IsWindow(host.owner) || request.kind < 1 || request.kind == 3 || request.kind > 9 || request.count > (request.kind == 9 ? 65536 : itemLimit) || request.shell_count > shellLimit) return ERROR_INVALID_PARAMETER;
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
        (request.kind != 9 && !copyItems(request.items, request.count, next.items, itemLimit)) || !copyItems(request.shells, request.shell_count, next.shells, shellLimit)) return ERROR_INVALID_DATA;
    if (!isfinite(request.anchor_x) || !isfinite(request.anchor_y)) return ERROR_INVALID_DATA;
    next.icon = request.icon; next.anchorX = request.anchor_x; next.anchorY = request.anchor_y;
    next.kind = request.kind; next.selected = request.selected; next.count = request.count; next.shellCount = request.shell_count; next.settings = request.settings; next.palette = request.palette;
    if (host.phase == DialogHost::Phase::active) {
        if (host.model.kind != request.kind || request.kind == 9) closeDialog(true);
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
        const bool copyChanged = host.model.settings.copy_on_select != next.settings.copy_on_select;
        const bool confirmChanged = host.model.settings.confirm_close != next.settings.confirm_close;
        const bool borderChanged = host.model.settings.active_pane_border != next.settings.active_pane_border;
        if (itemsChanged || paletteChanged) clearThemeCardCache();
        host.model = next;
        if (host.model.kind == 4) {
            host.draft = next.settings;
            SendDlgItemMessageW(host.window, densityId, CB_SETCURSEL, host.draft.ui_density, 0);
            BOOL valid = FALSE;
            const UINT size = GetDlgItemInt(host.window, sizeId, &valid, FALSE);
            if (!valid || size != host.draft.font_size) {
                wchar_t text[8]; swprintf(text, 8, L"%u", host.draft.font_size);
                SetDlgItemTextW(host.window, sizeId, text);
            }
            setFontSizeInvalid(false);
            uint32_t selectedShell;
            if (!comboSelection(shellId, selectedShell) || selectedShell != host.draft.shell)
                fillCombo(GetDlgItem(host.window, shellId), host.model.shells, host.model.shellCount, host.draft.shell);
            if (blinkChanged) InvalidateRect(GetDlgItem(host.window, blinkId), nullptr, FALSE);
            if (copyChanged) InvalidateRect(GetDlgItem(host.window, copyOnSelectId), nullptr, FALSE);
            if (confirmChanged) InvalidateRect(GetDlgItem(host.window, confirmCloseId), nullptr, FALSE);
            if (borderChanged) InvalidateRect(GetDlgItem(host.window, activePaneBorderId), nullptr, FALSE);
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
        // Preview colors do not change list membership; resetting would discard pixel scrolling.
        if (itemsChanged) fillList(listControlId);
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
    if (request.kind == 9 && request.count) {
        host.exitItems = static_cast<DialogItem*>(calloc(request.count, sizeof(DialogItem)));
        if (!host.exitItems) return ERROR_NOT_ENOUGH_MEMORY;
        if (!copyItems(request.items, request.count, host.exitItems, request.count)) {
            free(host.exitItems); host.exitItems = nullptr;
            return ERROR_INVALID_DATA;
        }
    }
    host.model = next;
    host.draft = request.settings;
    host.cursorBlinkMotion = {};
    host.cursorBlinkMotion.update(host.draft.cursor_blink ? 1.0f : 0.0f, GetTickCount64(), false);
    host.copyOnSelectMotion.update(host.draft.copy_on_select ? 1.0f : 0.0f, GetTickCount64(), false);
    host.confirmCloseMotion = {};
    host.confirmCloseMotion.update(host.draft.confirm_close ? 1.0f : 0.0f, GetTickCount64(), false);
    host.activePaneBorderMotion = {};
    host.activePaneBorderMotion.update(host.draft.active_pane_border ? 1.0f : 0.0f, GetTickCount64(), false);
    host.page = SettingsPage::general;
    host.fontSizeInvalid = false;
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
    if (request.kind != 8 && !createDialogBackdrop()) { closeDialog(true); return ERROR_NOT_ENOUGH_MEMORY; }
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
    if (request.kind != 8) EnableWindow(host.owner, FALSE);
    // The backdrop is part of the owner, not the popup's GDI surface.
    vex_window_invalidate();
    if (IsWindow(host.backdrop)) SetWindowPos(host.backdrop, result, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    ShowWindow(result, SW_SHOW);
    SetActiveWindow(result);
    HWND focus = GetDlgItem(result, request.kind == 7 ? shellNameId : request.kind == 4 ? shellId : destructiveDialog() ? IDCANCEL : request.kind == 2 ? queryId : listId);
    SetFocus(focus);
    RedrawWindow(result, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    return 0;
}
