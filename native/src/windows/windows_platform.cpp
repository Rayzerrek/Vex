#include "windows_platform.h"
#include <windows.h>
#include <windowsx.h>
#include <d2d1.h>
#include <dwrite.h>
#include <dwmapi.h>
#include <math.h>
#include <stdio.h>

static VexWindowCallbacks callbacks;
static HWND window;
static ID2D1Factory* drawingFactory;
static IDWriteFactory* textFactory;
static ID2D1HwndRenderTarget* target;
static ID2D1SolidColorBrush* brush;
static IDWriteTextFormat* terminalFonts[4];
static IDWriteTextFormat* chromeFont;
static IDWriteTextFormat* chromeBoldFont;
static IDWriteTextFormat* badgeFont;
static ID2D1StrokeStyle* roundStrokeStyle;
struct IconCacheEntry {
    uint64_t key;
    ID2D1Bitmap* bitmap;
};
static IconCacheEntry iconCache[64];
static size_t iconCacheCount;
static size_t iconCacheClock;
static float cellWidth, cellHeight, clientWidth, clientHeight, gridLeft, gridTop;
static uint32_t tabStripCount = 1;
static float tabWidthDip = 160;
static bool frameScheduled, firstOutput, firstFrame;
static ULONGLONG frameDeadline;
static int platformError;
static LARGE_INTEGER startupCounter, counterFrequency;
static uint16_t pendingSurrogate;
static uint64_t paintCount;
static wchar_t projectName[64] = L"Vex";
static wchar_t projectBranch[64] = L"";
static int hoveredCaptionButton;
static int pressedCaptionButton;
static bool hoveredNewTab;
static int hoveredTabIndex = -1;
static bool hoveredTabClose;
static bool trackingMouseLeave;
enum class PointerCaptureState { none, armed, dragging, cancelled };
static PointerCaptureState pointerCaptureState = PointerCaptureState::none;

HWND vex_native_window() { return window; }

template<class T> static void releaseCom(T*& object) {
    if (object) { object->Release(); object = nullptr; }
}
static void clearIconCache() {
    for (size_t i = 0; i < iconCacheCount; i++) {
        releaseCom(iconCache[i].bitmap);
        iconCache[i].key = 0;
    }
    iconCacheCount = 0;
    iconCacheClock = 0;
}
static D2D1_COLOR_F rgb(uint32_t color) {
    return D2D1::ColorF((color >> 16) / 255.0f, ((color >> 8) & 255) / 255.0f, (color & 255) / 255.0f);
}
static void rectangle(float x, float y, float width, float height, uint32_t color) {
    brush->SetColor(rgb(color));
    target->FillRectangle(D2D1::RectF(x, y, x + width, y + height), brush);
}
static void labelWithFont(IDWriteTextFormat* font, const wchar_t* text, float x, float y, float width, uint32_t color) {
    brush->SetColor(rgb(color));
    target->DrawText(text, (UINT32)wcslen(text), font ? font : chromeFont, D2D1::RectF(x, y, x + width, y + 24), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
}
static void label(const wchar_t* text, float x, float y, float width, uint32_t color) {
    labelWithFont(chromeFont, text, x, y, width, color);
}
static float plusLeft() { return 140 + tabStripCount * (tabWidthDip + 4); }
extern "C" int vex_window_tab_strip(uint32_t count, float tab_width) {
    if (!count || count > 8 || !isfinite(tab_width) || tab_width < 24 || tab_width > 160) return ERROR_INVALID_PARAMETER;
    tabStripCount = count; tabWidthDip = tab_width;
    return 0;
}
extern "C" int vex_draw_tab_insertion_marker(float x) {
    if (!target || !brush || !isfinite(x) || x < 140 || x > plusLeft()) return ERROR_INVALID_PARAMETER;
    rectangle(x - 1, 7, 2, 30, 0x9b3bb9);
    return 0;
}

static void startupMilestone(const char* milestone) {
    LARGE_INTEGER now; QueryPerformanceCounter(&now);
    char line[180];
    snprintf(line, sizeof(line), "Vex native %s: %.3f ms\n", milestone,
        1000.0 * (now.QuadPart - startupCounter.QuadPart) / counterFrequency.QuadPart);
    OutputDebugStringA(line);
    wchar_t path[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(L"VEX_NATIVE_METRICS", path, MAX_PATH);
    if (length > 0 && length < MAX_PATH) {
        HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE) {
            DWORD written; WriteFile(file, line, (DWORD)strlen(line), &written, nullptr); CloseHandle(file);
        }
    }
}
static bool selftestEnabled() {
    wchar_t enabled[2];
    return GetEnvironmentVariableW(L"VEX_NATIVE_SELFTEST", enabled, 2) == 1 && enabled[0] == L'1';
}
static HRESULT createRenderTarget() {
    if (target) return S_OK;
    RECT bounds; GetClientRect(window, &bounds);
    const float dpi = (float)GetDpiForWindow(window);
    HRESULT result = drawingFactory->CreateHwndRenderTarget(
        D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT, D2D1::PixelFormat(), dpi, dpi),
        D2D1::HwndRenderTargetProperties(window, D2D1::SizeU(bounds.right, bounds.bottom)), &target);
    if (FAILED(result)) return result;
    result = target->CreateSolidColorBrush(rgb(0xe4e4e7), &brush);
    if (FAILED(result)) releaseCom(target);
    return result;
}
static void updateWindowMetrics() {
    RECT bounds; GetClientRect(window, &bounds);
    float scale = GetDpiForWindow(window) / 96.0f;
    clientWidth = bounds.right / scale;
    clientHeight = bounds.bottom / scale;
    callbacks.resize(callbacks.context, clientWidth, clientHeight, cellWidth, cellHeight, scale);
}
static void renderScheduledFrame() {
    KillTimer(window, 1); frameScheduled = false;
    InvalidateRect(window, nullptr, FALSE); UpdateWindow(window);
}
extern "C" void vex_window_invalidate() {
    if (!IsWindow(window)) return;
    ULONGLONG now = GetTickCount64();
    if (!frameScheduled || frameDeadline > now + 8) {
        frameDeadline = now + 8;
        if (!SetTimer(window, 1, 8, nullptr)) { platformError = (int)GetLastError(); DestroyWindow(window); }
        else frameScheduled = true;
    } else if (now >= frameDeadline) renderScheduledFrame();
}
extern "C" void vex_window_defer_synchronized() {
    if (!IsWindow(window)) return;
    ULONGLONG now = GetTickCount64();
    if (!frameScheduled || frameDeadline < now + 100) {
        frameDeadline = now + 100;
        if (!SetTimer(window, 1, 100, nullptr)) { platformError = (int)GetLastError(); DestroyWindow(window); }
        else frameScheduled = true;
    } else if (now >= frameDeadline) renderScheduledFrame();
}
extern "C" void vex_window_close() { if (IsWindow(window)) PostMessageW(window, WM_CLOSE, 0, 0); }
extern "C" uint64_t vex_window_paint_count() { return paintCount; }

static void drawProjectPill() {
    const D2D1_ROUNDED_RECT pill = D2D1::RoundedRect(D2D1::RectF(8.0f, 7.0f, 134.0f, 37.0f), 6.0f, 6.0f);
    brush->SetColor(rgb(0x262936));
    target->FillRoundedRectangle(pill, brush);
    brush->SetColor(rgb(0x41439a));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(8.5f, 7.5f, 133.5f, 36.5f), 5.5f, 5.5f), brush, 1.0f);
    // Lucide Folder glyph on the left of the project pill.
    brush->SetColor(rgb(0x9ea2ff));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(15.5f, 18.0f, 28.5f, 27.5f), 1.5f, 1.5f), brush, 1.25f);
    target->DrawLine(D2D1::Point2F(15.5f, 18.5f), D2D1::Point2F(15.5f, 15.5f), brush, 1.25f);
    target->DrawLine(D2D1::Point2F(15.5f, 15.5f), D2D1::Point2F(20.5f, 15.5f), brush, 1.25f);
    target->DrawLine(D2D1::Point2F(20.5f, 15.5f), D2D1::Point2F(22.0f, 18.0f), brush, 1.25f);
    if (projectBranch[0] != 0) {
        labelWithFont(chromeBoldFont, projectName, 34.0f, 8.5f, 82.0f, 0xe4e4e7);
        labelWithFont(badgeFont, projectBranch, 34.0f, 22.0f, 82.0f, 0x8f9197);
    } else {
        labelWithFont(chromeBoldFont, projectName, 34.0f, 13.0f, 82.0f, 0xe4e4e7);
    }
    brush->SetColor(rgb(0x8f9197));
    target->DrawLine(D2D1::Point2F(119.5f, 20.5f), D2D1::Point2F(122.5f, 23.5f), brush, 1.2f);
    target->DrawLine(D2D1::Point2F(122.5f, 23.5f), D2D1::Point2F(125.5f, 20.5f), brush, 1.2f);
}

static void drawNewTabButton() {
    const float bx = plusLeft() + 2.0f;
    const float by = 8.0f;
    const bool enabled = tabStripCount < 8;
    if (enabled && hoveredNewTab) {
        brush->SetColor(rgb(0x262932));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, by, bx + 28.0f, by + 28.0f), 5.0f, 5.0f), brush);
    }
    const float cx = bx + 14.0f;
    const float cy = by + 14.0f;
    brush->SetColor(rgb(enabled ? (hoveredNewTab ? 0xffffff : 0xe4e4e7) : 0x60636a));
    target->DrawLine(D2D1::Point2F(cx - 5.0f, cy), D2D1::Point2F(cx + 5.0f, cy), brush, 1.35f);
    target->DrawLine(D2D1::Point2F(cx, cy - 5.0f), D2D1::Point2F(cx, cy + 5.0f), brush, 1.35f);
}

