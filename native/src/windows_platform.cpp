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
static float cellWidth, cellHeight, clientWidth, clientHeight, gridLeft, gridTop;
static uint32_t drawnTabCount = 1;
static bool frameScheduled, firstOutput, firstFrame;
static ULONGLONG frameDeadline;
static int platformError;
static LARGE_INTEGER startupCounter, counterFrequency;
static uint16_t pendingSurrogate;
static uint64_t paintCount;

HWND vex_native_window() { return window; }

template<class T> static void releaseCom(T*& object) {
    if (object) { object->Release(); object = nullptr; }
}
static D2D1_COLOR_F rgb(uint32_t color) {
    return D2D1::ColorF((color >> 16) / 255.0f, ((color >> 8) & 255) / 255.0f, (color & 255) / 255.0f);
}
static void rectangle(float x, float y, float width, float height, uint32_t color) {
    brush->SetColor(rgb(color));
    target->FillRectangle(D2D1::RectF(x, y, x + width, y + height), brush);
}
static void label(const wchar_t* text, float x, float y, float width, uint32_t color) {
    brush->SetColor(rgb(color));
    target->DrawText(text, (UINT32)wcslen(text), chromeFont, D2D1::RectF(x, y, x + width, y + 24), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
}
static float tabWidth(uint32_t count) { return fminf(160, fmaxf(24, (clientWidth - 140 - 120 - 38) / count - 4)); }
static float plusLeft() { return 140 + drawnTabCount * (tabWidth(drawnTabCount) + 4); }

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
    if (!frameScheduled) {
        frameDeadline = GetTickCount64() + 8;
        if (!SetTimer(window, 1, 8, nullptr)) { platformError = (int)GetLastError(); DestroyWindow(window); }
        else frameScheduled = true;
    } else if (GetTickCount64() >= frameDeadline) renderScheduledFrame();
}
extern "C" void vex_window_close() { if (IsWindow(window)) PostMessageW(window, WM_CLOSE, 0, 0); }
extern "C" uint64_t vex_window_paint_count() { return paintCount; }
static void drawWindow() {
    HRESULT result = createRenderTarget();
    if (FAILED(result)) { platformError = (int)result; DestroyWindow(window); return; }
    target->BeginDraw();
    paintCount++;
    target->Clear(rgb(0x0f1114));
    brush->SetColor(rgb(0x9b3bb9));
    target->DrawRectangle(D2D1::RectF(16, 17, 29, 27), brush, 1);
    target->DrawLine(D2D1::Point2F(16, 17), D2D1::Point2F(16, 14), brush, 1);
    target->DrawLine(D2D1::Point2F(16, 14), D2D1::Point2F(22, 14), brush, 1);
    label(L"Vex", 38, 12, 85, 0xe4e4e7);
    rectangle(128, 12, 1, 20, 0x60636a);
    callbacks.paint(callbacks.context);
    label(L"+", plusLeft() + 8, 12, 24, drawnTabCount < 8 ? 0xe4e4e7 : 0x60636a);
    label(L"\x2014", clientWidth - 112, 12, 24, 0xe4e4e7);
    label(IsZoomed(window) ? L"\x2750" : L"\x25A1", clientWidth - 72, 12, 24, 0xe4e4e7);
    label(L"\x00D7", clientWidth - 32, 12, 24, 0xe4e4e7);
    result = target->EndDraw();
    if (result == D2DERR_RECREATE_TARGET) {
        releaseCom(brush); releaseCom(target); InvalidateRect(window, nullptr, FALSE);
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
                            if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, (int)units, (char*)bytes, size, nullptr, nullptr)) callbacks.text(callbacks.context, bytes, size);
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
static bool handleKey(uint32_t key) {
    uint32_t mods = (GetKeyState(VK_SHIFT) < 0 ? 1u : 0u) | (GetKeyState(VK_CONTROL) < 0 ? 2u : 0u) | (GetKeyState(VK_MENU) < 0 ? 4u : 0u);
    if (key == 'V' && mods == 3) { pasteClipboard(); return true; }
    return callbacks.key(callbacks.context, key, mods);
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
            POINT point; GetCursorPos(&point); ScreenToClient(hwnd, &point);
            SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(point.y < 74 * GetDpiForWindow(hwnd) / 96.0f ? 32512 : 32513)));
            return TRUE;
        }
        break;
    case WM_LBUTTONDOWN: case WM_MBUTTONDOWN: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (y < 44 && x >= 140 && x < plusLeft()) {
            uint32_t index = (uint32_t)((x - 140) / (tabWidth(drawnTabCount) + 4));
            bool close = message == WM_MBUTTONDOWN || x > 140 + index * (tabWidth(drawnTabCount) + 4) + tabWidth(drawnTabCount) - 22;
            if (index < drawnTabCount) callbacks.action(callbacks.context, close ? 7 : 6, index);
        } else if (y < 44 && x >= plusLeft() && x < plusLeft() + 32) callbacks.action(callbacks.context, 0, 0);
        else if (y >= 44) callbacks.click(callbacks.context, x, y);
        return 0;
    }
    case WM_LBUTTONUP: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (y < 44 && x >= clientWidth - 120) {
            if (x >= clientWidth - 40) SendMessageW(hwnd, WM_CLOSE, 0, 0);
            else if (x >= clientWidth - 80) ShowWindow(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
            else ShowWindow(hwnd, SW_MINIMIZE);
        }
        return 0;
    }
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED && window) {
            if (target) {
                HRESULT result = target->Resize(D2D1::SizeU(LOWORD(lparam), HIWORD(lparam)));
                if (result == D2DERR_RECREATE_TARGET) { releaseCom(brush); releaseCom(target); }
                else if (FAILED(result)) { platformError = (int)result; DestroyWindow(hwnd); return 0; }
            }
            updateWindowMetrics(); InvalidateRect(hwnd, nullptr, FALSE);
        }
        return 0;
    case WM_DPICHANGED: {
        auto bounds = reinterpret_cast<RECT*>(lparam);
        if (target) target->SetDpi((float)HIWORD(wparam), (float)HIWORD(wparam));
        SetWindowPos(hwnd, nullptr, bounds->left, bounds->top, bounds->right - bounds->left, bounds->bottom - bounds->top, SWP_NOZORDER | SWP_NOACTIVATE);
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
    case WM_TIMER: renderScheduledFrame(); return 0;
    case WM_KEYDOWN: case WM_SYSKEYDOWN: return handleKey((uint32_t)wparam) ? 1 : DefWindowProcW(hwnd, message, wparam, lparam);
    case WM_KILLFOCUS: pendingSurrogate = 0; return 0;
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
    case WM_MOUSEWHEEL: callbacks.scroll(callbacks.context, -(GET_WHEEL_DELTA_WPARAM(wparam) / WHEEL_DELTA) * 3); vex_window_invalidate(); return 0;
    case WM_CLOSE: DestroyWindow(hwnd); return 0;
    case WM_DESTROY: KillTimer(hwnd, 1); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

extern "C" void vex_startup_begin() { QueryPerformanceCounter(&startupCounter); QueryPerformanceFrequency(&counterFrequency); }
extern "C" int vex_window_run(VexWindowCallbacks value) {
    callbacks = value;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HRESULT result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &drawingFactory);
    if (FAILED(result)) return (int)result;
    result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)&textFactory);
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 13, L"en-us", &chromeFont);
    if (SUCCEEDED(result)) result = chromeFont->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    if (SUCCEEDED(result)) {
        IDWriteInlineObject* ellipsis = nullptr;
        result = textFactory->CreateEllipsisTrimmingSign(chromeFont, &ellipsis);
        DWRITE_TRIMMING trimming = {}; trimming.granularity = DWRITE_TRIMMING_GRANULARITY_CHARACTER;
        if (SUCCEEDED(result)) result = chromeFont->SetTrimming(&trimming, ellipsis);
        releaseCom(ellipsis);
    }
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
    if (SUCCEEDED(result)) {
        WNDCLASSEXW klass = {};
        klass.cbSize = sizeof(klass); klass.lpfnWndProc = windowProcedure;
        klass.hInstance = GetModuleHandleW(nullptr); klass.lpszClassName = L"VexNativeWorkspace";
        klass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32513));
        if (!RegisterClassExW(&klass)) platformError = (int)GetLastError();
        else {
            const float initialScale = GetDpiForSystem() / 96.0f;
            window = CreateWindowExW(0, klass.lpszClassName, L"Vex — native rewrite", WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, (int)(1120 * initialScale), (int)(700 * initialScale), nullptr, nullptr, klass.hInstance, nullptr);
            if (!window) platformError = (int)GetLastError();
            else {
                BOOL dark = TRUE; DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
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
    releaseCom(brush); releaseCom(target); releaseCom(chromeFont);
    for (auto& font : terminalFonts) releaseCom(font);
    releaseCom(textFactory); releaseCom(drawingFactory);
    return platformError;
}
extern "C" void vex_draw_tab(uint32_t index, uint32_t count, bool active) {
    if (!count || count > 8 || index >= count) return;
    drawnTabCount = count;
    float width = tabWidth(count), x = 140 + index * (width + 4);
    if (active) {
        brush->SetColor(rgb(0x25213d));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 7, x + width, 37), 5, 5), brush);
    }
    if (width >= 95) label(L">_  Command Prompt", x + 10, 12, width - 34, active ? 0xe4e4e7 : 0x8f9197);
    else {
        wchar_t number[12]; swprintf(number, 12, L"%u", index + 1);
        label(number, x + 8, 12, width - 22, active ? 0xe4e4e7 : 0x8f9197);
    }
    label(L"\x00D7", x + width - 19, 12, 16, 0x8f9197);
}
extern "C" void vex_begin_pane(VexPaneRect bounds, bool focused) {
    rectangle(bounds.x, bounds.y, bounds.width, bounds.height, 0x282c34);
    brush->SetColor(rgb(focused ? 0x41439a : 0x484b52));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bounds.x + 0.5f, bounds.y + 0.5f, bounds.x + bounds.width - 0.5f, bounds.y + bounds.height - 0.5f), 6, 6), brush, 1);
    label(L">_  Command Prompt", bounds.x + 10, bounds.y + 2, fmaxf(0, bounds.width - 20), focused ? 0xe4e4e7 : 0x8f9197);
    gridLeft = bounds.x + 6; gridTop = bounds.y + 30;
    target->PushAxisAlignedClip(D2D1::RectF(gridLeft, gridTop, fmaxf(gridLeft, bounds.x + bounds.width - 6), fmaxf(gridTop, bounds.y + bounds.height - 6)), D2D1_ANTIALIAS_MODE_ALIASED);
}
extern "C" void vex_end_pane() { target->PopAxisAlignedClip(); }
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