static void drawCaptionButtons() {
    const float baseLeft = clientWidth - 120.0f;
    // Minimize button (button 1)
    {
        const float left = baseLeft;
        if (pressedCaptionButton == 1 || hoveredCaptionButton == 1) {
            rectangle(left, 0.0f, 40.0f, 43.0f, pressedCaptionButton == 1 ? 0x353944 : 0x262932);
        }
        const float cx = left + 20.0f, cy = 21.5f;
        brush->SetColor(rgb(0xe4e4e7));
        target->DrawLine(D2D1::Point2F(cx - 5.0f, cy), D2D1::Point2F(cx + 5.0f, cy), brush, 1.0f);
    }
    // Maximize / Restore button (button 2)
    {
        const float left = baseLeft + 40.0f;
        if (pressedCaptionButton == 2 || hoveredCaptionButton == 2) {
            rectangle(left, 0.0f, 40.0f, 43.0f, pressedCaptionButton == 2 ? 0x353944 : 0x262932);
        }
        const float cx = left + 20.0f, cy = 21.5f;
        brush->SetColor(rgb(0xe4e4e7));
        if (IsZoomed(window)) {
            target->DrawRectangle(D2D1::RectF(cx - 4.5f, cy - 2.5f, cx + 2.5f, cy + 4.5f), brush, 1.0f);
            target->DrawLine(D2D1::Point2F(cx - 2.5f, cy - 2.5f), D2D1::Point2F(cx - 2.5f, cy - 4.5f), brush, 1.0f);
            target->DrawLine(D2D1::Point2F(cx - 2.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy - 4.5f), brush, 1.0f);
            target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy + 2.5f), brush, 1.0f);
            target->DrawLine(D2D1::Point2F(cx + 2.5f, cy + 2.5f), D2D1::Point2F(cx + 4.5f, cy + 2.5f), brush, 1.0f);
        } else {
            target->DrawRectangle(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), brush, 1.0f);
        }
    }
    // Close button (button 3)
    {
        const float left = baseLeft + 80.0f;
        const bool hot = pressedCaptionButton == 3 || hoveredCaptionButton == 3;
        if (hot) {
            rectangle(left, 0.0f, 40.0f, 43.0f, pressedCaptionButton == 3 ? 0xf1707a : 0xe81123);
        }
        const float cx = left + 20.0f, cy = 21.5f;
        brush->SetColor(rgb(hot ? 0xffffff : 0xe4e4e7));
        target->DrawLine(D2D1::Point2F(cx - 4.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy + 4.5f), brush, 1.15f);
        target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.15f);
    }
}

static void drawWindow() {
    HRESULT result = createRenderTarget();
    if (FAILED(result)) { platformError = (int)result; DestroyWindow(window); return; }
    target->BeginDraw();
    paintCount++;
    target->Clear(rgb(0x0f1114));
    rectangle(0.0f, 0.0f, clientWidth, 44.0f, 0x181a1f);
    rectangle(0.0f, 43.0f, clientWidth, 1.0f, 0x2d3139);
    drawProjectPill();
    callbacks.paint(callbacks.context);
    drawNewTabButton();
    drawCaptionButtons();
    result = target->EndDraw();
    if (result == D2DERR_RECREATE_TARGET) {
        clearIconCache(); releaseCom(brush); releaseCom(target); InvalidateRect(window, nullptr, FALSE);
    } else if (FAILED(result)) { platformError = (int)result; DestroyWindow(window); }
    else if (!firstFrame) { firstFrame = true; startupMilestone("first frame"); }
}

static void pasteClipboard() {
    DWORD error = ERROR_SUCCESS;
    if (!OpenClipboard(window)) error = GetLastError() ? GetLastError() : ERROR_BUSY;
    else {
        if (IsClipboardFormatAvailable(CF_UNICODETEXT)) {
            HANDLE data = GetClipboardData(CF_UNICODETEXT);
            auto text = data ? static_cast<const wchar_t*>(GlobalLock(data)) : nullptr;
            if (!text) error = GetLastError() ? GetLastError() : ERROR_INVALID_DATA;
            else {
                SIZE_T capacity = GlobalSize(data) / sizeof(wchar_t);
                size_t units = 0;
                while (units < capacity && units < 1024 * 1024 && text[units]) units++;
                if (units >= capacity || units >= 1024 * 1024) error = ERROR_BUFFER_OVERFLOW;
                else if (units) {
                    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, (int)units, nullptr, 0, nullptr, nullptr);
                    if (size <= 0) error = GetLastError();
                    else if (size > 1024 * 1024) error = ERROR_BUFFER_OVERFLOW;
                    else {
                        auto bytes = static_cast<uint8_t*>(HeapAlloc(GetProcessHeap(), 0, size));
                        if (!bytes) error = ERROR_OUTOFMEMORY;
                        else {
                            if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, (int)units, (char*)bytes, size, nullptr, nullptr)) callbacks.paste(callbacks.context, bytes, size);
                            else error = GetLastError();
                            HeapFree(GetProcessHeap(), 0, bytes);
                        }
                    }
                }
                GlobalUnlock(data);
            }
        }
        CloseClipboard();
    }
    // Report after releasing clipboard/borrowed memory; a warning may pump window messages.
    if (error) {
        char message[160];
        snprintf(message, sizeof(message), "Vex native paste failed (Win32 %lu). Clipboard text must be valid Unicode and at most 1 MiB.", (unsigned long)error);
        MessageBoxA(window, message, "Vex native paste", MB_OK | MB_ICONWARNING);
    }
}
extern "C" void vex_window_paste_clipboard() { pasteClipboard(); }
static uint32_t currentModifiers() {
    return (GetKeyState(VK_SHIFT) < 0 ? 1u : 0u) | (GetKeyState(VK_CONTROL) < 0 ? 2u : 0u) | (GetKeyState(VK_MENU) < 0 ? 4u : 0u);
}
static bool handleKey(uint32_t key) {
    uint32_t mods = currentModifiers();
    if ((key == 'V' && mods == 3) || (key == VK_INSERT && mods == 1)) { pasteClipboard(); return true; }
    return callbacks.key(callbacks.context, key, mods);
}
static void setPointerCursor(uint32_t response, float y) {
    uint16_t resource = response == VEX_POINTER_RIGHT ? 32644 : response == VEX_POINTER_DOWN ? 32645 :
        response == VEX_POINTER_SELECTION ? 32513 :
        response == VEX_POINTER_TAB_ARMED || response == VEX_POINTER_TAB_DRAGGING || y < 74 ? 32512 : 32513;
    SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(resource)));
}
extern "C" int vex_window_cancel_pointer_capture() {
    if (pointerCaptureState == PointerCaptureState::none || pointerCaptureState == PointerCaptureState::cancelled) return 0;
    bool captured = pointerCaptureState == PointerCaptureState::dragging;
    // Suppress a later button-up over chrome even after capture has been stolen or deliberately released.
    pointerCaptureState = PointerCaptureState::cancelled;
    if (captured && GetCapture() == window && !ReleaseCapture()) {
        DWORD error = GetLastError();
        return (int)(error ? error : ERROR_INVALID_HANDLE);
    }
    return 0;
}
extern "C" bool vex_window_has_pointer_capture() {
    return window && GetCapture() == window;
}
static void releasePointerCapture() {
    int error = vex_window_cancel_pointer_capture();
    if (error) { platformError = error; DestroyWindow(window); }
}
static void cancelPointerGesture() {
    if (pointerCaptureState == PointerCaptureState::none || pointerCaptureState == PointerCaptureState::cancelled) return;
    callbacks.pointer(callbacks.context, VEX_POINTER_CANCEL, 0, 0, currentModifiers());
    // Also release if a failed application ignores further callbacks during destruction.
    releasePointerCapture();
}
static void capturePointerGesture(HWND hwnd) {
    pointerCaptureState = PointerCaptureState::dragging;
    SetCapture(hwnd);
    if (GetCapture() != hwnd) cancelPointerGesture();
}
static void beginPointerGesture(HWND hwnd, float x, float y) {
    uint32_t response = callbacks.pointer(callbacks.context, VEX_POINTER_BEGIN, x, y, currentModifiers());
    if (response == VEX_POINTER_NORMAL) return;
    SetFocus(hwnd);
    if (response == VEX_POINTER_TAB_ARMED) pointerCaptureState = PointerCaptureState::armed;
    else capturePointerGesture(hwnd);
    setPointerCursor(response, y);
}
static void updateTopChromeHover(float x, float y) {
    int nextCaption = 0;
    bool nextNewTab = false;
    int nextTab = -1;
    bool nextTabClose = false;
    if (pointerCaptureState == PointerCaptureState::none && y >= 0.0f && y < 44.0f) {
        if (x >= clientWidth - 120.0f && x < clientWidth) {
            nextCaption = (x >= clientWidth - 40.0f) ? 3 : (x >= clientWidth - 80.0f) ? 2 : 1;
        } else if (x >= plusLeft() && x < plusLeft() + 32.0f && y >= 7.0f && y <= 37.0f) {
            nextNewTab = true;
        } else if (x >= 140.0f && x < plusLeft() && y >= 7.0f && y <= 37.0f) {
            uint32_t idx = (uint32_t)((x - 140.0f) / (tabWidthDip + 4.0f));
            float local = x - 140.0f - idx * (tabWidthDip + 4.0f);
            if (idx < tabStripCount && local >= 0.0f && local < tabWidthDip) {
                nextTab = (int)idx;
                nextTabClose = local > tabWidthDip - 22.0f;
            }
        }
    }
    if (nextCaption != hoveredCaptionButton || nextNewTab != hoveredNewTab ||
        nextTab != hoveredTabIndex || nextTabClose != hoveredTabClose) {
        hoveredCaptionButton = nextCaption;
        hoveredNewTab = nextNewTab;
        hoveredTabIndex = nextTab;
        hoveredTabClose = nextTabClose;
        vex_window_invalidate();
    }
}
static LRESULT CALLBACK windowProcedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_NCCALCSIZE: if (wparam) return 0; break;
    case WM_NCHITTEST: {
        LRESULT hit = DefWindowProcW(hwnd, message, wparam, lparam);
        if (hit != HTCLIENT) return hit;
        POINT point = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        ScreenToClient(hwnd, &point);
        RECT bounds; GetClientRect(hwnd, &bounds);
        int edge = (int)(6 * GetDpiForWindow(hwnd) / 96.0f);
        if (!IsZoomed(hwnd)) {
            bool left = point.x < edge, right = point.x >= bounds.right - edge;
            bool top = point.y < edge, bottom = point.y >= bounds.bottom - edge;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = point.x / scale;
        if (point.y / scale < 44 && x < clientWidth - 120 && (x < 140 || x > plusLeft() + 32)) return HTCAPTION;
        return HTCLIENT;
    }
    case WM_GETMINMAXINFO: {
        auto limits = reinterpret_cast<MINMAXINFO*>(lparam);
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        limits->ptMinTrackSize = POINT{(LONG)(640 * scale), (LONG)(400 * scale)};
        MONITORINFO monitor = {}; monitor.cbSize = sizeof(monitor);
        if (GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor)) {
            limits->ptMaxPosition = POINT{monitor.rcWork.left - monitor.rcMonitor.left, monitor.rcWork.top - monitor.rcMonitor.top};
            limits->ptMaxSize = POINT{monitor.rcWork.right - monitor.rcWork.left, monitor.rcWork.bottom - monitor.rcWork.top};
        }
        return 0;
    }
    case WM_SETCURSOR:
        if (LOWORD(lparam) == HTCLIENT) {
            POINT point;
            if (!GetCursorPos(&point) || !ScreenToClient(hwnd, &point)) break;
            float scale = GetDpiForWindow(hwnd) / 96.0f;
            float x = point.x / scale, y = point.y / scale;
            setPointerCursor(callbacks.pointer(callbacks.context, VEX_POINTER_HOVER, x, y, currentModifiers()), y);
            return TRUE;
        }
        break;
    case WM_LBUTTONDOWN: case WM_MBUTTONDOWN: {
        cancelPointerGesture();
        pointerCaptureState = PointerCaptureState::none;
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (message == WM_LBUTTONDOWN && y < 44 && x >= clientWidth - 120) {
            pressedCaptionButton = (x >= clientWidth - 40) ? 3 : (x >= clientWidth - 80) ? 2 : 1;
            vex_window_invalidate();
            return 0;
        }
        if (y < 44 && x >= 140 && x < plusLeft()) {
            uint32_t index = (uint32_t)((x - 140) / (tabWidthDip + 4));
            float local = x - 140 - index * (tabWidthDip + 4);
            if (index >= tabStripCount || local >= tabWidthDip) return 0;
            bool close = message == WM_MBUTTONDOWN || local > tabWidthDip - 22;
            if (close) callbacks.action(callbacks.context, 7, index);
            else beginPointerGesture(hwnd, x, y);
        } else if (y < 44 && x >= plusLeft() && x < plusLeft() + 32) callbacks.action(callbacks.context, 0, 0);
        else if (y >= 44) {
            if (message == WM_LBUTTONDOWN) {
                beginPointerGesture(hwnd, x, y);
                if (pointerCaptureState == PointerCaptureState::none) callbacks.click(callbacks.context, x, y);
            } else {
                SetFocus(hwnd);
                callbacks.mouse_button(callbacks.context, 3, true, x, y, currentModifiers());
            }
        }
        return 0;
    }
    case WM_MBUTTONUP: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        callbacks.mouse_button(callbacks.context, 3, false, x, y, currentModifiers());
        return 0;
    }
    case WM_RBUTTONDOWN: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (y >= 44) {
            SetFocus(hwnd);
            callbacks.mouse_button(callbacks.context, 2, true, x, y, currentModifiers());
        }
        return 0;
    }
    case WM_RBUTTONUP: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        callbacks.mouse_button(callbacks.context, 2, false, x, y, currentModifiers());
        return 0;
    }
    case WM_LBUTTONUP: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        int wasPressedCaption = pressedCaptionButton;
        if (pressedCaptionButton != 0) {
            pressedCaptionButton = 0;
            vex_window_invalidate();
        }
        if (pointerCaptureState != PointerCaptureState::none) {
            if (pointerCaptureState == PointerCaptureState::armed || pointerCaptureState == PointerCaptureState::dragging) callbacks.pointer(callbacks.context, VEX_POINTER_END, x, y, currentModifiers());
            releasePointerCapture();
            pointerCaptureState = PointerCaptureState::none;
            return 0;
        }
        if (y < 44 && x >= clientWidth - 120) {
            int btn = (x >= clientWidth - 40) ? 3 : (x >= clientWidth - 80) ? 2 : 1;
            if (wasPressedCaption == 0 || wasPressedCaption == btn) {
                if (btn == 3) SendMessageW(hwnd, WM_CLOSE, 0, 0);
                else if (btn == 2) ShowWindow(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
                else ShowWindow(hwnd, SW_MINIMIZE);
            }
        }
        return 0;
    }
    case WM_MOUSEMOVE: {
        if (!trackingMouseLeave) {
            TRACKMOUSEEVENT tme = {};
            tme.cbSize = sizeof(tme);
            tme.dwFlags = TME_LEAVE;
            tme.hwndTrack = hwnd;
            if (TrackMouseEvent(&tme)) trackingMouseLeave = true;
        }
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (pointerCaptureState == PointerCaptureState::armed && !(wparam & MK_LBUTTON)) cancelPointerGesture();
        uint32_t event = pointerCaptureState == PointerCaptureState::armed || pointerCaptureState == PointerCaptureState::dragging ? VEX_POINTER_MOVE : VEX_POINTER_HOVER;
        uint32_t response = callbacks.pointer(callbacks.context, event, x, y, currentModifiers());
        if (pointerCaptureState == PointerCaptureState::armed && response == VEX_POINTER_TAB_DRAGGING) capturePointerGesture(hwnd);
        else if (response == VEX_POINTER_NORMAL) cancelPointerGesture();
        updateTopChromeHover(x, y);
        setPointerCursor(response, y);
        return 0;
    }
    case WM_MOUSELEAVE:
        trackingMouseLeave = false;
        if (hoveredCaptionButton != 0 || pressedCaptionButton != 0 || hoveredNewTab || hoveredTabIndex != -1 || hoveredTabClose) {
            hoveredCaptionButton = 0;
            pressedCaptionButton = 0;
            hoveredNewTab = false;
            hoveredTabIndex = -1;
            hoveredTabClose = false;
            vex_window_invalidate();
        }
        return 0;
    case WM_CAPTURECHANGED: case WM_CANCELMODE:
        cancelPointerGesture();
        return 0;
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED && window) {
            if (target) {
                HRESULT result = target->Resize(D2D1::SizeU(LOWORD(lparam), HIWORD(lparam)));
                if (result == D2DERR_RECREATE_TARGET) { clearIconCache(); releaseCom(brush); releaseCom(target); }
                else if (FAILED(result)) { platformError = (int)result; DestroyWindow(hwnd); return 0; }
            }
            updateWindowMetrics(); InvalidateRect(hwnd, nullptr, FALSE);
        }
        return 0;
    case WM_DPICHANGED: {
        cancelPointerGesture();
        clearIconCache();
        auto bounds = reinterpret_cast<RECT*>(lparam);
        if (target) target->SetDpi((float)HIWORD(wparam), (float)HIWORD(wparam));
        if (!SetWindowPos(hwnd, nullptr, bounds->left, bounds->top, bounds->right - bounds->left, bounds->bottom - bounds->top, SWP_NOZORDER | SWP_NOACTIVATE)) {
            DWORD error = GetLastError();
            platformError = (int)(error ? error : ERROR_INVALID_WINDOW_HANDLE);
            DestroyWindow(hwnd);
            return 0;
        }
        // A DPI transition need not change physical bounds, so WM_SIZE alone cannot refresh DIP geometry.
        updateWindowMetrics(); InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    }
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: { PAINTSTRUCT paint; BeginPaint(hwnd, &paint); drawWindow(); EndPaint(hwnd, &paint); return 0; }
    case WM_APP + 1: {
        if (wparam > UINT32_MAX) return 0;
        uint32_t token = (uint32_t)wparam;
        const uint8_t* bytes; size_t length;
        if (vex_pty_borrow_output(token, &bytes, &length)) {
            if (!firstOutput) { firstOutput = true; startupMilestone("first PTY output"); }
            bool visible = callbacks.output(callbacks.context, token, bytes, length);
            vex_pty_ack_output(token);
            // Deadline check also renders when posted pipe events starve WM_TIMER.
            if (visible) vex_window_invalidate();
            else if (frameScheduled && GetTickCount64() >= frameDeadline) renderScheduledFrame();
        }
        return 0;
    }
    case WM_APP + 2: return selftestEnabled() && wparam <= UINT32_MAX ? (LRESULT)callbacks.probe(callbacks.context, (uint32_t)wparam) : 0;
    case WM_APP + 3:
        if (wparam <= UINT32_MAX && vex_pty_exists((uint32_t)wparam)) { platformError = lparam ? (int)lparam : ERROR_WRITE_FAULT; DestroyWindow(hwnd); }
        return 0;
    case WM_APP + 4:
        if (wparam <= UINT32_MAX && vex_pty_exists((uint32_t)wparam)) callbacks.exited(callbacks.context, (uint32_t)wparam, (uint32_t)lparam);
        return 0;
    case WM_APP + 5:
        if (selftestEnabled() && wparam <= 7 && lparam >= 0 && (uint64_t)lparam <= UINT32_MAX) callbacks.action(callbacks.context, (uint32_t)wparam, (uint32_t)lparam);
        return 0;
    case WM_APP + 6: {
        uint32_t source = (uint32_t)(wparam & 0xff), operation = (uint32_t)(wparam >> 8);
        if (!selftestEnabled() || wparam > 0x8ff || operation > 8 || source >= tabStripCount) return 0;
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float startX = (140 + source * (tabWidthDip + 4) + fminf(40, (tabWidthDip - 22) / 2)) * scale;
        if (!isfinite(startX) || startX < 0 || startX > 32767 || !isfinite(scale) || scale <= 0 || 20 * scale > 32767) return 0;
        // Replay one gesture in this callback so OS-generated moves cannot interleave with capture snapshots.
        SendMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, MAKELPARAM((uint16_t)lroundf(startX), (uint16_t)lroundf(20 * scale)));
        uint64_t pressed = callbacks.probe(callbacks.context, 45), capturedPress = vex_window_has_pointer_capture();
        SendMessageW(hwnd, WM_MOUSEMOVE, MK_LBUTTON, lparam);
        uint64_t result = (callbacks.probe(callbacks.context, 60 + source) << 32) |
            pressed | (capturedPress << 2) | (callbacks.probe(callbacks.context, 45) << 3) |
            ((uint64_t)vex_window_has_pointer_capture() << 5) | (callbacks.probe(callbacks.context, 49) << 8);
        switch (operation) {
            case 1: SendMessageW(hwnd, WM_KEYDOWN, VK_ESCAPE, 0); break;
            case 2: SendMessageW(hwnd, WM_CAPTURECHANGED, 0, 0); break;
            case 3: SendMessageW(hwnd, WM_CANCELMODE, 0, 0); break;
            case 4: SendMessageW(hwnd, WM_KILLFOCUS, 0, 0); break;
            case 5: {
                RECT bounds;
                if (!GetWindowRect(hwnd, &bounds)) { platformError = ERROR_INVALID_WINDOW_HANDLE; DestroyWindow(hwnd); return 0; }
                UINT dpi = GetDpiForWindow(hwnd);
                SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(dpi, dpi), (LPARAM)&bounds);
                break;
            }
            case 6: callbacks.action(callbacks.context, 7, source); break;
            case 7: SendMessageW(hwnd, WM_CLOSE, 0, 0); return (LRESULT)result;
            case 8: callbacks.action(callbacks.context, 4, 0); break;
        }
        LPARAM release = operation ? MAKELPARAM((uint16_t)lroundf((clientWidth - 20) * scale), (uint16_t)lroundf(20 * scale)) : lparam;
        SendMessageW(hwnd, WM_LBUTTONUP, 0, release);
        return (LRESULT)result;
    }
    case WM_TIMER: renderScheduledFrame(); return 0;
    case WM_KEYDOWN: case WM_SYSKEYDOWN:
        if (wparam == VK_ESCAPE && (pointerCaptureState == PointerCaptureState::armed || pointerCaptureState == PointerCaptureState::dragging)) { cancelPointerGesture(); return 1; }
        return handleKey((uint32_t)wparam) ? 1 : DefWindowProcW(hwnd, message, wparam, lparam);
    case WM_KILLFOCUS: cancelPointerGesture(); pendingSurrogate = 0; return 0;
    case WM_CHAR: case WM_SYSCHAR: {
        uint16_t unit = (uint16_t)wparam;
        if (unit >= 0xd800 && unit <= 0xdbff) { pendingSurrogate = unit; return 0; }
        wchar_t text[2]; int count = 1;
        if (pendingSurrogate && unit >= 0xdc00 && unit <= 0xdfff) { text[0] = pendingSurrogate; text[1] = unit; count = 2; }
        else text[0] = unit;
        pendingSurrogate = 0;
        uint8_t bytes[8];
        int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, count, (char*)bytes, sizeof(bytes), nullptr, nullptr);
        if (length > 0) callbacks.text(callbacks.context, bytes, (size_t)length);
        return 0;
    }
    case WM_MOUSEWHEEL: {
        POINT point = { GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam) };
        ScreenToClient(hwnd, &point);
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        callbacks.scroll(callbacks.context, -(GET_WHEEL_DELTA_WPARAM(wparam) / WHEEL_DELTA) * 3, point.x / scale, point.y / scale, currentModifiers());
        return 0;
    }
    case WM_CLOSE: cancelPointerGesture(); DestroyWindow(hwnd); return 0;
    case WM_DESTROY: cancelPointerGesture(); KillTimer(hwnd, 1); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

static void loadAppIcons(HICON& outBig, HICON& outSmall) {
    outBig = nullptr;
    outSmall = nullptr;
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(nullptr, path, 32768);
    if (!length || length >= 32768) return;
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash || (slash - path) + 12 >= 32768) return;
    wcscpy(slash + 1, L"app.ico");
    outBig = static_cast<HICON>(LoadImageW(nullptr, path, IMAGE_ICON, 32, 32, LR_LOADFROMFILE));
    outSmall = static_cast<HICON>(LoadImageW(nullptr, path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE));
}

extern "C" void vex_startup_begin() { QueryPerformanceCounter(&startupCounter); QueryPerformanceFrequency(&counterFrequency); }
extern "C" int vex_window_run(VexWindowCallbacks value) {
    callbacks = value;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HRESULT result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &drawingFactory);
    if (FAILED(result)) return (int)result;
    D2D1_STROKE_STYLE_PROPERTIES strokeProps = D2D1::StrokeStyleProperties(
        D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_ROUND,
        D2D1_LINE_JOIN_ROUND, 10.0f, D2D1_DASH_STYLE_SOLID, 0.0f);
    result = drawingFactory->CreateStrokeStyle(strokeProps, nullptr, 0, &roundStrokeStyle);
    if (SUCCEEDED(result)) result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)&textFactory);
    const auto configureChromeFormat = [](IDWriteTextFormat* fmt) -> HRESULT {
        HRESULT hr = fmt->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        if (FAILED(hr)) return hr;
        IDWriteInlineObject* ellipsis = nullptr;
        hr = textFactory->CreateEllipsisTrimmingSign(fmt, &ellipsis);
        DWRITE_TRIMMING trimming = {}; trimming.granularity = DWRITE_TRIMMING_GRANULARITY_CHARACTER;
        if (SUCCEEDED(hr)) hr = fmt->SetTrimming(&trimming, ellipsis);
        releaseCom(ellipsis);
        return hr;
    };
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12.5f, L"en-us", &chromeFont);
    if (SUCCEEDED(result)) result = configureChromeFormat(chromeFont);
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12.0f, L"en-us", &chromeBoldFont);
    if (SUCCEEDED(result)) result = configureChromeFormat(chromeBoldFont);
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 10.0f, L"en-us", &badgeFont);
    if (SUCCEEDED(result)) result = configureChromeFormat(badgeFont);
    for (int i = 0; i < 4 && SUCCEEDED(result); i++) {
        result = textFactory->CreateTextFormat(L"Cascadia Mono", nullptr, (i & 1) ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            (i & 2) ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 14, L"en-us", &terminalFonts[i]);
        if (SUCCEEDED(result)) result = terminalFonts[i]->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    }
    if (SUCCEEDED(result)) {
        IDWriteTextLayout* measure = nullptr;
        result = textFactory->CreateTextLayout(L"M", 1, terminalFonts[0], 100, 100, &measure);
        if (SUCCEEDED(result)) {
            DWRITE_TEXT_METRICS metrics; DWRITE_LINE_METRICS line; UINT32 count;
            result = measure->GetMetrics(&metrics);
            if (SUCCEEDED(result)) result = measure->GetLineMetrics(&line, 1, &count);
            if (SUCCEEDED(result)) {
                cellWidth = metrics.width; cellHeight = ceilf(line.height);
                if (!isfinite(cellWidth) || !isfinite(cellHeight) || cellWidth <= 0 || cellHeight <= 0) result = E_INVALIDARG;
                for (auto font : terminalFonts) if (SUCCEEDED(result)) result = font->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, cellHeight, line.baseline);
            }
            releaseCom(measure);
        }
    }
    HICON iconBig = nullptr, iconSmall = nullptr;
    loadAppIcons(iconBig, iconSmall);
    if (SUCCEEDED(result)) {
        WNDCLASSEXW klass = {};
        klass.cbSize = sizeof(klass); klass.lpfnWndProc = windowProcedure;
        klass.hInstance = GetModuleHandleW(nullptr); klass.lpszClassName = L"VexNativeWorkspace";
        klass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32513));
        klass.hIcon = iconBig ? iconBig : LoadIconW(nullptr, MAKEINTRESOURCEW(32512));
        klass.hIconSm = iconSmall ? iconSmall : klass.hIcon;
        if (!RegisterClassExW(&klass)) platformError = (int)GetLastError();
        else {
            const float initialScale = GetDpiForSystem() / 96.0f;
            window = CreateWindowExW(0, klass.lpszClassName, L"Vex", WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, (int)(1120 * initialScale), (int)(700 * initialScale), nullptr, nullptr, klass.hInstance, nullptr);
            if (!window) platformError = (int)GetLastError();
            else {
                if (iconBig) SendMessageW(window, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(iconBig));
                if (iconSmall) SendMessageW(window, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(iconSmall));
                MARGINS margins = { 0, 0, 1, 0 };
                DwmExtendFrameIntoClientArea(window, &margins);
                BOOL dark = TRUE; DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
                DWORD corner = 2; DwmSetWindowAttribute(window, 33, &corner, sizeof(corner));
                COLORREF border = 0x0039312D; DwmSetWindowAttribute(window, 34, &border, sizeof(border));
                SetEnvironmentVariableW(L"TERM", L"xterm-256color"); SetEnvironmentVariableW(L"COLORTERM", L"truecolor");
                updateWindowMetrics();
                ShowWindow(window, SW_SHOW); UpdateWindow(window);
                MSG message; BOOL status;
                while ((status = GetMessageW(&message, nullptr, 0, 0)) > 0) {
                    if (message.message == WM_KEYDOWN || message.message == WM_SYSKEYDOWN) {
                        // Do not TranslateMessage a consumed workspace chord into terminal control bytes.
                        if (DispatchMessageW(&message) != 1) TranslateMessage(&message);
                    } else { TranslateMessage(&message); DispatchMessageW(&message); }
                }
                if (status < 0) platformError = (int)GetLastError();
            }
        }
    } else platformError = (int)result;
    if (iconBig) DestroyIcon(iconBig);
    if (iconSmall) DestroyIcon(iconSmall);
    clearIconCache();
    releaseCom(brush); releaseCom(target);
    releaseCom(chromeFont); releaseCom(chromeBoldFont); releaseCom(badgeFont);
    for (auto& font : terminalFonts) releaseCom(font);
    releaseCom(roundStrokeStyle);
    releaseCom(textFactory); releaseCom(drawingFactory);
    return platformError;
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

static void drawAppIconContent(ID2D1RenderTarget* rt, ID2D1SolidColorBrush* b, VexAppIcon icon, float ox, float oy, float sizeDip) {
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
        rt->DrawLine(pt(x1, y1), pt(x2, y2), b, sw, roundStrokeStyle);
    };
    const auto thinLine = [&](float x1, float y1, float x2, float y2) {
        rt->DrawLine(pt(x1, y1), pt(x2, y2), b, sw * 0.78f, roundStrokeStyle);
    };
    const auto circle = [&](float cx, float cy, float r) {
        rt->DrawEllipse(D2D1::Ellipse(pt(cx, cy), r * s, r * s), b, sw, roundStrokeStyle);
    };
    const auto fillCircle = [&](float cx, float cy, float r) {
        rt->FillEllipse(D2D1::Ellipse(pt(cx, cy), r * s, r * s), b);
    };
    const auto box = [&](float x1, float y1, float x2, float y2, float rx) {
        rt->DrawRoundedRectangle(rrc(x1, y1, x2, y2, rx), b, sw, roundStrokeStyle);
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
    switch (icon.glyph) {
    case 2: // powershell
        box(2.5f, 3.5f, 21.5f, 20.5f, 3.0f);
        line(6.5f, 8.0f, 11.5f, 12.0f);
        line(11.5f, 12.0f, 6.5f, 16.0f);
        line(12.5f, 16.0f, 17.5f, 16.0f);
        break;
    case 3: // code_brackets (< / >)
        line(8.0f, 7.0f, 3.5f, 12.0f);
        line(3.5f, 12.0f, 8.0f, 17.0f);
        line(16.0f, 7.0f, 20.5f, 12.0f);
        line(20.5f, 12.0f, 16.0f, 17.0f);
        thinLine(13.5f, 5.5f, 10.5f, 18.5f);
        break;
    case 4: // neovim
        line(5.5f, 19.5f, 5.5f, 4.5f);
        line(5.5f, 4.5f, 18.5f, 19.5f);
        line(18.5f, 19.5f, 18.5f, 4.5f);
        break;
    case 5: // vim
        line(4.5f, 5.5f, 12.0f, 19.5f);
        line(12.0f, 19.5f, 19.5f, 5.5f);
        line(4.5f, 5.5f, 9.0f, 5.5f);
        line(15.0f, 5.5f, 19.5f, 5.5f);
        break;
    case 6: // git_branch (Lucide git-branch)
        line(6.5f, 5.0f, 6.5f, 19.0f);
        circle(6.5f, 6.0f, 2.3f);
        circle(6.5f, 18.0f, 2.3f);
        circle(17.5f, 8.0f, 2.3f);
        line(6.5f, 14.0f, 14.5f, 14.0f);
        line(14.5f, 14.0f, 17.5f, 10.3f);
        break;
    case 7: // github
        circle(12.0f, 12.5f, 7.5f);
        line(7.0f, 6.5f, 5.5f, 3.5f);
        line(5.5f, 3.5f, 9.5f, 5.5f);
        line(17.0f, 6.5f, 18.5f, 3.5f);
        line(18.5f, 3.5f, 14.5f, 5.5f);
        break;
    case 8: // docker
        fillBox(5.0f, 9.0f, 8.2f, 12.2f, 0.6f);
        fillBox(9.2f, 9.0f, 12.4f, 12.2f, 0.6f);
        fillBox(13.4f, 9.0f, 16.6f, 12.2f, 0.6f);
        fillBox(9.2f, 5.0f, 12.4f, 8.2f, 0.6f);
        line(3.5f, 14.0f, 20.5f, 14.0f);
        line(4.5f, 14.0f, 6.5f, 18.5f);
        line(6.5f, 18.5f, 17.0f, 18.5f);
        line(17.0f, 18.5f, 20.5f, 14.0f);
        break;
    case 9: // kubernetes
        circle(12.0f, 12.0f, 8.0f);
        fillCircle(12.0f, 12.0f, 2.2f);
        thinLine(12.0f, 2.5f, 12.0f, 21.5f);
        thinLine(2.5f, 12.0f, 21.5f, 12.0f);
        thinLine(5.3f, 5.3f, 18.7f, 18.7f);
        thinLine(18.7f, 5.3f, 5.3f, 18.7f);
        break;
    case 10: // nodejs (hexagon)
        line(12.0f, 3.0f, 20.0f, 7.5f);
        line(20.0f, 7.5f, 20.0f, 16.5f);
        line(20.0f, 16.5f, 12.0f, 21.0f);
        line(12.0f, 21.0f, 4.0f, 16.5f);
        line(4.0f, 16.5f, 4.0f, 7.5f);
        line(4.0f, 7.5f, 12.0f, 3.0f);
        thinLine(12.0f, 7.5f, 12.0f, 16.5f);
        break;
    case 11: // npm_box
        box(3.5f, 3.5f, 20.5f, 20.5f, 2.0f);
        line(8.0f, 16.5f, 8.0f, 8.5f);
        line(8.0f, 8.5f, 15.5f, 8.5f);
        line(15.5f, 8.5f, 15.5f, 16.5f);
        line(12.0f, 11.5f, 12.0f, 16.5f);
        break;
    case 12: // pnpm_cubes
        fillBox(3.5f, 3.5f, 11.0f, 11.0f, 1.5f);
        fillBox(13.0f, 3.5f, 20.5f, 11.0f, 1.5f);
        fillBox(3.5f, 13.0f, 11.0f, 20.5f, 1.5f);
        box(13.5f, 13.5f, 20.0f, 20.0f, 1.5f);
        break;
    case 13: // bun
        circle(12.0f, 13.0f, 7.5f);
        line(9.5f, 5.5f, 12.0f, 8.0f);
        line(14.5f, 5.5f, 12.0f, 8.0f);
        fillCircle(9.5f, 13.0f, 1.2f);
        fillCircle(14.5f, 13.0f, 1.2f);
        break;
    case 14: // deno
        circle(12.0f, 12.0f, 8.5f);
        fillCircle(10.0f, 9.5f, 1.3f);
        line(13.0f, 9.0f, 16.5f, 17.5f);
        break;
    case 15: // python
        box(7.0f, 3.5f, 16.5f, 12.0f, 3.0f);
        box(7.5f, 12.0f, 17.0f, 20.5f, 3.0f);
        box(3.5f, 7.5f, 12.0f, 16.0f, 3.0f);
        box(12.0f, 8.0f, 20.5f, 16.5f, 3.0f);
        break;
    case 16: // rust_gear
        circle(12.0f, 12.0f, 6.8f);
        circle(12.0f, 12.0f, 2.8f);
        line(12.0f, 2.5f, 12.0f, 5.0f);
        line(12.0f, 19.0f, 12.0f, 21.5f);
        line(2.5f, 12.0f, 5.0f, 12.0f);
        line(19.0f, 12.0f, 21.5f, 12.0f);
        line(5.3f, 5.3f, 7.0f, 7.0f);
        line(17.0f, 17.0f, 18.7f, 18.7f);
        line(18.7f, 5.3f, 17.0f, 7.0f);
        line(7.0f, 17.0f, 5.3f, 18.7f);
        break;
    case 17: // go_gopher
        box(4.5f, 5.5f, 19.5f, 19.5f, 6.0f);
        circle(9.5f, 10.5f, 2.0f);
        circle(14.5f, 10.5f, 2.0f);
        line(10.5f, 15.0f, 13.5f, 15.0f);
        break;
    case 18: // zig_bolt
        line(4.5f, 6.0f, 19.5f, 6.0f);
        line(19.5f, 6.0f, 4.5f, 18.0f);
        line(4.5f, 18.0f, 19.5f, 18.0f);
        line(13.5f, 3.5f, 10.5f, 20.5f);
        break;
    case 19: // dotnet
        fillCircle(5.0f, 17.5f, 1.8f);
        line(9.0f, 18.0f, 9.0f, 6.5f);
        line(9.0f, 6.5f, 16.5f, 18.0f);
        line(16.5f, 18.0f, 16.5f, 6.5f);
        line(20.0f, 6.5f, 20.0f, 18.0f);
        break;
    case 20: // java_cup
        box(5.0f, 10.0f, 16.0f, 19.0f, 3.0f);
        line(16.0f, 11.5f, 19.5f, 11.5f);
        line(19.5f, 11.5f, 19.5f, 16.0f);
        line(19.5f, 16.0f, 16.0f, 16.0f);
        thinLine(8.5f, 4.5f, 8.5f, 8.0f);
        thinLine(12.5f, 3.5f, 12.5f, 8.0f);
        break;
    case 21: // c_lang
        line(17.5f, 7.0f, 10.0f, 5.0f);
        line(10.0f, 5.0f, 5.5f, 9.5f);
        line(5.5f, 9.5f, 5.5f, 14.5f);
        line(5.5f, 14.5f, 10.0f, 19.0f);
        line(10.0f, 19.0f, 17.5f, 17.0f);
        break;
    case 22: // cpp_lang
        line(13.0f, 6.5f, 7.5f, 5.5f);
        line(7.5f, 5.5f, 4.0f, 9.5f);
        line(4.0f, 9.5f, 4.0f, 14.5f);
        line(4.0f, 14.5f, 7.5f, 18.5f);
        line(7.5f, 18.5f, 13.0f, 17.5f);
        thinLine(13.5f, 12.0f, 17.5f, 12.0f);
        thinLine(15.5f, 10.0f, 15.5f, 14.0f);
        thinLine(18.5f, 12.0f, 22.0f, 12.0f);
        thinLine(20.25f, 10.0f, 20.25f, 14.0f);
        break;
    case 23: // ai_sparkle (Claude / Gemini / Aider / Ollama)
        line(12.0f, 2.5f, 12.0f, 21.5f);
        line(2.5f, 12.0f, 21.5f, 12.0f);
        line(5.5f, 5.5f, 18.5f, 18.5f);
        line(18.5f, 5.5f, 5.5f, 18.5f);
        fillCircle(12.0f, 12.0f, 2.5f);
        break;
    case 24: // openai_knot
        circle(12.0f, 12.0f, 7.8f);
        circle(12.0f, 12.0f, 3.4f);
        thinLine(12.0f, 4.2f, 18.5f, 8.0f);
        thinLine(18.5f, 8.0f, 18.5f, 16.0f);
        thinLine(18.5f, 16.0f, 12.0f, 19.8f);
        thinLine(12.0f, 19.8f, 5.5f, 16.0f);
        thinLine(5.5f, 16.0f, 5.5f, 8.0f);
        thinLine(5.5f, 8.0f, 12.0f, 4.2f);
        break;
    case 25: // copilot
        box(4.0f, 8.5f, 20.0f, 17.5f, 4.0f);
        circle(9.0f, 13.0f, 1.8f);
        circle(15.0f, 13.0f, 1.8f);
        line(7.0f, 8.5f, 9.5f, 5.0f);
        line(17.0f, 8.5f, 14.5f, 5.0f);
        line(9.5f, 5.0f, 14.5f, 5.0f);
        break;
    case 26: // cursor_arrow
        line(5.5f, 4.0f, 19.5f, 11.5f);
        line(19.5f, 11.5f, 12.5f, 13.5f);
        line(12.5f, 13.5f, 9.5f, 20.5f);
        line(9.5f, 20.5f, 5.5f, 4.0f);
        break;
    case 27: // activity_pulse (Lucide activity: btop / htop)
        line(2.5f, 12.0f, 6.5f, 12.0f);
        line(6.5f, 12.0f, 9.5f, 4.0f);
        line(9.5f, 4.0f, 14.5f, 20.0f);
        line(14.5f, 20.0f, 17.5f, 12.0f);
        line(17.5f, 12.0f, 21.5f, 12.0f);
        break;
    case 28: // database (Lucide database)
        box(4.5f, 4.0f, 19.5f, 20.0f, 4.0f);
        line(4.5f, 9.5f, 19.5f, 9.5f);
        line(4.5f, 14.8f, 19.5f, 14.8f);
        break;
    case 29: // globe (Lucide globe: curl / wget / httpie)
        circle(12.0f, 12.0f, 8.5f);
        thinLine(3.5f, 12.0f, 20.5f, 12.0f);
        thinLine(12.0f, 3.5f, 12.0f, 20.5f);
        rt->DrawEllipse(D2D1::Ellipse(pt(12.0f, 12.0f), 4.2f * s, 8.5f * s), b, sw * 0.8f, roundStrokeStyle);
        break;
    case 30: // shield_key (Lucide shield: ssh / gpg)
        line(12.0f, 3.0f, 19.5f, 6.0f);
        line(19.5f, 6.0f, 19.5f, 12.5f);
        line(19.5f, 12.5f, 12.0f, 21.0f);
        line(12.0f, 21.0f, 4.5f, 12.5f);
        line(4.5f, 12.5f, 4.5f, 6.0f);
        line(4.5f, 6.0f, 12.0f, 3.0f);
        circle(12.0f, 11.0f, 2.0f);
        line(12.0f, 13.0f, 12.0f, 16.5f);
        break;
    case 31: // package_box (Lucide package: winget / scoop / brew)
        line(12.0f, 2.8f, 20.0f, 7.2f);
        line(20.0f, 7.2f, 20.0f, 16.8f);
        line(20.0f, 16.8f, 12.0f, 21.2f);
        line(12.0f, 21.2f, 4.0f, 16.8f);
        line(4.0f, 16.8f, 4.0f, 7.2f);
        line(4.0f, 7.2f, 12.0f, 2.8f);
        thinLine(4.0f, 7.2f, 12.0f, 11.8f);
        thinLine(20.0f, 7.2f, 12.0f, 11.8f);
        thinLine(12.0f, 11.8f, 12.0f, 21.2f);
        break;
    case 32: // cloud (Lucide cloud: aws / gcloud / az / flyctl / vercel)
        box(4.0f, 10.5f, 20.0f, 19.0f, 4.2f);
        circle(10.5f, 10.0f, 4.2f);
        circle(15.2f, 11.5f, 3.2f);
        break;
    case 33: // layers (Lucide layers: terraform / ansible / helm)
        line(12.0f, 3.5f, 20.5f, 8.0f);
        line(20.5f, 8.0f, 12.0f, 12.5f);
        line(12.0f, 12.5f, 3.5f, 8.0f);
        line(3.5f, 8.0f, 12.0f, 3.5f);
        line(3.5f, 12.5f, 12.0f, 17.0f);
        line(12.0f, 17.0f, 20.5f, 12.5f);
        line(3.5f, 16.5f, 12.0f, 21.0f);
        line(12.0f, 21.0f, 20.5f, 16.5f);
        break;
    case 34: // ubuntu_circle
        circle(12.0f, 12.0f, 5.5f);
        fillCircle(12.0f, 3.8f, 2.1f);
        fillCircle(5.0f, 16.2f, 2.1f);
        fillCircle(19.0f, 16.2f, 2.1f);
        break;
    case 35: // arch_peak
        line(12.0f, 3.0f, 4.0f, 20.0f);
        line(12.0f, 3.0f, 20.0f, 20.0f);
        line(4.0f, 20.0f, 9.0f, 17.5f);
        line(20.0f, 20.0f, 15.0f, 17.5f);
        circle(12.0f, 16.5f, 2.8f);
        break;
    case 36: // debian_swirl
        circle(12.0f, 11.5f, 6.5f);
        line(12.0f, 18.0f, 16.5f, 20.5f);
        break;
    case 37: // linux_penguin
        circle(12.0f, 7.5f, 3.8f);
        box(7.0f, 10.5f, 17.0f, 20.5f, 4.5f);
        line(7.0f, 12.0f, 4.0f, 16.5f);
        line(17.0f, 12.0f, 20.0f, 16.5f);
        break;
    case 38: // apple
        circle(10.0f, 13.5f, 5.5f);
        circle(14.0f, 13.5f, 5.5f);
        line(12.0f, 7.5f, 15.0f, 3.5f);
        break;
    case 39: // android
        box(5.0f, 9.0f, 19.0f, 19.5f, 5.0f);
        line(8.0f, 9.0f, 5.5f, 4.5f);
        line(16.0f, 9.0f, 18.5f, 4.5f);
        fillCircle(9.5f, 13.5f, 1.3f);
        fillCircle(14.5f, 13.5f, 1.3f);
        break;
    case 40: // vite_bolt
        line(4.5f, 5.0f, 12.0f, 20.5f);
        line(19.5f, 5.0f, 12.0f, 20.5f);
        line(13.5f, 4.0f, 10.0f, 11.5f);
        line(10.0f, 11.5f, 14.5f, 11.5f);
        line(14.5f, 11.5f, 11.0f, 18.0f);
        break;
    case 41: // next_n
        circle(12.0f, 12.0f, 8.5f);
        line(8.5f, 16.0f, 8.5f, 8.0f);
        line(8.5f, 8.0f, 16.5f, 18.0f);
        line(15.5f, 8.0f, 15.5f, 13.5f);
        break;
    case 42: // react_atom
        fillCircle(12.0f, 12.0f, 2.2f);
        rt->DrawEllipse(D2D1::Ellipse(pt(12.0f, 12.0f), 9.0f * s, 3.8f * s), b, sw * 0.85f, roundStrokeStyle);
        rt->DrawEllipse(D2D1::Ellipse(pt(12.0f, 12.0f), 3.8f * s, 9.0f * s), b, sw * 0.85f, roundStrokeStyle);
        break;
    case 43: // vue_v
        line(3.5f, 5.0f, 12.0f, 20.0f);
        line(20.5f, 5.0f, 12.0f, 20.0f);
        line(8.0f, 5.0f, 12.0f, 12.5f);
        line(16.0f, 5.0f, 12.0f, 12.5f);
        break;
    case 44: // svelte_s
        box(6.5f, 3.5f, 18.5f, 12.5f, 4.0f);
        box(5.5f, 11.5f, 17.5f, 20.5f, 4.0f);
        break;
    case 45: // angular_shield
        line(12.0f, 3.0f, 20.0f, 6.0f);
        line(20.0f, 6.0f, 18.0f, 16.5f);
        line(18.0f, 16.5f, 12.0f, 21.0f);
        line(12.0f, 21.0f, 6.0f, 16.5f);
        line(6.0f, 16.5f, 4.0f, 6.0f);
        line(4.0f, 6.0f, 12.0f, 3.0f);
        line(12.0f, 7.5f, 8.5f, 16.0f);
        line(12.0f, 7.5f, 15.5f, 16.0f);
        thinLine(9.5f, 13.5f, 14.5f, 13.5f);
        break;
    case 46: // tailwind_wave
        line(4.0f, 10.5f, 8.5f, 6.5f);
        line(8.5f, 6.5f, 14.0f, 10.5f);
        line(14.0f, 10.5f, 19.5f, 6.5f);
        line(4.5f, 17.0f, 9.0f, 13.0f);
        line(9.0f, 13.0f, 14.5f, 17.0f);
        line(14.5f, 17.0f, 20.0f, 13.0f);
        break;
    case 47: // lua_orbit
        circle(11.0f, 13.0f, 6.5f);
        fillCircle(13.5f, 10.5f, 1.8f);
        fillCircle(18.5f, 5.5f, 2.2f);
        break;
    case 48: // ruby_gem
        line(7.0f, 4.5f, 17.0f, 4.5f);
        line(17.0f, 4.5f, 21.0f, 9.5f);
        line(21.0f, 9.5f, 12.0f, 20.0f);
        line(12.0f, 20.0f, 3.0f, 9.5f);
        line(3.0f, 9.5f, 7.0f, 4.5f);
        thinLine(3.0f, 9.5f, 21.0f, 9.5f);
        break;
    case 49: // php_oval
        rt->DrawEllipse(D2D1::Ellipse(pt(12.0f, 12.0f), 9.5f * s, 6.0f * s), b, sw, roundStrokeStyle);
        line(9.0f, 9.5f, 7.5f, 14.5f);
        line(12.5f, 9.5f, 11.0f, 14.5f);
        line(16.0f, 9.5f, 14.5f, 14.5f);
        break;
    case 50: // elixir_drop
        line(12.0f, 3.0f, 18.0f, 13.0f);
        line(12.0f, 3.0f, 6.0f, 13.0f);
        circle(12.0f, 14.8f, 6.0f);
        break;
    case 51: // search_lens (Lucide search: rg / fd / fzf / grep)
        circle(10.5f, 10.5f, 6.2f);
        line(15.2f, 15.2f, 20.5f, 20.5f);
        break;
    case 52: // wrench_tool (Lucide wrench: make / cmake / ninja / just)
        circle(15.5f, 8.5f, 4.5f);
        line(12.2f, 11.8f, 4.5f, 19.5f);
        line(4.5f, 19.5f, 7.0f, 20.5f);
        break;
    case 53: // folder_tree (Lucide folder: yazi / ranger / lf)
        box(3.0f, 6.5f, 21.0f, 19.5f, 2.5f);
        line(3.0f, 6.5f, 9.5f, 6.5f);
        line(9.5f, 6.5f, 11.5f, 9.0f);
        line(11.5f, 9.0f, 21.0f, 9.0f);
        break;
    case 54: // layout_grid (Lucide layout-grid: tmux / zellij)
        box(3.5f, 3.5f, 10.5f, 10.5f, 1.8f);
        box(13.5f, 3.5f, 20.5f, 10.5f, 1.8f);
        box(3.5f, 13.5f, 10.5f, 20.5f, 1.8f);
        box(13.5f, 13.5f, 20.5f, 20.5f, 1.8f);
        break;
    case 55: // film_media (Lucide play: ffmpeg / mpv / yt-dlp)
        box(3.0f, 4.0f, 21.0f, 20.0f, 3.0f);
        line(9.5f, 8.0f, 16.5f, 12.0f);
        line(16.5f, 12.0f, 9.5f, 16.0f);
        line(9.5f, 16.0f, 9.5f, 8.0f);
        break;
    default: // 1: terminal_prompt (>_ Lucide terminal)
        line(4.5f, 7.0f, 10.5f, 12.0f);
        line(10.5f, 12.0f, 4.5f, 17.0f);
        line(12.0f, 17.0f, 19.5f, 17.0f);
        break;
    }
}

static void drawCachedAppIcon(VexAppIcon icon, float x, float y, float sizeDip) {
    if (!target || !brush) return;
    const uint32_t sizeCode = (uint32_t)lroundf(sizeDip);
    const uint64_t key = computeIconCacheKey(icon, sizeCode);
    for (size_t i = 0; i < iconCacheCount; i++) {
        if (iconCache[i].key == key && iconCache[i].bitmap) {
            target->DrawBitmap(iconCache[i].bitmap, D2D1::RectF(x, y, x + sizeDip, y + sizeDip), 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            return;
        }
    }
    ID2D1BitmapRenderTarget* bmpTarget = nullptr;
    if (SUCCEEDED(target->CreateCompatibleRenderTarget(D2D1::SizeF(sizeDip, sizeDip), &bmpTarget)) && bmpTarget) {
        ID2D1SolidColorBrush* bmpBrush = nullptr;
        ID2D1Bitmap* bitmap = nullptr;
        if (SUCCEEDED(bmpTarget->CreateSolidColorBrush(rgb(icon.color_rgb ? icon.color_rgb : 0x8f9197), &bmpBrush)) && bmpBrush) {
            bmpTarget->BeginDraw();
            bmpTarget->Clear(D2D1::ColorF(0, 0.0f));
            drawAppIconContent(bmpTarget, bmpBrush, icon, 0.0f, 0.0f, sizeDip);
            if (SUCCEEDED(bmpTarget->EndDraw())) {
                bmpTarget->GetBitmap(&bitmap);
            }
        }
        releaseCom(bmpBrush);
        releaseCom(bmpTarget);
        if (bitmap) {
            size_t slot = 0;
            if (iconCacheCount < 64) {
                slot = iconCacheCount++;
            } else {
                slot = iconCacheClock;
                iconCacheClock = (iconCacheClock + 1) % 64;
                releaseCom(iconCache[slot].bitmap);
            }
            iconCache[slot].key = key;
            iconCache[slot].bitmap = bitmap;
            target->DrawBitmap(bitmap, D2D1::RectF(x, y, x + sizeDip, y + sizeDip), 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            return;
        }
    }
    drawAppIconContent(target, brush, icon, x, y, sizeDip);
}

static void drawProgramStatus(VexProgramStatus status, float x, float y, float width, bool iconOnly) {
    if (status.state < 2 || status.state > 5) return;
    const wchar_t* icon = L"\x25CC";
    const wchar_t* text = L"Working";
    uint32_t color = 0x8f9197;
    uint32_t pillFill = 0x1f222a;
    uint32_t pillBorder = 0x323642;
    switch (status.state) {
        case 3:
            icon = L"\x2713"; text = L"Done"; color = 0x87d9b7;
            pillFill = 0x162925; pillBorder = 0x264a40;
            break;
        case 4:
            icon = status.kind == 1 ? L"\x25C7" : status.kind == 3 ? L"\x21AA" : L"?";
            text = status.kind == 1 ? L"Needs permission" : status.kind == 3 ? L"Needs sign-in" : L"Needs input";
            color = 0xf2cd83;
            pillFill = 0x2b261c; pillBorder = 0x4d4128;
            break;
        case 5:
            icon = L"!"; text = L"Error"; color = 0xeb8b97;
            pillFill = 0x2b1c21; pillBorder = 0x4d2b34;
            break;
    }
    if (iconOnly) { label(icon, x, y, width, color); return; }
    wchar_t caption[64];
    if (status.progress >= 0 && status.progress <= 100 && (status.state == 2 || status.state == 4))
        swprintf(caption, 64, L"%ls %ls %d%%", icon, text, status.progress);
    else swprintf(caption, 64, L"%ls %ls", icon, text);
    const D2D1_ROUNDED_RECT pill = D2D1::RoundedRect(D2D1::RectF(x, y + 1.0f, x + width, y + 19.0f), 4.0f, 4.0f);
    brush->SetColor(rgb(pillFill));
    target->FillRoundedRectangle(pill, brush);
    brush->SetColor(rgb(pillBorder));
    target->DrawRoundedRectangle(pill, brush, 1.0f);
    labelWithFont(badgeFont, caption, x + 6.0f, y + 2.5f, fmaxf(0.0f, width - 10.0f), color);
}
static wchar_t shellName[64] = L"Command Prompt";
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
extern "C" int vex_window_project_info(const uint8_t* name, size_t name_length, const uint8_t* branch, size_t branch_length) {
    if ((name_length && !name) || name_length > 128 || (branch_length && !branch) || branch_length > 128) return ERROR_INVALID_PARAMETER;
    if (name_length > 0 && !memchr(name, 0, name_length)) {
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(name), (int)name_length, projectName, 63);
        if (count > 0) projectName[count] = 0;
    } else {
        wcscpy_s(projectName, L"Vex");
    }
    if (branch_length > 0 && !memchr(branch, 0, branch_length)) {
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(branch), (int)branch_length, projectBranch, 63);
        if (count > 0) projectBranch[count] = 0;
        else projectBranch[0] = 0;
    } else {
        projectBranch[0] = 0;
    }
    return 0;
}
extern "C" void vex_draw_tab(uint32_t index, uint32_t count, bool active, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention) {
    if (count != tabStripCount || index >= count) return;
    float width = tabWidthDip, x = 140 + index * (width + 4);
    const bool isHovered = (int)index == hoveredTabIndex;
    if (active) {
        brush->SetColor(rgb(0x25213d));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 7, x + width, 37), 5, 5), brush);
        brush->SetColor(rgb(0x41439a));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x + 0.5f, 7.5f, x + width - 0.5f, 36.5f), 4.5f, 4.5f), brush, 1.0f);
    } else if (isHovered) {
        brush->SetColor(rgb(0x1f2229));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 7, x + width, 37), 5, 5), brush);
    }
    float inset = 8.0f;
    if (width >= 68.0f && icons && icon_count > 0) {
        const size_t maxVisible = width >= 120.0f ? 3 : 1;
        const size_t visible = icon_count > maxVisible ? maxVisible : icon_count;
        const uint32_t tabBg = active ? 0x25213d : (isHovered ? 0x1f2229 : 0x181a1f);
        for (size_t i = 0; i < visible; i++) {
            const float tx = x + 7.0f + (float)i * 10.0f;
            const float ty = 13.0f;
            if (i > 0) {
                brush->SetColor(rgb(tabBg));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(tx - 1.0f, ty - 1.0f, tx + 19.0f, ty + 19.0f), 4.5f, 4.5f), brush);
            }
            brush->SetColor(rgb(0x20232b));
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(tx, ty, tx + 18.0f, ty + 18.0f), 4.0f, 4.0f), brush);
            brush->SetColor(rgb(0x2e323d));
            target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(tx + 0.5f, ty + 0.5f, tx + 17.5f, ty + 17.5f), 3.5f, 3.5f), brush, 1.0f);
            drawCachedAppIcon(icons[i], tx + 3.0f, ty + 3.0f, 12.0f);
        }
        inset = 7.0f + 18.0f + (float)(visible - 1) * 10.0f + 5.0f;
        if (icon_count > visible && width >= 132.0f) {
            const float bx = x + inset - 2.0f;
            const float by = 14.0f;
            brush->SetColor(rgb(0x2a2e39));
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, by, bx + 18.0f, by + 16.0f), 3.5f, 3.5f), brush);
            wchar_t plusText[8];
            swprintf(plusText, 8, L"+%u", (unsigned)(icon_count - visible));
            labelWithFont(badgeFont, plusText, bx + 2.5f, by + 1.0f, 16.0f, 0x9da1aa);
            inset += 20.0f;
        }
    }
    bool hasStatus = status.state >= 2 && status.state <= 5;
    float rightReserve = 24.0f;
    if (hasStatus && width >= 88.0f) {
        drawProgramStatus(status, x + width - 36.0f, 13.0f, 14.0f, true);
        rightReserve = 38.0f;
    } else if (!active && attention) {
        brush->SetColor(rgb(0xf2cd83));
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + width - 28.0f, 22.0f), 2.5f, 2.5f), brush);
        rightReserve = 34.0f;
    }
    if (width >= 95.0f) {
        labelUtf8OrDefault(title, title_length, x + inset, 13.0f, fmaxf(0.0f, width - inset - rightReserve), active ? 0xe4e4e7 : 0x8f9197);
    } else if (width < 68.0f) {
        wchar_t number[12]; swprintf(number, 12, L"%u", index + 1);
        label(number, x + 8.0f, 13.0f, fmaxf(0.0f, width - 22.0f), active ? 0xe4e4e7 : 0x8f9197);
    }
    const float closeX = x + width - 21.0f, closeY = 14.0f;
    const bool closeHot = isHovered && hoveredTabClose;
    if (closeHot) {
        brush->SetColor(rgb(0x353944));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(closeX, closeY, closeX + 16.0f, closeY + 16.0f), 4.0f, 4.0f), brush);
    }
    const float cx = closeX + 8.0f, cy = closeY + 8.0f;
    brush->SetColor(rgb(closeHot ? 0xe4e4e7 : 0x8f9197));
    target->DrawLine(D2D1::Point2F(cx - 3.0f, cy - 3.0f), D2D1::Point2F(cx + 3.0f, cy + 3.0f), brush, 1.15f);
    target->DrawLine(D2D1::Point2F(cx + 3.0f, cy - 3.0f), D2D1::Point2F(cx - 3.0f, cy + 3.0f), brush, 1.15f);
}
static void drawPaneHeaderActions(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action) {
    if (bounds.width < 180.0f || bounds.height < 28.0f) return;
    const float right = bounds.x + bounds.width - 6.0f;
    const float top = bounds.y + 4.0f;
    const uint32_t baseColor = (focused || hovered) ? 0x9da1aa : 0x5c6068;
    const auto drawButtonPill = [&](float bx, uint32_t fill) {
        brush->SetColor(rgb(fill));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, top, bx + 20.0f, top + 20.0f), 4.0f, 4.0f), brush);
    };
    if (can_focus) {
        const float bx = right - 86.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (zoomed) drawButtonPill(bx, hovered_action == 1 ? 0x3b3e6b : 0x2d2f52);
        else if (hovered_action == 1) drawButtonPill(bx, 0x353944);
        brush->SetColor(rgb(zoomed ? 0x9ea2ff : (hovered_action == 1 ? 0xe4e4e7 : baseColor)));
        if (!zoomed) {
            target->DrawLine(D2D1::Point2F(cx + 1.0f, cy - 1.0f), D2D1::Point2F(cx + 4.5f, cy - 4.5f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx + 1.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy - 4.5f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy - 1.5f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx - 1.0f, cy + 1.0f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx - 1.5f, cy + 4.5f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx - 4.5f, cy + 4.5f), D2D1::Point2F(cx - 4.5f, cy + 1.5f), brush, 1.15f);
        } else {
            target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx + 1.0f, cy - 1.0f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx + 1.0f, cy - 4.0f), D2D1::Point2F(cx + 1.0f, cy - 1.0f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx + 1.0f, cy - 1.0f), D2D1::Point2F(cx + 4.0f, cy - 1.0f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx - 4.5f, cy + 4.5f), D2D1::Point2F(cx - 1.0f, cy + 1.0f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx - 1.0f, cy + 4.0f), D2D1::Point2F(cx - 1.0f, cy + 1.0f), brush, 1.15f);
            target->DrawLine(D2D1::Point2F(cx - 1.0f, cy + 1.0f), D2D1::Point2F(cx - 4.0f, cy + 1.0f), brush, 1.15f);
        }
    }
    {
        const float bx = right - 64.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 2) drawButtonPill(bx, 0x353944);
        brush->SetColor(rgb(hovered_action == 2 ? 0xe4e4e7 : baseColor));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), 1.5f, 1.5f), brush, 1.1f);
        target->DrawLine(D2D1::Point2F(cx, cy - 4.5f), D2D1::Point2F(cx, cy + 4.5f), brush, 1.1f);
    }
    {
        const float bx = right - 42.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 3) drawButtonPill(bx, 0x353944);
        brush->SetColor(rgb(hovered_action == 3 ? 0xe4e4e7 : baseColor));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), 1.5f, 1.5f), brush, 1.1f);
        target->DrawLine(D2D1::Point2F(cx - 4.5f, cy), D2D1::Point2F(cx + 4.5f, cy), brush, 1.1f);
    }
    {
        const float bx = right - 20.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 4) drawButtonPill(bx, 0x4d2b31);
        brush->SetColor(rgb(hovered_action == 4 ? 0xffb4be : baseColor));
        target->DrawLine(D2D1::Point2F(cx - 3.5f, cy - 3.5f), D2D1::Point2F(cx + 3.5f, cy + 3.5f), brush, 1.2f);
        target->DrawLine(D2D1::Point2F(cx + 3.5f, cy - 3.5f), D2D1::Point2F(cx - 3.5f, cy + 3.5f), brush, 1.2f);
    }
}
extern "C" void vex_begin_pane(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action, const uint8_t* title, size_t title_length, VexAppIcon icon, VexProgramStatus status) {
    rectangle(bounds.x, bounds.y, bounds.width, bounds.height, 0x282c34);
    if (bounds.height >= 28.0f) {
        rectangle(bounds.x + 1.0f, bounds.y + 1.0f, fmaxf(0, bounds.width - 2.0f), 26.0f, 0x21252b);
        rectangle(bounds.x + 1.0f, bounds.y + 27.0f, fmaxf(0, bounds.width - 2.0f), 1.0f, 0x333842);
    }
    brush->SetColor(rgb(focused ? 0x41439a : (hovered ? 0x525660 : 0x484b52)));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x + 0.5f, bounds.y + 0.5f, bounds.x + bounds.width - 0.5f, bounds.y + bounds.height - 0.5f), 6, 6), brush, 1);
    drawPaneHeaderActions(bounds, focused, hovered, can_focus, zoomed, hovered_action);
    const float actionsWidth = (bounds.width >= 180.0f && bounds.height >= 28.0f) ? (can_focus ? 94.0f : 72.0f) : 0.0f;
    const float remainingWidth = fmaxf(0, bounds.width - actionsWidth);
    const float statusWidth = status.state >= 2 && status.state <= 5 ? fminf(remainingWidth < 260 ? 20 : 124, fmaxf(0, remainingWidth - 20)) : 0;
    if (statusWidth > 0) drawProgramStatus(status, bounds.x + remainingWidth - statusWidth - 8, bounds.y + 4, statusWidth, remainingWidth < 260);
    float titleInset = 10.0f;
    if (bounds.width >= 90.0f && bounds.height >= 28.0f) {
        VexAppIcon headerIcon = icon;
        if (headerIcon.kind == 0) {
            headerIcon.kind = 1;
            headerIcon.glyph = 1;
            headerIcon.color_rgb = focused ? 0x8286f5 : 0x6c7079;
        }
        drawCachedAppIcon(headerIcon, bounds.x + 8.0f, bounds.y + 7.0f, 14.0f);
        titleInset = 26.0f;
    }
    labelUtf8OrDefault(title, title_length, bounds.x + titleInset, bounds.y + 5.0f, fmaxf(0, remainingWidth - statusWidth - titleInset - 8), focused ? 0xe4e4e7 : 0x8f9197);
    gridLeft = bounds.x + 6; gridTop = bounds.y + 30;
    target->PushAxisAlignedClip(D2D1::RectF(gridLeft, gridTop, fmaxf(gridLeft, bounds.x + bounds.width - 6), fmaxf(gridTop, bounds.y + bounds.height - 6)), D2D1_ANTIALIAS_MODE_ALIASED);
}
extern "C" void vex_end_pane() { target->PopAxisAlignedClip(); }
extern "C" void vex_draw_scrollbar(VexPaneRect bounds, float thumb_y, float thumb_height, bool active) {
    if (!isfinite(thumb_y) || !isfinite(thumb_height) || thumb_height <= 0.0f || bounds.width < 24.0f) return;
    const float width = active ? 10.0f : 6.0f;
    const float right = bounds.x + bounds.width - 2.0f;
    const float left = right - width;
    const float radius = width * 0.5f;
    brush->SetColor(D2D1::ColorF(0xe4e4e7, active ? 0.62f : 0.32f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(left, thumb_y, right, thumb_y + thumb_height), radius, radius), brush);
}
extern "C" int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags) {
    float x = gridLeft + column * cellWidth, y = gridTop + row * cellHeight, width = cells * cellWidth;
    rectangle(x, y, width, cellHeight, background);
    if (!length || (flags & 16)) return 0;
    if (!utf8 || length > 65536) return ERROR_INVALID_PARAMETER;
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)utf8, (int)length, nullptr, 0);
    if (count <= 0) return (int)GetLastError();
    wchar_t scratch[4096];
    auto text = count <= 4096 ? scratch : static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, (size_t)count * sizeof(wchar_t)));
    if (!text) return ERROR_OUTOFMEMORY;
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)utf8, (int)length, text, count)) {
        DWORD error = GetLastError(); if (text != scratch) HeapFree(GetProcessHeap(), 0, text); return (int)error;
    }
    brush->SetColor(rgb(foreground));
    target->DrawText(text, count, terminalFonts[flags & 3], D2D1::RectF(x, y, x + width, y + cellHeight), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    if (text != scratch) HeapFree(GetProcessHeap(), 0, text);
    if (flags & 4) rectangle(x, y + cellHeight - 2, width, 1, foreground);
    if (flags & 8) rectangle(x, y + cellHeight * 0.55f, width, 1, foreground);
    return 0;
}
extern "C" void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color) {
    float x = gridLeft + column * cellWidth, y = gridTop + row * cellHeight;
    if (style == 0) rectangle(x, y, 1.5f, cellHeight, color);
    else if (style == 2) rectangle(x, y + cellHeight - 2, cellWidth, 2, color);
    else { brush->SetColor(rgb(color)); target->DrawRectangle(D2D1::RectF(x + 0.5f, y + 0.5f, x + cellWidth - 0.5f, y + cellHeight - 0.5f), brush); }
}
extern "C" void vex_window_error(const char* message) {
    MessageBoxA(IsWindow(window) ? window : nullptr, message, "Vex native rewrite", MB_OK | MB_ICONERROR);
    if (IsWindow(window)) PostMessageW(window, WM_CLOSE, 0, 0);
}
extern "C" int vex_machine_name(uint8_t* bytes, size_t capacity, size_t* length) {
    if (!bytes || !length || capacity > INT_MAX) return ERROR_INVALID_PARAMETER;
    wchar_t name[MAX_COMPUTERNAME_LENGTH + 1]; DWORD units = MAX_COMPUTERNAME_LENGTH + 1;
    if (!GetComputerNameW(name, &units)) return (int)GetLastError();
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, (int)units, (char*)bytes, (int)capacity, nullptr, nullptr);
    if (!count) return (int)GetLastError();
    *length = (size_t)count;
    return 0;
}
extern "C" int vex_system_directory(uint8_t* bytes, size_t capacity, size_t* length) {
    if (!bytes || !length || capacity > INT_MAX) return ERROR_INVALID_PARAMETER;
    wchar_t path[MAX_PATH]; UINT units = GetSystemDirectoryW(path, MAX_PATH);
    if (!units) return (int)GetLastError();
    if (units >= MAX_PATH) return ERROR_INSUFFICIENT_BUFFER;
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path, (int)units, (char*)bytes, (int)capacity, nullptr, nullptr);
    if (!count) return (int)GetLastError();
    *length = (size_t)count;
    return 0;
}
extern "C" int vex_clipboard_write(const uint8_t* bytes, size_t length) {
    if (!bytes || !length || length > 768 * 1024 - 4) return ERROR_INVALID_PARAMETER;
    int units = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)bytes, (int)length, nullptr, 0);
    if (units <= 0) return (int)GetLastError();
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, ((size_t)units + 1) * sizeof(wchar_t));
    if (!memory) return ERROR_OUTOFMEMORY;
    auto wide = static_cast<wchar_t*>(GlobalLock(memory));
    if (!wide) { DWORD error = GetLastError(); GlobalFree(memory); return (int)(error ? error : ERROR_OUTOFMEMORY); }
    int written = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)bytes, (int)length, wide, units);
    wide[units] = 0;
    GlobalUnlock(memory);
    if (written != units) { GlobalFree(memory); return ERROR_NO_UNICODE_TRANSLATION; }
    if (!OpenClipboard(IsWindow(window) ? window : nullptr)) { GlobalFree(memory); return 0; }
    if (!EmptyClipboard() || !SetClipboardData(CF_UNICODETEXT, memory)) GlobalFree(memory);
    CloseClipboard();
    return 0;
}
extern "C" void* vex_terminal_symbol(const char* name) {
    static HMODULE library;
    if (!library) {
        wchar_t path[32768]; DWORD length = GetModuleFileNameW(nullptr, path, 32768);
        if (!length || length >= 32768) return nullptr;
        wchar_t* slash = wcsrchr(path, L'\\');
        if (!slash || (slash - path) + 16 >= 32768) return nullptr;
        wcscpy(slash + 1, L"ghostty-vt.dll");
        library = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    }
    return library ? reinterpret_cast<void*>(GetProcAddress(library, name)) : nullptr;
}
