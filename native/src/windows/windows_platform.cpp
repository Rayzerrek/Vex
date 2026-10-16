#include "windows_platform.h"
#include "windows_renderer.h"
#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <d2d1.h>
#include <dwrite.h>
#include <dwmapi.h>
#include <math.h>
#include <stdio.h>

static VexWindowCallbacks callbacks;
static HWND window;
static ID2D1Factory1* drawingFactory;
static IDWriteFactory* textFactory;
static WindowsRenderer renderer;
static uint64_t rendererGeneration, rendererDebugMessages;
static unsigned rendererRecoveryAttempts;
static bool rendererResetOnNextResize;
static ID2D1RenderTarget* target;
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
static uint64_t activeTabIconKey, focusedPaneIconKey;
static float cellWidth, cellHeight, clientWidth, clientHeight, gridLeft, gridTop;
static float terminalFontSize = 14.0f;
static float dpiScale = 1.0f;
static uint32_t tabStripCount = 1;
static float tabWidthDip = 160;
static bool frameScheduled, firstOutput, firstFrame;
static bool cursorWakeArmed;
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
static bool hoveredShellPicker;
static int hoveredTabIndex = -1;
static bool hoveredTabClose;
static bool trackingMouseLeave;
enum class PointerCaptureState { none, armed, dragging, cancelled };
static PointerCaptureState pointerCaptureState = PointerCaptureState::none;
static VexChromePalette chromePalette = {
    0x080a0d, 0x0b0d11, 0x2f323a, 0x0d0f14, 0x0a0c10,
    0x1e2128, 0x514998, 0x171a20,
    0xe6e9f0, 0x898d98, 0x8b7cff, 0x48dbfb,
    true
};

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
    rectangle(x - 1, 7, 2, 30, chromePalette.accent);
    return 0;
}
extern "C" void vex_window_set_chrome_palette(VexChromePalette palette) {
    bool modeChanged = chromePalette.is_dark != palette.is_dark;
    chromePalette = palette;
    if (modeChanged && IsWindow(window)) {
        BOOL dark = palette.is_dark ? TRUE : FALSE;
        DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
    }
    vex_window_invalidate();
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
static HRESULT releaseRenderer() {
    clearIconCache(); releaseCom(brush); target = nullptr;
    rendererDebugMessages += renderer.debugErrorCount();
    return renderer.shutdown();
}
static void rendererFault(HRESULT result) {
    if (WindowsRenderer::deviceLost(result) && rendererRecoveryAttempts < 3) {
        rendererRecoveryAttempts++;
        HRESULT shutdown = releaseRenderer();
        if (SUCCEEDED(shutdown) || WindowsRenderer::deviceLost(shutdown)) {
            InvalidateRect(window, nullptr, FALSE);
            return;
        }
        result = shutdown;
    }
    platformError = (int)result;
    DestroyWindow(window);
}
static HRESULT createRenderTarget() {
    if (!renderer.ready()) {
        wchar_t warp[2];
        const bool forceWarp = selftestEnabled() && GetEnvironmentVariableW(L"VEX_NATIVE_WARP", warp, 2) == 1 && warp[0] == L'1';
        HRESULT result = renderer.initialize(window, drawingFactory, forceWarp);
        if (FAILED(result)) return result;
        rendererGeneration++;
    }
    target = renderer.drawingTarget();
    if (!brush) return target->CreateSolidColorBrush(rgb(0xe4e4e7), &brush);
    return S_OK;
}
static HRESULT resizeRenderer(UINT width, UINT height, float dpi) {
    if (rendererResetOnNextResize) {
        rendererResetOnNextResize = false;
        return DXGI_ERROR_DEVICE_RESET;
    }
    return renderer.resize(width, height, dpi);
}
static void updateWindowMetrics() {
    RECT bounds; GetClientRect(window, &bounds);
    float scale = GetDpiForWindow(window) / 96.0f;
    dpiScale = scale > 0.0f ? scale : 1.0f;
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
extern "C" bool vex_window_cursor_wake_armed() { return cursorWakeArmed; }
extern "C" int vex_window_schedule_cursor_wake(uint32_t delay_ms) {
    if (delay_ms > 60000) return ERROR_INVALID_PARAMETER;
    if (cursorWakeArmed && IsWindow(window)) KillTimer(window, 3);
    cursorWakeArmed = false;
    if (!delay_ms) return 0;
    if (!IsWindow(window)) return ERROR_INVALID_WINDOW_HANDLE;
    if (!SetTimer(window, 3, delay_ms, nullptr)) {
        DWORD error = GetLastError();
        return (int)(error ? error : ERROR_NOT_ENOUGH_MEMORY);
    }
    cursorWakeArmed = true;
    return 0;
}

static void drawProjectPill() {
    const D2D1_ROUNDED_RECT pill = D2D1::RoundedRect(D2D1::RectF(8.0f, 7.0f, 134.0f, 37.0f), 6.0f, 6.0f);
    brush->SetColor(rgb(chromePalette.active_tab_bg));
    target->FillRoundedRectangle(pill, brush);
    brush->SetColor(rgb(chromePalette.active_tab_border));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(8.5f, 7.5f, 133.5f, 36.5f), 5.5f, 5.5f), brush, 1.0f);
    // Lucide Folder glyph on the left of the project pill.
    brush->SetColor(rgb(chromePalette.accent));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(15.5f, 18.0f, 28.5f, 27.5f), 1.5f, 1.5f), brush, 1.25f);
    target->DrawLine(D2D1::Point2F(15.5f, 18.5f), D2D1::Point2F(15.5f, 15.5f), brush, 1.25f);
    target->DrawLine(D2D1::Point2F(15.5f, 15.5f), D2D1::Point2F(20.5f, 15.5f), brush, 1.25f);
    target->DrawLine(D2D1::Point2F(20.5f, 15.5f), D2D1::Point2F(22.0f, 18.0f), brush, 1.25f);
    if (projectBranch[0] != 0) {
        labelWithFont(chromeBoldFont, projectName, 34.0f, 8.5f, 82.0f, chromePalette.text_primary);
        labelWithFont(badgeFont, projectBranch, 34.0f, 22.0f, 82.0f, chromePalette.git_branch);
    } else {
        labelWithFont(chromeBoldFont, projectName, 34.0f, 13.0f, 82.0f, chromePalette.text_primary);
    }
    brush->SetColor(rgb(chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(119.5f, 20.5f), D2D1::Point2F(122.5f, 23.5f), brush, 1.2f);
    target->DrawLine(D2D1::Point2F(122.5f, 23.5f), D2D1::Point2F(125.5f, 20.5f), brush, 1.2f);
}

static void drawNewTabButton() {
    const float bx = plusLeft() + 1.0f;
    const float by = 8.0f;
    const bool enabled = tabStripCount < 8;
    if (enabled && hoveredNewTab) {
        brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, by, bx + 22.0f, by + 28.0f), 5.0f, 5.0f), brush);
    }
    const float cx = bx + 11.0f;
    const float cy = by + 14.0f;
    brush->SetColor(rgb(enabled ? (hoveredNewTab ? chromePalette.accent : chromePalette.text_primary) : chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(cx - 4.5f, cy), D2D1::Point2F(cx + 4.5f, cy), brush, 1.35f);
    target->DrawLine(D2D1::Point2F(cx, cy - 4.5f), D2D1::Point2F(cx, cy + 4.5f), brush, 1.35f);

    const float sx = plusLeft() + 23.0f;
    if (hoveredShellPicker) {
        brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(sx, by, sx + 15.0f, by + 28.0f), 4.0f, 4.0f), brush);
    }
    const float scx = sx + 7.5f;
    brush->SetColor(rgb(hoveredShellPicker ? chromePalette.accent : chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(scx - 3.0f, cy - 1.5f), D2D1::Point2F(scx, cy + 1.8f), brush, 1.25f, roundStrokeStyle);
    target->DrawLine(D2D1::Point2F(scx, cy + 1.8f), D2D1::Point2F(scx + 3.0f, cy - 1.5f), brush, 1.25f, roundStrokeStyle);
}

static void drawCaptionButtons() {
    const float baseLeft = clientWidth - 120.0f;
    // Minimize button (button 1)
    {
        const float left = baseLeft;
        const bool hot = pressedCaptionButton == 1 || hoveredCaptionButton == 1;
        if (hot) {
            rectangle(left, 0.0f, 40.0f, 43.0f, pressedCaptionButton == 1 ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg);
        }
        const float cx = left + 20.0f, cy = 21.5f;
        brush->SetColor(rgb(hot ? chromePalette.text_primary : chromePalette.text_muted));
        target->DrawLine(D2D1::Point2F(cx - 5.0f, cy), D2D1::Point2F(cx + 5.0f, cy), brush, 1.3f, roundStrokeStyle);
    }
    // Maximize / Restore button (button 2)
    {
        const float left = baseLeft + 40.0f;
        const bool hot = pressedCaptionButton == 2 || hoveredCaptionButton == 2;
        if (hot) {
            rectangle(left, 0.0f, 40.0f, 43.0f, pressedCaptionButton == 2 ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg);
        }
        const float cx = left + 20.0f, cy = 21.5f;
        brush->SetColor(rgb(hot ? chromePalette.text_primary : chromePalette.text_muted));
        if (IsZoomed(window)) {
            target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 5.0f, cy - 2.0f, cx + 2.2f, cy + 5.0f), 1.6f, 1.6f), brush, 1.25f, roundStrokeStyle);
            target->DrawLine(D2D1::Point2F(cx - 2.2f, cy - 2.0f), D2D1::Point2F(cx - 2.2f, cy - 4.8f), brush, 1.25f, roundStrokeStyle);
            target->DrawLine(D2D1::Point2F(cx - 2.2f, cy - 4.8f), D2D1::Point2F(cx + 4.8f, cy - 4.8f), brush, 1.25f, roundStrokeStyle);
            target->DrawLine(D2D1::Point2F(cx + 4.8f, cy - 4.8f), D2D1::Point2F(cx + 4.8f, cy + 2.2f), brush, 1.25f, roundStrokeStyle);
            target->DrawLine(D2D1::Point2F(cx + 2.2f, cy + 2.2f), D2D1::Point2F(cx + 4.8f, cy + 2.2f), brush, 1.25f, roundStrokeStyle);
        } else {
            target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.8f, cy - 4.8f, cx + 4.8f, cy + 4.8f), 1.8f, 1.8f), brush, 1.25f, roundStrokeStyle);
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
        brush->SetColor(rgb(hot ? 0xffffff : chromePalette.text_muted));
        target->DrawLine(D2D1::Point2F(cx - 4.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy + 4.5f), brush, 1.35f, roundStrokeStyle);
        target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.35f, roundStrokeStyle);
    }
}

static void renderScene() {
    activeTabIconKey = 0; focusedPaneIconKey = 0;
    target->Clear(rgb(chromePalette.pane_bg));
    rectangle(0.0f, 0.0f, clientWidth, 44.0f, chromePalette.titlebar_bg);
    rectangle(0.0f, 43.0f, clientWidth, 1.0f, chromePalette.border);
    drawProjectPill();
    drawNewTabButton();
    callbacks.paint(callbacks.context);
    drawCaptionButtons();
}

static bool drawWindow(void* pixels = nullptr, UINT stride = 0) {
    RECT bounds;
    if (!IsWindow(window) || IsIconic(window) || !GetClientRect(window, &bounds) || bounds.right <= 0 || bounds.bottom <= 0) return false;
    HRESULT result = createRenderTarget();
    if (SUCCEEDED(result)) result = renderer.beginFrame();
    if (FAILED(result)) { rendererFault(result); return false; }
    paintCount++;
    renderScene();
    result = renderer.endFrame(pixels, stride);
    if (FAILED(result)) { rendererFault(result); return false; }
    rendererRecoveryAttempts = 0;
    if (SUCCEEDED(result) && !firstFrame) { firstFrame = true; startupMilestone("first frame"); }
    return SUCCEEDED(result);
}

static bool renderToMemoryBmp(int& outWidth, int& outHeight, HDC& outMemDc, HBITMAP& outDib, HGDIOBJ& outOldObj, void*& outBits) {
    outWidth = 0; outHeight = 0; outMemDc = nullptr; outDib = nullptr; outOldObj = nullptr; outBits = nullptr;
    if (!drawingFactory || !window) return false;
    RECT bounds; GetClientRect(window, &bounds);
    const int width = bounds.right - bounds.left;
    const int height = bounds.bottom - bounds.top;
    if (width <= 0 || height <= 0) return false;
    const float dpi = (float)GetDpiForWindow(window);
    dpiScale = dpi > 0.0f ? (dpi / 96.0f) : 1.0f;

    HDC memDc = CreateCompatibleDC(nullptr);
    if (!memDc) return false;
    BITMAPINFO bmi = {};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = width;
    bmi.bmiHeader.biHeight = height; // bottom-up DIB for standard BMP
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(memDc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dib || !bits) {
        if (dib) DeleteObject(dib);
        DeleteDC(memDc);
        return false;
    }
    HGDIOBJ oldObj = SelectObject(memDc, dib);

    // Read back the real D3D12-backed scene, not a separate software-rendered approximation.
    if (!drawWindow(bits, (UINT)width * 4)) {
        SelectObject(memDc, oldObj);
        DeleteObject(dib);
        DeleteDC(memDc);
        return false;
    }
    outWidth = width; outHeight = height; outMemDc = memDc; outDib = dib; outOldObj = oldObj; outBits = bits;
    return true;
}

static void drawToDc(HDC hdc) {
    if (!hdc) return;
    int width = 0, height = 0;
    HDC memDc = nullptr; HBITMAP dib = nullptr; HGDIOBJ oldObj = nullptr; void* bits = nullptr;
    if (!renderToMemoryBmp(width, height, memDc, dib, oldObj, bits)) return;
    BitBlt(hdc, 0, 0, width, height, memDc, 0, 0, SRCCOPY);
    SelectObject(memDc, oldObj);
    DeleteObject(dib);
    DeleteDC(memDc);
}

static bool saveCaptureBmp() {
    if (!drawingFactory || !window) return false;
    RECT bounds;
    if (!GetClientRect(window, &bounds)) return false;
    const int width = bounds.right - bounds.left;
    const int height = bounds.bottom - bounds.top;
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return false;
    const float dpi = (float)GetDpiForWindow(window);
    dpiScale = dpi > 0.0f ? (dpi / 96.0f) : 1.0f;
    const DWORD pixelBytes = (DWORD)(width * height * 4);
    void* bits = HeapAlloc(GetProcessHeap(), 0, pixelBytes);
    if (!bits) return false;
    if (!drawWindow(bits, (UINT)width * 4)) {
        HeapFree(GetProcessHeap(), 0, bits);
        return false;
    }

    wchar_t path[32768] = {};
    DWORD envLen = GetEnvironmentVariableW(L"VEX_NATIVE_CAPTURE_BMP", path, 32768);
    if (!envLen || envLen >= 32768) {
        DWORD modLen = GetModuleFileNameW(nullptr, path, 32768);
        wchar_t* slash = modLen ? wcsrchr(path, L'\\') : nullptr;
        if (slash) {
            *slash = 0;
            wchar_t* parentSlash = wcsrchr(path, L'\\');
            if (parentSlash && (parentSlash - path) + 32 < 32768) {
                wcscpy(parentSlash + 1, L"verification\\.capture.bmp");
            }
        }
    }

    bool saved = false;
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        BITMAPFILEHEADER bfh = {};
        bfh.bfType = 0x4D42; // 'BM'
        bfh.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
        bfh.bfSize = bfh.bfOffBits + pixelBytes;
        BITMAPINFOHEADER bih = {};
        bih.biSize = sizeof(BITMAPINFOHEADER);
        bih.biWidth = width;
        bih.biHeight = height;
        bih.biPlanes = 1;
        bih.biBitCount = 32;
        bih.biCompression = BI_RGB;
        bih.biSizeImage = pixelBytes;
        DWORD written1 = 0, written2 = 0, written3 = 0;
        if (WriteFile(file, &bfh, sizeof(bfh), &written1, nullptr) &&
            WriteFile(file, &bih, sizeof(bih), &written2, nullptr) &&
            WriteFile(file, bits, pixelBytes, &written3, nullptr)) {
            saved = (written1 == sizeof(bfh) && written2 == sizeof(bih) && written3 == pixelBytes);
        }
        CloseHandle(file);
    }
    HeapFree(GetProcessHeap(), 0, bits);
    return saved;
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
        response == VEX_POINTER_LINK ? 32649 :
        response == VEX_POINTER_SELECTION ? 32513 :
        response == VEX_POINTER_TAB_ARMED || response == VEX_POINTER_TAB_DRAGGING || y < 74 ? 32512 : 32513;
    SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(resource)));
}
static void refreshHoverCursor(HWND hwnd) {
    if (pointerCaptureState != PointerCaptureState::none) return;
    POINT point;
    if (!GetCursorPos(&point) || !ScreenToClient(hwnd, &point)) return;
    RECT bounds;
    if (!GetClientRect(hwnd, &bounds) || point.x < 0 || point.y < 0 || point.x >= bounds.right || point.y >= bounds.bottom) return;
    float scale = GetDpiForWindow(hwnd) / 96.0f;
    float x = point.x / scale, y = point.y / scale;
    setPointerCursor(callbacks.pointer(callbacks.context, VEX_POINTER_HOVER, x, y, currentModifiers()), y);
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
    bool nextShellPicker = false;
    int nextTab = -1;
    bool nextTabClose = false;
    if (pointerCaptureState == PointerCaptureState::none && y >= 0.0f && y < 44.0f) {
        if (x >= clientWidth - 120.0f && x < clientWidth) {
            nextCaption = (x >= clientWidth - 40.0f) ? 3 : (x >= clientWidth - 80.0f) ? 2 : 1;
        } else if (x >= plusLeft() && x < plusLeft() + 23.0f && y >= 7.0f && y <= 37.0f) {
            nextNewTab = true;
        } else if (x >= plusLeft() + 23.0f && x < plusLeft() + 38.0f && y >= 7.0f && y <= 37.0f) {
            nextShellPicker = true;
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
        nextShellPicker != hoveredShellPicker || nextTab != hoveredTabIndex || nextTabClose != hoveredTabClose) {
        hoveredCaptionButton = nextCaption;
        hoveredNewTab = nextNewTab;
        hoveredShellPicker = nextShellPicker;
        hoveredTabIndex = nextTab;
        hoveredTabClose = nextTabClose;
        vex_window_invalidate();
    }
}
static LRESULT CALLBACK windowProcedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_NCCALCSIZE: {
        if (wparam && IsZoomed(hwnd)) {
            auto params = reinterpret_cast<NCCALCSIZE_PARAMS*>(lparam);
            UINT dpi = GetDpiForWindow(hwnd);
            int frame = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            params->rgrc[0].left += frame;
            params->rgrc[0].top += frame;
            params->rgrc[0].right -= frame;
            params->rgrc[0].bottom -= frame;
        }
        return 0;
    }
    case WM_NCACTIVATE:
        return DefWindowProcW(hwnd, message, wparam, -1);
    case WM_NCHITTEST: {
        POINT point = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        ScreenToClient(hwnd, &point);
        RECT bounds; GetClientRect(hwnd, &bounds);
        if (point.x < 0 || point.y < 0 || point.x >= bounds.right || point.y >= bounds.bottom) {
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
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
        if (point.y / scale < 44 && x < clientWidth - 120 && (x < 140 || x > plusLeft() + 38)) return HTCAPTION;
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
    case WM_LBUTTONDBLCLK: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (y < 44 && x >= 140 && x < plusLeft()) {
            uint32_t index = (uint32_t)((x - 140) / (tabWidthDip + 4));
            float local = x - 140 - index * (tabWidthDip + 4);
            if (index < tabStripCount && local <= tabWidthDip - 22) {
                cancelPointerGesture();
                pointerCaptureState = PointerCaptureState::none;
                callbacks.action(callbacks.context, 8, index);
                return 0;
            }
        }
        [[fallthrough]];
    }
    case WM_LBUTTONDOWN: case WM_MBUTTONDOWN: {
        cancelPointerGesture();
        pointerCaptureState = PointerCaptureState::none;
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (message != WM_MBUTTONDOWN && y < 44 && x >= clientWidth - 120) {
            pressedCaptionButton = (x >= clientWidth - 40) ? 3 : (x >= clientWidth - 80) ? 2 : 1;
            vex_window_invalidate();
            return 0;
        }
        if (y < 44 && x >= 140 && x < plusLeft()) {
            uint32_t index = (uint32_t)((x - 140) / (tabWidthDip + 4));
            float local = x - 140 - index * (tabWidthDip + 4);
            if (index >= tabStripCount || local >= tabWidthDip) return 0;
            bool close = message == WM_MBUTTONDOWN || local > tabWidthDip - 22;
            if (close) callbacks.action(callbacks.context, 16, index);
            else beginPointerGesture(hwnd, x, y);
        } else if (y < 44 && x >= plusLeft() && x < plusLeft() + 23) callbacks.action(callbacks.context, 0, 0);
        else if (y < 44 && x >= plusLeft() + 23 && x < plusLeft() + 38) callbacks.action(callbacks.context, 18, 0);
        else if (y >= 44) {
            if (message != WM_MBUTTONDOWN) {
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
        else if (response == VEX_POINTER_NORMAL || response == VEX_POINTER_LINK) cancelPointerGesture();
        updateTopChromeHover(x, y);
        setPointerCursor(response, y);
        return 0;
    }
    case WM_MOUSELEAVE:
        trackingMouseLeave = false;
        if (hoveredCaptionButton != 0 || pressedCaptionButton != 0 || hoveredNewTab || hoveredShellPicker || hoveredTabIndex != -1 || hoveredTabClose) {
            hoveredCaptionButton = 0;
            pressedCaptionButton = 0;
            hoveredNewTab = false;
            hoveredShellPicker = false;
            hoveredTabIndex = -1;
            hoveredTabClose = false;
            vex_window_invalidate();
        }
        return 0;
    case WM_CAPTURECHANGED: case WM_CANCELMODE:
        cancelPointerGesture();
        return 0;
    case WM_SIZE:
        callbacks.cursor_activity(callbacks.context, wparam != SIZE_MINIMIZED && IsWindowVisible(hwnd) && GetFocus() == hwnd);
        if (wparam != SIZE_MINIMIZED && window) {
            RECT bounds;
            if (!GetClientRect(hwnd, &bounds) || bounds.right <= 0 || bounds.bottom <= 0) return 0;
            if (renderer.ready()) {
                HRESULT result = resizeRenderer((UINT)bounds.right, (UINT)bounds.bottom, (float)GetDpiForWindow(hwnd));
                if (FAILED(result)) {
                    rendererFault(result);
                    if (!IsWindow(hwnd)) return 0;
                }
            }
            updateWindowMetrics(); InvalidateRect(hwnd, nullptr, FALSE);
        }
        return 0;
    case WM_DPICHANGED: {
        cancelPointerGesture();
        clearIconCache();
        auto bounds = reinterpret_cast<RECT*>(lparam);
        if (!SetWindowPos(hwnd, nullptr, bounds->left, bounds->top, bounds->right - bounds->left, bounds->bottom - bounds->top, SWP_NOZORDER | SWP_NOACTIVATE)) {
            DWORD error = GetLastError();
            platformError = (int)(error ? error : ERROR_INVALID_WINDOW_HANDLE);
            DestroyWindow(hwnd);
            return 0;
        }
        // A DPI transition need not change physical bounds, so WM_SIZE alone cannot refresh DIP geometry.
        if (renderer.ready() && !IsIconic(hwnd)) {
            RECT client;
            if (!GetClientRect(hwnd, &client)) { rendererFault(HRESULT_FROM_WIN32(GetLastError())); return 0; }
            if (client.right > 0 && client.bottom > 0) {
                HRESULT result = resizeRenderer((UINT)client.right, (UINT)client.bottom, (float)HIWORD(wparam));
                if (FAILED(result)) {
                    rendererFault(result);
                    if (!IsWindow(hwnd)) return 0;
                }
            }
        }
        updateWindowMetrics(); InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    }
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: { PAINTSTRUCT paint; BeginPaint(hwnd, &paint); drawWindow(); EndPaint(hwnd, &paint); return 0; }
    case WM_PRINT:
    case WM_PRINTCLIENT:
        drawToDc((HDC)wparam);
        return 0;
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
        if (selftestEnabled() && wparam <= 18 && lparam >= 0 && (uint64_t)lparam <= UINT32_MAX) callbacks.action(callbacks.context, (uint32_t)wparam, (uint32_t)lparam);
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
    case WM_APP + 7:
        return (selftestEnabled() && saveCaptureBmp()) ? 1 : 0;
    case WM_APP + 8:
        if (!selftestEnabled() || lparam) return 0;
        switch (wparam) {
            case 0: return renderer.ready() ? 12 : 0;
            case 1: return renderer.software() ? 1 : 0;
            case 2: return (LRESULT)renderer.framesPresented();
            case 3: return (LRESULT)rendererGeneration;
            case 4: return renderer.width();
            case 5: return renderer.height();
            case 6: return (LRESULT)(rendererDebugMessages + renderer.debugErrorCount());
            case 7:
                // Exercise the production recovery path after draining a healthy device.
                rendererFault(DXGI_ERROR_DEVICE_RESET);
                return 1;
            case 8: rendererResetOnNextResize = true; return 1;
            case 9: return renderer.debugValidationEnabled() ? 1 : 0;
            case 10: return (LRESULT)renderer.terminal.drawCalls;
            case 11: return (LRESULT)renderer.terminal.glyphHits;
            case 12: return (LRESULT)renderer.terminal.glyphMisses;
            case 13: return (LRESULT)renderer.terminal.uploadAllocations;
            case 14: return (LRESULT)renderer.terminal.uploadCapacity();
            case 15: return (LRESULT)renderer.terminal.atlasResets;
            case 16: return TerminalRenderer::atlasSize * TerminalRenderer::atlasSize;
            case 17: return (LRESULT)renderer.terminal.uploadTrims;
            case 30: return (LRESULT)activeTabIconKey;
            case 31: return (LRESULT)focusedPaneIconKey;
            case 20: case 21: {
                UINT64 bytes = 0;
                return SUCCEEDED(renderer.videoMemoryUsage(wparam == 20, bytes)) ? (LRESULT)bytes : -1;
            }
        }
        return 0;
    case WM_TIMER:
        if (wparam == 3) {
            KillTimer(hwnd, 3); cursorWakeArmed = false;
            callbacks.cursor_wake(callbacks.context);
        } else if (wparam == 2) {
            HRESULT result = renderer.trimUnusedTerminalUploads();
            if (FAILED(result)) { rendererFault(result); return 0; }
            if (callbacks.tick) callbacks.tick(callbacks.context);
        } else if (wparam == 1) {
            renderScheduledFrame();
        }
        return 0;
    case WM_KEYDOWN: case WM_SYSKEYDOWN:
        if (wparam == VK_ESCAPE && (pointerCaptureState == PointerCaptureState::armed || pointerCaptureState == PointerCaptureState::dragging)) { cancelPointerGesture(); return 1; }
        if (wparam == VK_CONTROL || wparam == VK_LCONTROL || wparam == VK_RCONTROL) refreshHoverCursor(hwnd);
        return handleKey((uint32_t)wparam) ? 1 : DefWindowProcW(hwnd, message, wparam, lparam);
    case WM_KEYUP: case WM_SYSKEYUP:
        if (wparam == VK_CONTROL || wparam == VK_LCONTROL || wparam == VK_RCONTROL) refreshHoverCursor(hwnd);
        return DefWindowProcW(hwnd, message, wparam, lparam);
    case WM_SETFOCUS:
        callbacks.cursor_activity(callbacks.context, IsWindowVisible(hwnd) && !IsIconic(hwnd));
        return 0;
    case WM_SHOWWINDOW:
        callbacks.cursor_activity(callbacks.context, wparam && !IsIconic(hwnd) && GetFocus() == hwnd);
        break;
    case WM_KILLFOCUS:
        cancelPointerGesture(); pendingSurrogate = 0;
        callbacks.cursor_activity(callbacks.context, false);
        return 0;
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
    case WM_DESTROY: cancelPointerGesture(); KillTimer(hwnd, 1); KillTimer(hwnd, 2); KillTimer(hwnd, 3); cursorWakeArmed = false; PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

static HRESULT rebuildTerminalFonts(float fontSize) {
    if (!textFactory || !isfinite(fontSize)) return E_INVALIDARG;
    const float clamped = fminf(32.0f, fmaxf(9.0f, roundf(fontSize)));
    IDWriteTextFormat* newFonts[4] = {};
    HRESULT result = S_OK;
    for (int i = 0; i < 4 && SUCCEEDED(result); i++) {
        result = textFactory->CreateTextFormat(L"Cascadia Mono", nullptr, (i & 1) ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            (i & 2) ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, clamped, L"en-us", &newFonts[i]);
        if (SUCCEEDED(result)) result = newFonts[i]->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    }
    float newCellWidth = 0.0f, newCellHeight = 0.0f;
    if (SUCCEEDED(result)) {
        IDWriteTextLayout* measure = nullptr;
        result = textFactory->CreateTextLayout(L"M", 1, newFonts[0], 200, 200, &measure);
        if (SUCCEEDED(result)) {
            DWRITE_TEXT_METRICS metrics; DWRITE_LINE_METRICS line; UINT32 count;
            result = measure->GetMetrics(&metrics);
            if (SUCCEEDED(result)) result = measure->GetLineMetrics(&line, 1, &count);
            if (SUCCEEDED(result)) {
                newCellWidth = metrics.width;
                newCellHeight = ceilf(line.height);
                if (!isfinite(newCellWidth) || !isfinite(newCellHeight) || newCellWidth <= 0 || newCellHeight <= 0) result = E_INVALIDARG;
                for (int i = 0; i < 4 && SUCCEEDED(result); i++) result = newFonts[i]->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, newCellHeight, line.baseline);
            }
            releaseCom(measure);
        }
    }
    if (FAILED(result)) {
        for (auto& font : newFonts) releaseCom(font);
        return result;
    }
    renderer.terminal.clearGlyphCache();
    for (int i = 0; i < 4; i++) {
        releaseCom(terminalFonts[i]);
        terminalFonts[i] = newFonts[i];
    }
    terminalFontSize = clamped;
    cellWidth = newCellWidth;
    cellHeight = newCellHeight;
    return S_OK;
}
extern "C" int vex_window_set_font_size(float font_size) {
    HRESULT result = rebuildTerminalFonts(font_size);
    if (FAILED(result)) return (int)result;
    if (IsWindow(window)) {
        updateWindowMetrics();
        vex_window_invalidate();
    }
    return 0;
}
extern "C" float vex_window_font_size(void) { return terminalFontSize; }

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
    if (SUCCEEDED(result)) result = rebuildTerminalFonts(14.0f);
    HICON iconBig = nullptr, iconSmall = nullptr;
    loadAppIcons(iconBig, iconSmall);
    if (SUCCEEDED(result)) {
        WNDCLASSEXW klass = {};
        klass.cbSize = sizeof(klass); klass.style = CS_DBLCLKS; klass.lpfnWndProc = windowProcedure;
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
                MARGINS margins = { 1, 1, 1, 1 };
                DwmExtendFrameIntoClientArea(window, &margins);
                BOOL dark = TRUE; DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
                DWORD corner = 2; DwmSetWindowAttribute(window, 33, &corner, sizeof(corner));
                COLORREF border = 0xFFFFFFFE; DwmSetWindowAttribute(window, 34, &border, sizeof(border));
                SetWindowPos(window, nullptr, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                SetTimer(window, 2, 250, nullptr);
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
    HRESULT shutdown = releaseRenderer();
    if (FAILED(shutdown) && !WindowsRenderer::deviceLost(shutdown) && !platformError) platformError = (int)shutdown;
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

static uint64_t drawAppIconTile(VexAppIcon icon, float x, float y) {
    constexpr float glyphSize = 12.0f;
    brush->SetColor(rgb(chromePalette.pane_header_bg));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, y, x + 18.0f, y + 18.0f), 4.0f, 4.0f), brush);
    brush->SetColor(rgb(chromePalette.border));
    target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x + 0.5f, y + 0.5f, x + 17.5f, y + 17.5f), 3.5f, 3.5f), brush, 1.0f);
    drawCachedAppIcon(icon, x + 3.0f, y + 3.0f, glyphSize);
    return computeIconCacheKey(icon, (uint32_t)glyphSize);
}

static void drawProgramStatus(VexProgramStatus status, float x, float y, float width, bool iconOnly) {
    if (status.state < 2 || status.state > 5) return;
    const wchar_t* icon = L"\x25CC";
    const wchar_t* text = L"Working";
    const bool dark = chromePalette.is_dark;
    uint32_t color = chromePalette.text_muted;
    uint32_t pillFill = chromePalette.inactive_tab_hover_bg;
    uint32_t pillBorder = chromePalette.border;
    switch (status.state) {
        case 3:
            icon = L"\x2713"; text = L"Done"; color = dark ? 0x87d9b7 : 0x1c7c54;
            pillFill = dark ? 0x162925 : 0xdff5ec; pillBorder = dark ? 0x264a40 : 0x99d9bd;
            break;
        case 4:
            icon = status.kind == 1 ? L"\x25C7" : status.kind == 3 ? L"\x21AA" : L"?";
            text = status.kind == 1 ? L"Needs permission" : status.kind == 3 ? L"Needs sign-in" : L"Needs input";
            color = dark ? 0xf2cd83 : 0x9a6700;
            pillFill = dark ? 0x2b261c : 0xfdf3d7; pillBorder = dark ? 0x4d4128 : 0xe3c376;
            break;
        case 5:
            icon = L"!"; text = L"Error"; color = dark ? 0xeb8b97 : 0xb42336;
            pillFill = dark ? 0x2b1c21 : 0xfce6e9; pillBorder = dark ? 0x4d2b34 : 0xe8a5b0;
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
extern "C" void vex_draw_tab(uint32_t index, uint32_t count, bool active, bool editing, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention) {
    if (count != tabStripCount || index >= count) return;
    float width = tabWidthDip, x = 140 + index * (width + 4);
    const bool isHovered = (int)index == hoveredTabIndex;
    if (active) {
        brush->SetColor(rgb(chromePalette.active_tab_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 7, x + width, 37), 5, 5), brush);
        brush->SetColor(rgb(chromePalette.active_tab_border));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x + 0.5f, 7.5f, x + width - 0.5f, 36.5f), 4.5f, 4.5f), brush, 1.0f);
    } else if (isHovered) {
        brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(x, 7, x + width, 37), 5, 5), brush);
    }
    float inset = 8.0f;
    if (width >= 68.0f && icons && icon_count > 0) {
        const size_t maxVisible = width >= 120.0f ? 3 : 1;
        const size_t visible = icon_count > maxVisible ? maxVisible : icon_count;
        const uint32_t tabBg = active ? chromePalette.active_tab_bg : (isHovered ? chromePalette.inactive_tab_hover_bg : chromePalette.titlebar_bg);
        for (size_t i = 0; i < visible; i++) {
            const float tx = x + 7.0f + (float)i * 10.0f;
            const float ty = 13.0f;
            if (i > 0) {
                brush->SetColor(rgb(tabBg));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(tx - 1.0f, ty - 1.0f, tx + 19.0f, ty + 19.0f), 4.5f, 4.5f), brush);
            }
            const uint64_t iconKey = drawAppIconTile(icons[i], tx, ty);
            if (active && i == 0) activeTabIconKey = iconKey;
        }
        inset = 7.0f + 18.0f + (float)(visible - 1) * 10.0f + 5.0f;
        if (icon_count > visible && width >= 132.0f) {
            const float bx = x + inset - 2.0f;
            const float by = 14.0f;
            brush->SetColor(rgb(chromePalette.pane_header_bg));
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, by, bx + 18.0f, by + 16.0f), 3.5f, 3.5f), brush);
            wchar_t plusText[8];
            swprintf(plusText, 8, L"+%u", (unsigned)(icon_count - visible));
            labelWithFont(badgeFont, plusText, bx + 2.5f, by + 1.0f, 16.0f, chromePalette.text_muted);
            inset += 20.0f;
        }
    }
    bool hasStatus = status.state >= 2 && status.state <= 5;
    float rightReserve = 24.0f;
    if (hasStatus && width >= 88.0f) {
        drawProgramStatus(status, x + width - 36.0f, 13.0f, 14.0f, true);
        rightReserve = 38.0f;
    } else if (!active && attention) {
        brush->SetColor(rgb(chromePalette.accent));
        target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x + width - 28.0f, 22.0f), 2.5f, 2.5f), brush);
        rightReserve = 34.0f;
    }
    if (width >= 95.0f) {
        const float textW = fmaxf(0.0f, width - inset - rightReserve);
        if (editing && textW > 8.0f) {
            const D2D1_ROUNDED_RECT editBox = D2D1::RoundedRect(D2D1::RectF(x + inset - 3.0f, 10.5f, x + inset + textW + 2.0f, 33.5f), 3.5f, 3.5f);
            brush->SetColor(rgb(chromePalette.window_bg));
            target->FillRoundedRectangle(editBox, brush);
            brush->SetColor(rgb(chromePalette.accent));
            target->DrawRoundedRectangle(editBox, brush, 1.1f);
            if (title && title_length > 0 && title_length <= 256) {
                wchar_t text[257];
                int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(title), (int)title_length, text, 256);
                if (count > 0) {
                    text[count] = 0;
                    label(text, x + inset, 13.0f, textW, chromePalette.text_primary);
                }
            }
        } else {
            labelUtf8OrDefault(title, title_length, x + inset, 13.0f, textW, active ? chromePalette.text_primary : chromePalette.text_muted);
        }
    } else if (width < 68.0f) {
        wchar_t number[12]; swprintf(number, 12, L"%u", index + 1);
        label(number, x + 8.0f, 13.0f, fmaxf(0.0f, width - 22.0f), active ? chromePalette.text_primary : chromePalette.text_muted);
    }
    const float closeX = x + width - 21.0f, closeY = 14.0f;
    const bool closeHot = isHovered && hoveredTabClose;
    if (closeHot) {
        brush->SetColor(rgb(chromePalette.border));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(closeX, closeY, closeX + 16.0f, closeY + 16.0f), 4.0f, 4.0f), brush);
    }
    const float cx = closeX + 8.0f, cy = closeY + 8.0f;
    brush->SetColor(rgb(closeHot ? chromePalette.text_primary : chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(cx - 3.0f, cy - 3.0f), D2D1::Point2F(cx + 3.0f, cy + 3.0f), brush, 1.15f);
    target->DrawLine(D2D1::Point2F(cx + 3.0f, cy - 3.0f), D2D1::Point2F(cx - 3.0f, cy + 3.0f), brush, 1.15f);
}
static void drawPaneHeaderActions(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action) {
    if (bounds.width < 180.0f || bounds.height < 28.0f) return;
    const float right = bounds.x + bounds.width - 6.0f;
    const float top = bounds.y + 4.0f;
    const uint32_t baseColor = (focused || hovered) ? chromePalette.text_muted : chromePalette.border;
    const auto drawButtonPill = [&](float bx, uint32_t fill) {
        brush->SetColor(rgb(fill));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, top, bx + 20.0f, top + 20.0f), 4.0f, 4.0f), brush);
    };
    if (can_focus) {
        const float bx = right - 86.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (zoomed) drawButtonPill(bx, chromePalette.active_tab_bg);
        else if (hovered_action == 1) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg);
        brush->SetColor(rgb(zoomed ? chromePalette.accent : (hovered_action == 1 ? chromePalette.text_primary : baseColor)));
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
        if (hovered_action == 2) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg);
        brush->SetColor(rgb(hovered_action == 2 ? chromePalette.text_primary : baseColor));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), 1.5f, 1.5f), brush, 1.1f);
        target->DrawLine(D2D1::Point2F(cx, cy - 4.5f), D2D1::Point2F(cx, cy + 4.5f), brush, 1.1f);
    }
    {
        const float bx = right - 42.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 3) drawButtonPill(bx, chromePalette.inactive_tab_hover_bg);
        brush->SetColor(rgb(hovered_action == 3 ? chromePalette.text_primary : baseColor));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f), 1.5f, 1.5f), brush, 1.1f);
        target->DrawLine(D2D1::Point2F(cx - 4.5f, cy), D2D1::Point2F(cx + 4.5f, cy), brush, 1.1f);
    }
    {
        const float bx = right - 20.0f, cx = bx + 10.0f, cy = top + 10.0f;
        if (hovered_action == 4) drawButtonPill(bx, chromePalette.is_dark ? 0x4d2b31 : 0xfad2d8);
        brush->SetColor(rgb(hovered_action == 4 ? (chromePalette.is_dark ? 0xffb4be : 0xb42336) : baseColor));
        target->DrawLine(D2D1::Point2F(cx - 3.5f, cy - 3.5f), D2D1::Point2F(cx + 3.5f, cy + 3.5f), brush, 1.2f);
        target->DrawLine(D2D1::Point2F(cx + 3.5f, cy - 3.5f), D2D1::Point2F(cx - 3.5f, cy + 3.5f), brush, 1.2f);
    }
}
extern "C" void vex_begin_pane(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action, const uint8_t* title, size_t title_length, VexAppIcon icon, VexProgramStatus status) {
    rectangle(bounds.x, bounds.y, bounds.width, bounds.height, chromePalette.pane_bg);
    if (bounds.height >= 28.0f) {
        rectangle(bounds.x, bounds.y, bounds.width, 26.0f, chromePalette.pane_header_bg);
        rectangle(bounds.x, bounds.y + 26.0f, bounds.width, 1.0f, chromePalette.border);
        if (can_focus && !zoomed && focused) {
            rectangle(bounds.x, bounds.y, bounds.width, 2.0f, chromePalette.accent);
        }
    }
    if (can_focus && !zoomed) {
        if (bounds.x > 1.0f) rectangle(bounds.x - 3.5f, bounds.y, 1.0f, bounds.height, chromePalette.border);
        if (bounds.y > 45.0f) rectangle(bounds.x, bounds.y - 3.5f, bounds.width, 1.0f, chromePalette.border);
    }
    drawPaneHeaderActions(bounds, focused, hovered, can_focus, zoomed, hovered_action);
    const float actionsWidth = (bounds.width >= 180.0f && bounds.height >= 28.0f) ? (can_focus ? 94.0f : 72.0f) : 0.0f;
    const float remainingWidth = fmaxf(0, bounds.width - actionsWidth);
    const float statusWidth = status.state >= 2 && status.state <= 5 ? fminf(remainingWidth < 260 ? 20 : 124, fmaxf(0, remainingWidth - 20)) : 0;
    if (statusWidth > 0) drawProgramStatus(status, bounds.x + remainingWidth - statusWidth - 8, bounds.y + 4, statusWidth, remainingWidth < 260);
    float titleInset = 10.0f;
    if (bounds.width >= 90.0f && bounds.height >= 28.0f) {
        const uint64_t iconKey = drawAppIconTile(icon, bounds.x + 8.0f, bounds.y + 4.0f);
        if (focused) focusedPaneIconKey = iconKey;
        titleInset = 30.0f;
    }
    labelUtf8OrDefault(title, title_length, bounds.x + titleInset, bounds.y + 5.0f, fmaxf(0, remainingWidth - statusWidth - titleInset - 8), focused ? chromePalette.text_primary : chromePalette.text_muted);
    gridLeft = bounds.x + 6; gridTop = bounds.y + 30;
    const float scale = dpiScale;
    const D3D12_RECT clip = {
        (LONG)fmaxf(0, roundf(gridLeft * scale)), (LONG)fmaxf(0, roundf(gridTop * scale)),
        (LONG)fminf((float)renderer.width(), fmaxf(gridLeft, bounds.x + bounds.width - 6) * scale),
        (LONG)fminf((float)renderer.height(), fmaxf(gridTop, bounds.y + bounds.height - 6) * scale)
    };
    renderer.beginTerminalPass(clip);
}
extern "C" void vex_end_pane() { renderer.endTerminalPass(); }
extern "C" void vex_draw_scrollbar(VexPaneRect bounds, float thumb_y, float thumb_height, bool active) {
    if (!isfinite(thumb_y) || !isfinite(thumb_height) || thumb_height <= 0.0f || bounds.width < 24.0f) return;
    const float width = active ? 10.0f : 6.0f;
    const float right = bounds.x + bounds.width - 2.0f;
    const float left = right - width;
    const float radius = width * 0.5f;
    brush->SetColor(D2D1::ColorF(chromePalette.text_primary, active ? 0.55f : 0.26f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(left, thumb_y, right, thumb_y + thumb_height), radius, radius), brush);
}
extern "C" void vex_draw_split_preview(VexPaneRect bounds) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    const D2D1_ROUNDED_RECT rect = D2D1::RoundedRect(D2D1::RectF(bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height), 6.0f, 6.0f);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f));
    target->FillRoundedRectangle(rect, brush);
    brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.72f));
    target->DrawRoundedRectangle(rect, brush, 1.5f);
}
static void drawOverlayBackdropAndCard(float mx, float my, float mw, float mh) {
    const D2D1_SIZE_F win = target->GetSize();
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.48f : 0.24f));
    target->FillRectangle(D2D1::RectF(0.0f, 44.0f, win.width, win.height), brush);
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.35f : 0.14f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - 2.0f, my + 4.0f, mx + mw + 2.0f, my + mh + 6.0f), 11.0f, 11.0f), brush);
    const D2D1_ROUNDED_RECT modal = D2D1::RoundedRect(D2D1::RectF(mx, my, mx + mw, my + mh), 9.0f, 9.0f);
    brush->SetColor(rgb(chromePalette.titlebar_bg));
    target->FillRoundedRectangle(modal, brush);
    brush->SetColor(rgb(chromePalette.border));
    target->DrawRoundedRectangle(modal, brush, 1.2f);
}
extern "C" void vex_draw_theme_picker(bool dark_mode, const VexThemeCard* cards, size_t count) {
    if (!target || !brush || !cards || count == 0) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(560.0f, fmaxf(320.0f, win.width - 48.0f));
    const float mh = 384.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = roundf(fmaxf(48.0f, (win.height - mh) * 0.44f));
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    label(L"Theme Studio", mx + 20.0f, my + 14.0f, 220.0f, chromePalette.text_primary);
    labelWithFont(badgeFont, L"Live preview \x2022 Curated dark & light palettes", mx + 20.0f, my + 32.0f, 280.0f, chromePalette.text_muted);

    const float toggleX = mx + mw - 148.0f, toggleY = my + 15.0f;
    const D2D1_ROUNDED_RECT toggleTrack = D2D1::RoundedRect(D2D1::RectF(toggleX, toggleY, toggleX + 128.0f, toggleY + 26.0f), 6.0f, 6.0f);
    brush->SetColor(rgb(chromePalette.pane_header_bg));
    target->FillRoundedRectangle(toggleTrack, brush);
    brush->SetColor(rgb(chromePalette.border));
    target->DrawRoundedRectangle(toggleTrack, brush, 1.0f);

    const float activeHalfX = dark_mode ? (toggleX + 2.0f) : (toggleX + 64.0f);
    const D2D1_ROUNDED_RECT activePill = D2D1::RoundedRect(D2D1::RectF(activeHalfX, toggleY + 2.0f, activeHalfX + 62.0f, toggleY + 24.0f), 4.5f, 4.5f);
    brush->SetColor(rgb(chromePalette.active_tab_bg));
    target->FillRoundedRectangle(activePill, brush);
    brush->SetColor(rgb(chromePalette.active_tab_border));
    target->DrawRoundedRectangle(activePill, brush, 1.0f);
    labelWithFont(badgeFont, L"\x263E Dark", toggleX + 12.0f, toggleY + 5.0f, 52.0f, dark_mode ? chromePalette.text_primary : chromePalette.text_muted);
    labelWithFont(badgeFont, L"\x2600 Light", toggleX + 74.0f, toggleY + 5.0f, 52.0f, !dark_mode ? chromePalette.text_primary : chromePalette.text_muted);

    const float gridX = mx + 20.0f, gridY = my + 58.0f;
    const float colGap = 12.0f, rowGap = 10.0f;
    const float cardW = (mw - 40.0f - colGap) * 0.5f, cardH = 88.0f;
    const size_t visible = count > 6 ? 6 : count;
    for (size_t i = 0; i < visible; i++) {
        const VexThemeCard& c = cards[i];
        const float cx = gridX + (float)(i % 2) * (cardW + colGap);
        const float cy = gridY + (float)(i / 2) * (cardH + rowGap);
        const D2D1_ROUNDED_RECT cardRect = D2D1::RoundedRect(D2D1::RectF(cx, cy, cx + cardW, cy + cardH), 7.0f, 7.0f);
        brush->SetColor(rgb(c.bg_rgb));
        target->FillRoundedRectangle(cardRect, brush);
        brush->SetColor(rgb(c.selected ? c.accent_rgb : (c.hovered ? chromePalette.text_muted : chromePalette.border)));
        target->DrawRoundedRectangle(cardRect, brush, c.selected ? 2.0f : 1.0f);

        const uint32_t subColor = c.is_dark ? 0x8b93a6 : 0x68645e;
        labelUtf8WithFont(chromeFont, c.name, c.name_length, cx + 12.0f, cy + 9.0f, cardW - 64.0f, c.fg_rgb);
        labelUtf8WithFont(badgeFont, c.subtitle, c.subtitle_length, cx + 12.0f, cy + 28.0f, cardW - 24.0f, subColor);

        if (c.active) {
            const D2D1_ROUNDED_RECT badge = D2D1::RoundedRect(D2D1::RectF(cx + cardW - 54.0f, cy + 9.0f, cx + cardW - 10.0f, cy + 25.0f), 4.0f, 4.0f);
            brush->SetColor(D2D1::ColorF(c.accent_rgb, 0.20f));
            target->FillRoundedRectangle(badge, brush);
            brush->SetColor(rgb(c.accent_rgb));
            target->DrawRoundedRectangle(badge, brush, 1.0f);
            labelWithFont(badgeFont, L"Active", cx + cardW - 48.0f, cy + 10.0f, 40.0f, c.fg_rgb);
        }

        const float swY = cy + cardH - 24.0f;
        const D2D1_ROUNDED_RECT termPreview = D2D1::RoundedRect(D2D1::RectF(cx + 12.0f, swY - 2.0f, cx + cardW - 12.0f, swY + 14.0f), 4.0f, 4.0f);
        brush->SetColor(D2D1::ColorF(c.is_dark ? 0x000000 : 0xffffff, c.is_dark ? 0.28f : 0.55f));
        target->FillRoundedRectangle(termPreview, brush);

        uint32_t dots[6] = { c.accent_rgb, c.swatches[0], c.swatches[1], c.swatches[2], c.swatches[3], c.swatches[4] };
        for (int s = 0; s < 6; s++) {
            const float sx = cx + 20.0f + (float)s * 22.0f;
            brush->SetColor(rgb(dots[s]));
            target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(sx, swY + 2.0f, sx + 16.0f, swY + 10.0f), 3.0f, 3.0f), brush);
        }
    }
    labelWithFont(badgeFont, L"\x2190\x2191\x2193\x2192 Preview    Tab: Dark / Light    Enter: Apply    Esc: Revert", mx + 20.0f, my + mh - 24.0f, mw - 40.0f, chromePalette.text_muted);
}
extern "C" void vex_draw_command_palette(const uint8_t* query, size_t query_length, const VexCommandItem* items, size_t count) {
    if (!target || !brush) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(520.0f, fmaxf(300.0f, win.width - 48.0f));
    const size_t visible = count > 9 ? 9 : count;
    const float mh = 58.0f + (float)(visible > 0 ? visible : 1) * 34.0f + 12.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = 58.0f;
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    const D2D1_ROUNDED_RECT searchBox = D2D1::RoundedRect(D2D1::RectF(mx + 12.0f, my + 12.0f, mx + mw - 12.0f, my + 44.0f), 6.0f, 6.0f);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(searchBox, brush);
    brush->SetColor(rgb(chromePalette.active_tab_border));
    target->DrawRoundedRectangle(searchBox, brush, 1.1f);
    label(L">", mx + 22.0f, my + 19.0f, 16.0f, chromePalette.accent);
    if (query && query_length > 0) {
        labelUtf8WithFont(chromeFont, query, query_length, mx + 36.0f, my + 19.0f, mw - 60.0f, chromePalette.text_primary);
    } else {
        label(L"Type a command or action...", mx + 36.0f, my + 19.0f, mw - 60.0f, chromePalette.text_muted);
    }

    const float listY = my + 52.0f;
    if (visible == 0) {
        label(L"No matching commands", mx + 24.0f, listY + 8.0f, mw - 48.0f, chromePalette.text_muted);
        return;
    }
    for (size_t i = 0; i < visible; i++) {
        const VexCommandItem& item = items[i];
        const float ry = listY + (float)i * 34.0f;
        const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 10.0f, ry, mx + mw - 10.0f, ry + 31.0f), 5.0f, 5.0f);
        if (item.selected || item.hovered) {
            brush->SetColor(rgb(item.selected ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
            target->FillRoundedRectangle(rowRect, brush);
            if (item.selected) {
                brush->SetColor(rgb(chromePalette.active_tab_border));
                target->DrawRoundedRectangle(rowRect, brush, 1.0f);
            }
        }
        labelUtf8WithFont(chromeFont, item.title, item.title_length, mx + 22.0f, ry + 7.0f, mw - 160.0f, item.selected ? chromePalette.text_primary : chromePalette.text_muted);
        if (item.shortcut && item.shortcut_length > 0) {
            const float sw = (float)item.shortcut_length * 6.8f + 14.0f;
            const float sx = mx + mw - 20.0f - sw;
            const D2D1_ROUNDED_RECT kbd = D2D1::RoundedRect(D2D1::RectF(sx, ry + 5.5f, sx + sw, ry + 25.5f), 4.0f, 4.0f);
            brush->SetColor(rgb(chromePalette.pane_header_bg));
            target->FillRoundedRectangle(kbd, brush);
            brush->SetColor(rgb(chromePalette.border));
            target->DrawRoundedRectangle(kbd, brush, 1.0f);
            labelUtf8WithFont(badgeFont, item.shortcut, item.shortcut_length, sx + 7.0f, ry + 8.0f, sw - 10.0f, chromePalette.text_muted);
        }
    }
}
extern "C" void vex_draw_shell_picker(uint8_t mode, const VexShellPickerItem* items, size_t count) {
    if (!target || !brush || !items || count == 0) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(460.0f, fmaxf(300.0f, win.width - 48.0f));
    const size_t visible = count > 7 ? 7 : count;
    const float mh = 78.0f + (float)visible * 42.0f + 30.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = 58.0f;
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    label(L"Select Shell", mx + 18.0f, my + 12.0f, 160.0f, chromePalette.text_primary);
    const wchar_t* modeLabels[4] = { L"New Tab", L"Split Right", L"Split Down", L"Default" };
    const float pillGap = 6.0f;
    const float pillW = (mw - 28.0f - 3.0f * pillGap) * 0.25f;
    const float pillY = my + 38.0f;
    for (uint8_t m = 0; m < 4; m++) {
        const float px = mx + 14.0f + (float)m * (pillW + pillGap);
        const bool active = (mode == m);
        const D2D1_ROUNDED_RECT pill = D2D1::RoundedRect(D2D1::RectF(px, pillY, px + pillW, pillY + 26.0f), 5.0f, 5.0f);
        brush->SetColor(rgb(active ? chromePalette.active_tab_bg : chromePalette.pane_header_bg));
        target->FillRoundedRectangle(pill, brush);
        brush->SetColor(rgb(active ? chromePalette.active_tab_border : chromePalette.border));
        target->DrawRoundedRectangle(pill, brush, active ? 1.2f : 1.0f);
        labelWithFont(badgeFont, modeLabels[m], px + 9.0f, pillY + 5.0f, pillW - 12.0f, active ? chromePalette.text_primary : chromePalette.text_muted);
    }

    const float listY = my + 74.0f;
    for (size_t i = 0; i < visible; i++) {
        const VexShellPickerItem& item = items[i];
        const float ry = listY + (float)i * 42.0f;
        const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 10.0f, ry, mx + mw - 10.0f, ry + 38.0f), 6.0f, 6.0f);
        if (item.selected || item.hovered) {
            brush->SetColor(rgb(item.selected ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
            target->FillRoundedRectangle(rowRect, brush);
            if (item.selected) {
                brush->SetColor(rgb(chromePalette.active_tab_border));
                target->DrawRoundedRectangle(rowRect, brush, 1.0f);
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 12.0f, ry + 8.0f, mx + 15.0f, ry + 30.0f), 1.5f, 1.5f), brush);
            }
        }
        VexAppIcon icon = item.icon;
        if (icon.kind == 0) {
            icon.kind = 1;
            icon.glyph = 1;
            icon.color_rgb = chromePalette.accent;
        }
        drawCachedAppIcon(icon, mx + 22.0f, ry + 10.0f, 18.0f);
        labelUtf8WithFont(chromeFont, item.name, item.name_length, mx + 48.0f, ry + 4.0f, mw - 130.0f, item.selected ? chromePalette.text_primary : chromePalette.text_muted);
        labelUtf8WithFont(badgeFont, item.subtitle, item.subtitle_length, mx + 48.0f, ry + 21.0f, mw - 130.0f, chromePalette.text_muted);
        if (item.is_default) {
            const D2D1_ROUNDED_RECT badge = D2D1::RoundedRect(D2D1::RectF(mx + mw - 72.0f, ry + 10.0f, mx + mw - 18.0f, ry + 28.0f), 4.0f, 4.0f);
            brush->SetColor(D2D1::ColorF(chromePalette.accent, 0.18f));
            target->FillRoundedRectangle(badge, brush);
            brush->SetColor(rgb(chromePalette.accent));
            target->DrawRoundedRectangle(badge, brush, 1.0f);
            labelWithFont(badgeFont, L"Default", mx + mw - 64.0f, ry + 11.0f, 44.0f, chromePalette.text_primary);
        }
    }
    labelWithFont(badgeFont, L"\x2191\x2193 Select \x2022 Tab: Action Mode \x2022 Enter: Run \x2022 Esc: Close", mx + 18.0f, my + mh - 23.0f, mw - 36.0f, chromePalette.text_muted);
}
extern "C" void vex_draw_tab_peek(const VexTabPeekCard* cards, size_t count) {
    if (!target || !brush || !cards || count == 0) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(620.0f, fmaxf(340.0f, win.width - 48.0f));
    const size_t visible = count > 9 ? 9 : count;
    const size_t cols = visible <= 2 ? visible : 3;
    const size_t rows = (visible + cols - 1) / cols;
    const float cardH = 86.0f, colGap = 12.0f, rowGap = 12.0f;
    const float mh = 62.0f + (float)rows * cardH + (float)(rows > 0 ? rows - 1 : 0) * rowGap + 24.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = roundf(fmaxf(52.0f, (win.height - mh) * 0.42f));
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    label(L"Tab Overview", mx + 20.0f, my + 15.0f, 200.0f, chromePalette.text_primary);
    labelWithFont(badgeFont, L"\x2190\x2191\x2193\x2192 Select tab \x2022 Enter: Switch \x2022 Esc: Close", mx + mw - 260.0f, my + 18.0f, 240.0f, chromePalette.text_muted);

    const float gridX = mx + 20.0f, gridY = my + 50.0f;
    const float cardW = (mw - 40.0f - (float)(cols - 1) * colGap) / (float)cols;
    for (size_t i = 0; i < visible; i++) {
        const VexTabPeekCard& c = cards[i];
        const float cx = gridX + (float)(i % cols) * (cardW + colGap);
        const float cy = gridY + (float)(i / cols) * (cardH + rowGap);
        const D2D1_ROUNDED_RECT rect = D2D1::RoundedRect(D2D1::RectF(cx, cy, cx + cardW, cy + cardH), 7.0f, 7.0f);
        brush->SetColor(rgb(c.selected ? chromePalette.active_tab_bg : (c.hovered ? chromePalette.inactive_tab_hover_bg : chromePalette.pane_bg)));
        target->FillRoundedRectangle(rect, brush);
        brush->SetColor(rgb(c.selected ? chromePalette.accent : (c.active ? chromePalette.active_tab_border : chromePalette.border)));
        target->DrawRoundedRectangle(rect, brush, c.selected ? 2.0f : 1.0f);

        VexAppIcon icon = c.icon;
        if (icon.kind == 0) {
            icon.kind = 1;
            icon.glyph = 1;
            icon.color_rgb = chromePalette.accent;
        }
        drawCachedAppIcon(icon, cx + 12.0f, cy + 12.0f, 16.0f);
        labelUtf8OrDefault(c.title, c.title_length, cx + 34.0f, cy + 11.0f, cardW - 64.0f, chromePalette.text_primary);
        if (c.status_detail && c.status_detail_length > 0) {
            labelUtf8WithFont(badgeFont, c.status_detail, c.status_detail_length, cx + 12.0f, cy + 33.0f, cardW - 24.0f, chromePalette.text_muted);
        }

        wchar_t numBadge[16];
        swprintf(numBadge, 16, L"#%u", c.index + 1);
        labelWithFont(badgeFont, numBadge, cx + cardW - 28.0f, cy + 12.0f, 24.0f, chromePalette.text_muted);

        wchar_t panesBadge[48];
        if (c.pane_count > 1 && c.status_origin_pane > 0) {
            swprintf(panesBadge, 48, L"%u panes \x2022 Pane %u", c.pane_count, c.status_origin_pane);
            labelWithFont(badgeFont, panesBadge, cx + 12.0f, cy + 56.0f, fmaxf(40.0f, cardW - 128.0f), chromePalette.text_muted);
        } else {
            swprintf(panesBadge, 48, c.pane_count == 1 ? L"1 pane" : L"%u panes", c.pane_count);
            labelWithFont(badgeFont, panesBadge, cx + 12.0f, cy + 56.0f, 80.0f, chromePalette.text_muted);
        }

        if (c.status.state >= 2 && c.status.state <= 5) {
            drawProgramStatus(c.status, cx + cardW - 116.0f, cy + 52.0f, 104.0f, false);
        }
    }
}
extern "C" void vex_draw_path_completion(VexPaneRect bounds, const uint8_t* directory, size_t directory_length, const uint8_t* query, size_t query_length, const VexPathCompletionItem* items, size_t item_count, bool scan_error) {
    if (!target || !brush || bounds.width <= 0.0f || bounds.height <= 0.0f) return;
    const float mx = bounds.x, my = bounds.y, mw = bounds.width, mh = bounds.height;
    brush->SetColor(D2D1::ColorF(0x000000, chromePalette.is_dark ? 0.45f : 0.18f));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx - 1.0f, my + 2.0f, mx + mw + 1.0f, my + mh + 4.0f), 8.0f, 8.0f), brush);
    const D2D1_ROUNDED_RECT card = D2D1::RoundedRect(D2D1::RectF(mx, my, mx + mw, my + mh), 7.0f, 7.0f);
    brush->SetColor(rgb(chromePalette.titlebar_bg));
    target->FillRoundedRectangle(card, brush);
    brush->SetColor(rgb(chromePalette.active_tab_border));
    target->DrawRoundedRectangle(card, brush, 1.2f);

    target->PushAxisAlignedClip(D2D1::RectF(mx + 4.0f, my + 2.0f, mx + mw - 4.0f, my + mh - 2.0f), D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    if (directory && directory_length > 0) {
        wchar_t dirBuf[260];
        const int wlen = MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<const char*>(directory), (int)directory_length, dirBuf, 259);
        if (wlen > 0) {
            dirBuf[wlen] = L'\0';
            const wchar_t* displayDir = wlen > 44 ? (dirBuf + (wlen - 44)) : dirBuf;
            labelWithFont(badgeFont, displayDir, mx + 10.0f, my + 6.0f, mw - 20.0f, chromePalette.text_muted);
        }
    }

    const D2D1_ROUNDED_RECT searchBox = D2D1::RoundedRect(D2D1::RectF(mx + 8.0f, my + 24.0f, mx + mw - 8.0f, my + 50.0f), 5.0f, 5.0f);
    brush->SetColor(rgb(chromePalette.pane_bg));
    target->FillRoundedRectangle(searchBox, brush);
    brush->SetColor(rgb(chromePalette.accent));
    target->DrawRoundedRectangle(searchBox, brush, 1.0f);
    label(L"/", mx + 15.0f, my + 28.0f, 14.0f, chromePalette.accent);
    if (query_length == 0 || !query) {
        labelWithFont(badgeFont, L"Filter path\x2026 (\x2190 up \x2022 Tab dir \x2022 Enter insert)", mx + 28.0f, my + 30.0f, mw - 40.0f, chromePalette.text_muted);
    } else {
        labelUtf8OrDefault(query, query_length, mx + 28.0f, my + 28.0f, mw - 40.0f, chromePalette.text_primary);
    }

    const float listY = my + 56.0f, rowH = 26.0f;
    const size_t visible = item_count > 8 ? 8 : item_count;
    if (visible == 0 || !items) {
        labelWithFont(badgeFont, scan_error ? L"Unable to read directory" : L"No matching files or folders", mx + 14.0f, listY + 5.0f, mw - 28.0f, chromePalette.text_muted);
    } else {
        for (size_t i = 0; i < visible; i++) {
            const VexPathCompletionItem& it = items[i];
            const float iy = listY + (float)i * rowH;
            const D2D1_ROUNDED_RECT rowRect = D2D1::RoundedRect(D2D1::RectF(mx + 6.0f, iy, mx + mw - 6.0f, iy + rowH - 2.0f), 4.0f, 4.0f);
            if (it.selected || it.hovered) {
                brush->SetColor(rgb(it.selected ? chromePalette.active_tab_bg : chromePalette.inactive_tab_hover_bg));
                target->FillRoundedRectangle(rowRect, brush);
            }
            if (it.selected) {
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 8.0f, iy + 5.0f, mx + 11.0f, iy + rowH - 7.0f), 1.5f, 1.5f), brush);
            }
            const float ix = mx + 16.0f;
            if (it.is_directory) {
                brush->SetColor(rgb(chromePalette.accent));
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix, iy + 6.0f, ix + 5.5f, iy + 9.0f), 1.0f, 1.0f), brush);
                target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix, iy + 8.0f, ix + 12.5f, iy + 17.5f), 1.5f, 1.5f), brush);
            } else {
                brush->SetColor(rgb(chromePalette.text_muted));
                target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(ix + 1.5f, iy + 5.5f, ix + 11.0f, iy + 17.5f), 1.2f, 1.2f), brush, 1.1f);
            }
            labelUtf8OrDefault(it.name, it.name_length, mx + 33.0f, iy + 3.0f, mw - 68.0f, chromePalette.text_primary);
            if (it.is_directory) {
                labelWithFont(badgeFont, L"DIR", mx + mw - 34.0f, iy + 5.0f, 26.0f, chromePalette.accent);
            }
        }
    }
    target->PopAxisAlignedClip();
}
extern "C" void vex_draw_close_confirm(VexCloseConfirmModal modal) {
    if (!target || !brush) return;
    const D2D1_SIZE_F win = target->GetSize();
    const float mw = fminf(440.0f, fmaxf(300.0f, win.width - 48.0f));
    const float mh = 186.0f;
    const float mx = roundf((win.width - mw) * 0.5f);
    const float my = roundf(fmaxf(56.0f, (win.height - mh) * 0.44f));
    drawOverlayBackdropAndCard(mx, my, mw, mh);

    const uint32_t dangerColor = (modal.kind == 2)
        ? (chromePalette.is_dark ? 0xf59e0b : 0xd97706)
        : (chromePalette.is_dark ? 0xef4444 : 0xdc2626);

    // Top accent bar inside the modal card
    brush->SetColor(rgb(dangerColor));
    target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(mx + 14.0f, my + 1.0f, mx + mw - 14.0f, my + 3.5f), 1.5f, 1.5f), brush);

    // Icon badge
    const D2D1_ROUNDED_RECT iconBox = D2D1::RoundedRect(D2D1::RectF(mx + 20.0f, my + 18.0f, mx + 48.0f, my + 46.0f), 7.0f, 7.0f);
    brush->SetColor(D2D1::ColorF(dangerColor, chromePalette.is_dark ? 0.18f : 0.12f));
    target->FillRoundedRectangle(iconBox, brush);
    brush->SetColor(D2D1::ColorF(dangerColor, 0.45f));
    target->DrawRoundedRectangle(iconBox, brush, 1.0f);

    VexAppIcon icon = modal.icon;
    if (icon.kind == 0) {
        icon.kind = 1;
        icon.glyph = 1;
        icon.color_rgb = dangerColor;
    }
    drawCachedAppIcon(icon, mx + 25.0f, my + 23.0f, 18.0f);

    if (modal.title && modal.title_length > 0) {
        labelUtf8WithFont(chromeFont, modal.title, modal.title_length, mx + 58.0f, my + 23.0f, mw - 78.0f, chromePalette.text_primary);
    }

    if (modal.message && modal.message_length > 0) {
        wchar_t wide[512];
        int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(modal.message), (int)modal.message_length, wide, 511);
        if (count > 0 && chromeFont) {
            brush->SetColor(rgb(chromePalette.text_muted));
            target->DrawText(wide, (UINT32)count, chromeFont, D2D1::RectF(mx + 22.0f, my + 58.0f, mx + mw - 22.0f, my + 124.0f), brush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
        }
    }

    labelWithFont(badgeFont, L"Esc: Cancel \x2022 Enter: Confirm", mx + 22.0f, my + mh - 36.0f, 165.0f, chromePalette.text_muted);

    const float btnY = my + mh - 46.0f;
    const float btnH = 32.0f;
    const float confirmW = 128.0f;
    const float confirmX = mx + mw - 20.0f - confirmW;
    const float cancelW = 88.0f;
    const float cancelX = confirmX - 10.0f - cancelW;

    const bool cancelSelected = (modal.selected_button == 0);
    const bool cancelHovered = (modal.hovered_button == 1);
    const D2D1_ROUNDED_RECT cancelRect = D2D1::RoundedRect(D2D1::RectF(cancelX, btnY, cancelX + cancelW, btnY + btnH), 6.0f, 6.0f);
    brush->SetColor(rgb((cancelSelected || cancelHovered) ? chromePalette.active_tab_bg : chromePalette.pane_bg));
    target->FillRoundedRectangle(cancelRect, brush);
    brush->SetColor(rgb(cancelSelected ? chromePalette.accent : chromePalette.border));
    target->DrawRoundedRectangle(cancelRect, brush, cancelSelected ? 1.5f : 1.0f);
    label(L"Cancel", cancelX + 22.0f, btnY + 7.0f, cancelW - 24.0f, chromePalette.text_primary);

    const bool confirmSelected = (modal.selected_button == 1);
    const bool confirmHovered = (modal.hovered_button == 2);
    const D2D1_ROUNDED_RECT confirmRect = D2D1::RoundedRect(D2D1::RectF(confirmX, btnY, confirmX + confirmW, btnY + btnH), 6.0f, 6.0f);
    brush->SetColor(D2D1::ColorF(dangerColor, (confirmSelected || confirmHovered) ? 0.92f : 0.78f));
    target->FillRoundedRectangle(confirmRect, brush);
    brush->SetColor(rgb(confirmSelected ? 0xffffff : dangerColor));
    target->DrawRoundedRectangle(confirmRect, brush, confirmSelected ? 1.5f : 1.0f);
    if (modal.confirm_label && modal.confirm_label_length > 0) {
        const float textW = (float)modal.confirm_label_length * 6.8f;
        const float textX = confirmX + fmaxf(12.0f, (confirmW - textW) * 0.5f);
        labelUtf8WithFont(chromeFont, modal.confirm_label, modal.confirm_label_length, textX, btnY + 7.0f, confirmW - 16.0f, 0xffffff);
    }
}
static float snapDip(float v) {
    return dpiScale > 0.0f ? roundf(v * dpiScale) / dpiScale : roundf(v);
}

static void rectBounds(float left, float top, float right, float bottom, uint32_t color) {
    if (right <= left || bottom <= top) return;
    brush->SetColor(rgb(color));
    target->FillRectangle(D2D1::RectF(left, top, right, bottom), brush);
}

static void drawBoxArms(float left, float top, float right, float bottom, float cx, float cy, float lightT, float heavyT, float gap, uint8_t l, uint8_t r, uint8_t u, uint8_t d, uint32_t color) {
    const float halfL = lightT * 0.5f;
    const float halfH = heavyT * 0.5f;
    const float doubleSpan = lightT * 2.0f + gap;
    const float halfSpan = doubleSpan * 0.5f;
    const float halfGap = gap * 0.5f;
    const bool vDouble = (u == 3 || d == 3);
    const bool hDouble = (l == 3 || r == 3);

    if (l == 1 || l == 2) {
        const float ht = l == 2 ? halfH : halfL;
        const float endX = vDouble ? ((u == 3 && d == 3 && r != 3) ? cx - halfGap : cx + halfSpan) : (r == 0 ? cx + (u == 2 || d == 2 ? halfH : halfL) : cx);
        rectBounds(left, snapDip(cy - ht), snapDip(endX), snapDip(cy - ht) + (l == 2 ? heavyT : lightT), color);
    }
    if (r == 1 || r == 2) {
        const float ht = r == 2 ? halfH : halfL;
        const float startX = vDouble ? ((u == 3 && d == 3 && l != 3) ? cx + halfGap : cx - halfSpan) : (l == 0 ? cx - (u == 2 || d == 2 ? halfH : halfL) : cx);
        rectBounds(snapDip(startX), snapDip(cy - ht), right, snapDip(cy - ht) + (r == 2 ? heavyT : lightT), color);
    }
    if (u == 1 || u == 2) {
        const float ht = u == 2 ? halfH : halfL;
        const float endY = hDouble ? ((l == 3 && r == 3 && d != 3) ? cy - halfGap : cy + halfSpan) : (d == 0 ? cy + (l == 2 || r == 2 ? halfH : halfL) : cy);
        rectBounds(snapDip(cx - ht), top, snapDip(cx - ht) + (u == 2 ? heavyT : lightT), snapDip(endY), color);
    }
    if (d == 1 || d == 2) {
        const float ht = d == 2 ? halfH : halfL;
        const float startY = hDouble ? ((l == 3 && r == 3 && u != 3) ? cy + halfGap : cy - halfSpan) : (u == 0 ? cy - (l == 2 || r == 2 ? halfH : halfL) : cy);
        rectBounds(snapDip(cx - ht), snapDip(startY), snapDip(cx - ht) + (d == 2 ? heavyT : lightT), bottom, color);
    }

    const float topY = snapDip(cy - halfSpan);
    const float botY = snapDip(cy + halfGap);
    const float leftX = snapDip(cx - halfSpan);
    const float rightX = snapDip(cx + halfGap);

    if (l == 3) {
        const float topEnd = u == 3 ? leftX + lightT : (vDouble || u != 0 ? cx + halfSpan : cx);
        const float botEnd = d == 3 ? leftX + lightT : (vDouble || d != 0 ? cx + halfSpan : cx);
        rectBounds(left, topY, snapDip(topEnd), topY + lightT, color);
        rectBounds(left, botY, snapDip(botEnd), botY + lightT, color);
    }
    if (r == 3) {
        const float topStart = u == 3 ? rightX : (vDouble || u != 0 ? cx - halfSpan : cx);
        const float botStart = d == 3 ? rightX : (vDouble || d != 0 ? cx - halfSpan : cx);
        rectBounds(snapDip(topStart), topY, right, topY + lightT, color);
        rectBounds(snapDip(botStart), botY, right, botY + lightT, color);
    }
    if (u == 3) {
        const float leftEnd = l == 3 ? topY + lightT : (hDouble || l != 0 ? cy + halfSpan : cy);
        const float rightEnd = r == 3 ? topY + lightT : (hDouble || r != 0 ? cy + halfSpan : cy);
        rectBounds(leftX, top, leftX + lightT, snapDip(leftEnd), color);
        rectBounds(rightX, top, rightX + lightT, snapDip(rightEnd), color);
    }
    if (d == 3) {
        const float leftStart = l == 3 ? botY : (hDouble || l != 0 ? cy - halfSpan : cy);
        const float rightStart = r == 3 ? botY : (hDouble || r != 0 ? cy - halfSpan : cy);
        rectBounds(leftX, snapDip(leftStart), leftX + lightT, bottom, color);
        rectBounds(rightX, snapDip(rightStart), rightX + lightT, bottom, color);
    }
}

#define VEX_ARMS(l, r, u, d) ((uint8_t)((l) | ((r) << 2) | ((u) << 4) | ((d) << 6)))
static const uint8_t boxArmsTable[128] = {
    VEX_ARMS(1,1,0,0), VEX_ARMS(2,2,0,0), VEX_ARMS(0,0,1,1), VEX_ARMS(0,0,2,2), 0, 0, 0, 0, 0, 0, 0, 0, VEX_ARMS(0,1,0,1), VEX_ARMS(0,2,0,1), VEX_ARMS(0,1,0,2), VEX_ARMS(0,2,0,2),
    VEX_ARMS(1,0,0,1), VEX_ARMS(2,0,0,1), VEX_ARMS(1,0,0,2), VEX_ARMS(2,0,0,2), VEX_ARMS(0,1,1,0), VEX_ARMS(0,2,1,0), VEX_ARMS(0,1,2,0), VEX_ARMS(0,2,2,0), VEX_ARMS(1,0,1,0), VEX_ARMS(2,0,1,0), VEX_ARMS(1,0,2,0), VEX_ARMS(2,0,2,0), VEX_ARMS(0,1,1,1), VEX_ARMS(0,2,1,1), VEX_ARMS(0,1,2,1), VEX_ARMS(0,1,1,2),
    VEX_ARMS(0,1,2,2), VEX_ARMS(0,2,2,1), VEX_ARMS(0,2,1,2), VEX_ARMS(0,2,2,2), VEX_ARMS(1,0,1,1), VEX_ARMS(2,0,1,1), VEX_ARMS(1,0,2,1), VEX_ARMS(1,0,1,2), VEX_ARMS(1,0,2,2), VEX_ARMS(2,0,2,1), VEX_ARMS(2,0,1,2), VEX_ARMS(2,0,2,2), VEX_ARMS(1,1,0,1), VEX_ARMS(2,1,0,1), VEX_ARMS(1,2,0,1), VEX_ARMS(2,2,0,1),
    VEX_ARMS(1,1,0,2), VEX_ARMS(2,1,0,2), VEX_ARMS(1,2,0,2), VEX_ARMS(2,2,0,2), VEX_ARMS(1,1,1,0), VEX_ARMS(2,1,1,0), VEX_ARMS(1,2,1,0), VEX_ARMS(2,2,1,0), VEX_ARMS(1,1,2,0), VEX_ARMS(2,1,2,0), VEX_ARMS(1,2,2,0), VEX_ARMS(2,2,2,0), VEX_ARMS(1,1,1,1), VEX_ARMS(2,1,1,1), VEX_ARMS(1,2,1,1), VEX_ARMS(2,2,1,1),
    VEX_ARMS(1,1,2,1), VEX_ARMS(1,1,1,2), VEX_ARMS(1,1,2,2), VEX_ARMS(2,1,2,1), VEX_ARMS(1,2,2,1), VEX_ARMS(2,1,1,2), VEX_ARMS(1,2,1,2), VEX_ARMS(2,2,2,1), VEX_ARMS(2,2,1,2), VEX_ARMS(2,1,2,2), VEX_ARMS(1,2,2,2), VEX_ARMS(2,2,2,2), 0, 0, 0, 0,
    VEX_ARMS(3,3,0,0), VEX_ARMS(0,0,3,3), VEX_ARMS(0,3,0,1), VEX_ARMS(0,1,0,3), VEX_ARMS(0,3,0,3), VEX_ARMS(3,0,0,1), VEX_ARMS(1,0,0,3), VEX_ARMS(3,0,0,3), VEX_ARMS(0,3,1,0), VEX_ARMS(0,1,3,0), VEX_ARMS(0,3,3,0), VEX_ARMS(3,0,1,0), VEX_ARMS(1,0,3,0), VEX_ARMS(3,0,3,0), VEX_ARMS(0,3,1,1), VEX_ARMS(0,1,3,3),
    VEX_ARMS(0,3,3,3), VEX_ARMS(3,0,1,1), VEX_ARMS(1,0,3,3), VEX_ARMS(3,0,3,3), VEX_ARMS(3,3,0,1), VEX_ARMS(1,1,0,3), VEX_ARMS(3,3,0,3), VEX_ARMS(3,3,1,0), VEX_ARMS(1,1,3,0), VEX_ARMS(3,3,3,0), VEX_ARMS(3,3,1,1), VEX_ARMS(1,1,3,3), VEX_ARMS(3,3,3,3), 0, 0, 0,
    0, 0, 0, 0, VEX_ARMS(1,0,0,0), VEX_ARMS(0,0,1,0), VEX_ARMS(0,1,0,0), VEX_ARMS(0,0,0,1), VEX_ARMS(2,0,0,0), VEX_ARMS(0,0,2,0), VEX_ARMS(0,2,0,0), VEX_ARMS(0,0,0,2), VEX_ARMS(1,2,0,0), VEX_ARMS(0,0,1,2), VEX_ARMS(2,1,0,0), VEX_ARMS(0,0,2,1)
};
#undef VEX_ARMS

static bool drawCustomGlyph(uint32_t cp, float x, float y, float w, float h, uint32_t foreground) {
    const float left = snapDip(x);
    const float top = snapDip(y);
    const float right = snapDip(x + w);
    const float bottom = snapDip(y + h);
    const float cw = right - left;
    const float ch = bottom - top;
    if (cw <= 0.0f || ch <= 0.0f) return false;
    const float px = dpiScale > 0.0f ? (1.0f / dpiScale) : 1.0f;
    const float lightT = fmaxf(px, roundf(dpiScale) * px);
    const float heavyT = fmaxf(lightT + px, roundf(2.0f * dpiScale) * px);

    if (cp >= 0x2580 && cp <= 0x259F) {
        if (cp == 0x2580) {
            rectBounds(left, top, right, snapDip(top + ch * 0.5f), foreground);
        } else if (cp >= 0x2581 && cp <= 0x2587) {
            const float frac = (float)(cp - 0x2580) / 8.0f;
            rectBounds(left, snapDip(bottom - ch * frac), right, bottom, foreground);
        } else if (cp == 0x2588) {
            rectBounds(left, top, right, bottom, foreground);
        } else if (cp >= 0x2589 && cp <= 0x258F) {
            const float frac = (float)(0x2590 - cp) / 8.0f;
            rectBounds(left, top, snapDip(left + cw * frac), bottom, foreground);
        } else if (cp == 0x2590) {
            rectBounds(snapDip(left + cw * 0.5f), top, right, bottom, foreground);
        } else if (cp >= 0x2591 && cp <= 0x2593) {
            const float alpha = cp == 0x2591 ? 0.25f : (cp == 0x2592 ? 0.50f : 0.75f);
            brush->SetColor(D2D1::ColorF((foreground >> 16) / 255.0f, ((foreground >> 8) & 255) / 255.0f, (foreground & 255) / 255.0f, alpha));
            target->FillRectangle(D2D1::RectF(left, top, right, bottom), brush);
        } else if (cp == 0x2594) {
            rectBounds(left, top, right, snapDip(top + ch * 0.125f), foreground);
        } else if (cp == 0x2595) {
            rectBounds(snapDip(right - cw * 0.125f), top, right, bottom, foreground);
        } else {
            static const uint8_t quadMask[10] = { 4, 8, 1, 13, 9, 7, 11, 2, 6, 14 };
            const uint8_t m = quadMask[cp - 0x2596];
            const float midX = snapDip(left + cw * 0.5f);
            const float midY = snapDip(top + ch * 0.5f);
            if (m & 1) rectBounds(left, top, midX, midY, foreground);
            if (m & 2) rectBounds(midX, top, right, midY, foreground);
            if (m & 4) rectBounds(left, midY, midX, bottom, foreground);
            if (m & 8) rectBounds(midX, midY, right, bottom, foreground);
        }
        return true;
    }

    if (cp >= 0x2500 && cp <= 0x257F) {
        const float cx = snapDip(left + cw * 0.5f);
        const float cy = snapDip(top + ch * 0.5f);
        if ((cp >= 0x2504 && cp <= 0x250B) || (cp >= 0x254C && cp <= 0x254F)) {
            const bool vertical = (cp >= 0x2506 && cp <= 0x250B) || cp == 0x254E || cp == 0x254F;
            const bool heavy = (cp & 1) != 0;
            const int count = (cp >= 0x254C) ? 2 : ((cp == 0x2504 || cp == 0x2505 || cp == 0x2506 || cp == 0x2507) ? 3 : 4);
            const float t = heavy ? heavyT : lightT;
            const float span = vertical ? ch : cw;
            const float step = span / (float)count;
            const float dashLen = fmaxf(px, step * 0.65f);
            for (int i = 0; i < count; i++) {
                const float start = (float)i * step + (step - dashLen) * 0.5f;
                if (vertical) {
                    const float rx = snapDip(cx - t * 0.5f);
                    const float ry = snapDip(top + start);
                    rectBounds(rx, ry, rx + t, snapDip(ry + dashLen), foreground);
                } else {
                    const float rx = snapDip(left + start);
                    const float ry = snapDip(cy - t * 0.5f);
                    rectBounds(rx, ry, snapDip(rx + dashLen), ry + t, foreground);
                }
            }
            return true;
        }
        if (cp >= 0x256D && cp <= 0x2570) {
            ID2D1PathGeometry* path = nullptr;
            ID2D1GeometrySink* sink = nullptr;
            if (drawingFactory && SUCCEEDED(drawingFactory->CreatePathGeometry(&path)) && path && SUCCEEDED(path->Open(&sink)) && sink) {
                const float r = fminf(cw, ch) * 0.45f;
                if (cp == 0x256D) {
                    sink->BeginFigure(D2D1::Point2F(right, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx + r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy + r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, bottom));
                } else if (cp == 0x256E) {
                    sink->BeginFigure(D2D1::Point2F(left, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx - r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy + r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, bottom));
                } else if (cp == 0x256F) {
                    sink->BeginFigure(D2D1::Point2F(left, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx - r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy - r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, top));
                } else {
                    sink->BeginFigure(D2D1::Point2F(right, cy), D2D1_FIGURE_BEGIN_HOLLOW);
                    sink->AddLine(D2D1::Point2F(cx + r, cy));
                    sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(cx, cy - r), D2D1::SizeF(r, r), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                    sink->AddLine(D2D1::Point2F(cx, top));
                }
                sink->EndFigure(D2D1_FIGURE_END_OPEN);
                sink->Close();
                brush->SetColor(rgb(foreground));
                target->DrawGeometry(path, brush, lightT);
            }
            releaseCom(sink);
            releaseCom(path);
            return true;
        }
        if (cp >= 0x2571 && cp <= 0x2573) {
            brush->SetColor(rgb(foreground));
            if (cp == 0x2571 || cp == 0x2573) target->DrawLine(D2D1::Point2F(right, top), D2D1::Point2F(left, bottom), brush, lightT);
            if (cp == 0x2572 || cp == 0x2573) target->DrawLine(D2D1::Point2F(left, top), D2D1::Point2F(right, bottom), brush, lightT);
            return true;
        }
        const uint8_t packed = boxArmsTable[cp - 0x2500];
        if (packed != 0) {
            drawBoxArms(left, top, right, bottom, cx, cy, lightT, heavyT, lightT, packed & 3, (packed >> 2) & 3, (packed >> 4) & 3, (packed >> 6) & 3, foreground);
            return true;
        }
        return false;
    }

    if (cp >= 0xE0B0 && cp <= 0xE0B7) {
        const float cy = top + ch * 0.5f;
        brush->SetColor(rgb(foreground));
        if (cp == 0xE0B1) {
            target->DrawLine(D2D1::Point2F(left, top), D2D1::Point2F(right, cy), brush, lightT);
            target->DrawLine(D2D1::Point2F(right, cy), D2D1::Point2F(left, bottom), brush, lightT);
            return true;
        }
        if (cp == 0xE0B3) {
            target->DrawLine(D2D1::Point2F(right, top), D2D1::Point2F(left, cy), brush, lightT);
            target->DrawLine(D2D1::Point2F(left, cy), D2D1::Point2F(right, bottom), brush, lightT);
            return true;
        }
        ID2D1PathGeometry* path = nullptr;
        ID2D1GeometrySink* sink = nullptr;
        if (drawingFactory && SUCCEEDED(drawingFactory->CreatePathGeometry(&path)) && path && SUCCEEDED(path->Open(&sink)) && sink) {
            const bool filled = (cp == 0xE0B0 || cp == 0xE0B2 || cp == 0xE0B4 || cp == 0xE0B6);
            if (cp == 0xE0B0) {
                sink->BeginFigure(D2D1::Point2F(left, top), D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(D2D1::Point2F(right, cy));
                sink->AddLine(D2D1::Point2F(left, bottom));
                sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            } else if (cp == 0xE0B2) {
                sink->BeginFigure(D2D1::Point2F(right, top), D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(D2D1::Point2F(left, cy));
                sink->AddLine(D2D1::Point2F(right, bottom));
                sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            } else if (cp == 0xE0B4 || cp == 0xE0B5) {
                sink->BeginFigure(D2D1::Point2F(left, top), filled ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
                sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(left, bottom), D2D1::SizeF(cw, ch * 0.5f), 0.0f, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                sink->EndFigure(filled ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            } else {
                sink->BeginFigure(D2D1::Point2F(right, top), filled ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
                sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(right, bottom), D2D1::SizeF(cw, ch * 0.5f), 0.0f, D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, D2D1_ARC_SIZE_SMALL));
                sink->EndFigure(filled ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            }
            sink->Close();
            if (filled) target->FillGeometry(path, brush);
            else target->DrawGeometry(path, brush, lightT);
        }
        releaseCom(sink);
        releaseCom(path);
        return true;
    }

    if (cp >= 0x2800 && cp <= 0x28FF) {
        const uint8_t mask = (uint8_t)(cp - 0x2800);
        if (mask == 0) return true;
        const float dotW = fmaxf(px, snapDip(cw * 0.22f));
        const float dotH = fmaxf(px, snapDip(ch * 0.14f));
        const float colX[2] = { snapDip(left + cw * 0.28f - dotW * 0.5f), snapDip(left + cw * 0.72f - dotW * 0.5f) };
        const float rowY[4] = {
            snapDip(top + ch * 0.15f - dotH * 0.5f),
            snapDip(top + ch * 0.38f - dotH * 0.5f),
            snapDip(top + ch * 0.62f - dotH * 0.5f),
            snapDip(top + ch * 0.85f - dotH * 0.5f),
        };
        static const uint8_t bitCol[8] = { 0, 0, 0, 1, 1, 1, 0, 1 };
        static const uint8_t bitRow[8] = { 0, 1, 2, 0, 1, 2, 3, 3 };
        for (int b = 0; b < 8; b++) {
            if (mask & (1 << b)) {
                rectBounds(colX[bitCol[b]], rowY[bitRow[b]], colX[bitCol[b]] + dotW, rowY[bitRow[b]] + dotH, foreground);
            }
        }
        return true;
    }

    return false;
}

static HRESULT paintAtlasGlyph(ID2D1RenderTarget* raster, ID2D1SolidColorBrush* rasterBrush, const uint8_t* utf8, size_t length, uint32_t flags, float x, float y, float width, float height) {
    ID2D1RenderTarget* previousTarget = target;
    ID2D1SolidColorBrush* previousBrush = brush;
    target = raster; brush = rasterBrush;
    bool custom = false;
    if (length == 3 && (utf8[0] & 0xf0) == 0xe0) {
        const uint32_t cp = ((uint32_t)(utf8[0] & 15) << 12) | ((uint32_t)(utf8[1] & 63) << 6) | (utf8[2] & 63);
        custom = drawCustomGlyph(cp, x, y, width, height, 0xffffff);
    }
    target = previousTarget; brush = previousBrush;
    if (custom) return S_OK;
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)utf8, (int)length, nullptr, 0);
    if (count <= 0) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t scratch[4096];
    auto text = count <= 4096 ? scratch : static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, (size_t)count * sizeof(wchar_t)));
    if (!text) return E_OUTOFMEMORY;
    HRESULT result = S_OK;
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)utf8, (int)length, text, count)) result = HRESULT_FROM_WIN32(GetLastError());
    else {
        rasterBrush->SetColor(D2D1::ColorF(1, 1, 1, 1));
        raster->DrawText(text, count, terminalFonts[flags & 3], D2D1::RectF(x, y, x + width, y + height), rasterBrush, D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }
    if (text != scratch) HeapFree(GetProcessHeap(), 0, text);
    return result;
}
extern "C" int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags) {
    const float x = gridLeft + column * cellWidth, y = gridTop + row * cellHeight, width = cells * cellWidth;
    HRESULT result = S_OK;
    if (background != chromePalette.pane_bg) result = renderer.terminal.drawSolid(snapDip(x), snapDip(y), snapDip(x + width), snapDip(y + cellHeight), background);
    if (!length || (flags & 16) || FAILED(result)) return (int)renderer.terminalResult(result);
    if (!utf8 || length > 65536) return (int)renderer.terminalResult(E_INVALIDARG);
    size_t trimmed = length;
    while (trimmed && utf8[trimmed - 1] == ' ') trimmed--;
    bool ascii = length == cells;
    for (size_t i = 0; ascii && i < trimmed; i++) ascii = utf8[i] >= 32 && utf8[i] <= 126;
    if (ascii) {
        for (size_t i = 0; i < trimmed && SUCCEEDED(result); i++) {
            if (utf8[i] != ' ') result = renderer.terminal.drawGlyph(x + (float)i * cellWidth, y, cellWidth, cellHeight, utf8 + i, 1, flags, foreground, paintAtlasGlyph);
        }
    } else if (trimmed) result = renderer.terminal.drawGlyph(x, y, width, cellHeight, utf8, trimmed, flags, foreground, paintAtlasGlyph);
    if (SUCCEEDED(result) && (flags & 4)) result = renderer.terminal.drawSolid(x, y + cellHeight - 2, x + width, y + cellHeight - 1, foreground);
    if (SUCCEEDED(result) && (flags & 8)) result = renderer.terminal.drawSolid(x, y + cellHeight * 0.55f, x + width, y + cellHeight * 0.55f + 1, foreground);
    return (int)renderer.terminalResult(result);
}
extern "C" void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color) {
    const float x = gridLeft + column * cellWidth, y = gridTop + row * cellHeight;
    HRESULT result;
    if (style == 0) result = renderer.terminal.drawSolid(x, y, x + 1.5f, y + cellHeight, color);
    else if (style == 1) result = renderer.terminal.drawSolid(snapDip(x), snapDip(y), snapDip(x + cellWidth), snapDip(y + cellHeight), color);
    else if (style == 2) result = renderer.terminal.drawSolid(x, y + cellHeight - 2, x + cellWidth, y + cellHeight, color);
    else {
        result = renderer.terminal.drawSolid(x, y, x + cellWidth, y + 1, color);
        if (SUCCEEDED(result)) result = renderer.terminal.drawSolid(x, y + cellHeight - 1, x + cellWidth, y + cellHeight, color);
        if (SUCCEEDED(result)) result = renderer.terminal.drawSolid(x, y + 1, x + 1, y + cellHeight - 1, color);
        if (SUCCEEDED(result)) result = renderer.terminal.drawSolid(x + cellWidth - 1, y + 1, x + cellWidth, y + cellHeight - 1, color);
    }
    renderer.terminalResult(result);
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
extern "C" int vex_open_url(const uint8_t* bytes, size_t length) {
    if (!bytes || !length || length > 2048 || memchr(bytes, 0, length)) return ERROR_INVALID_PARAMETER;
    wchar_t wide[2049];
    int units = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes), (int)length, wide, 2048);
    if (units <= 0) return (int)GetLastError();
    wide[units] = 0;
    if (_wcsnicmp(wide, L"https://", 8) != 0 && _wcsnicmp(wide, L"http://", 7) != 0 && _wcsnicmp(wide, L"file://", 7) != 0) {
        return ERROR_INVALID_PARAMETER;
    }
    if (selftestEnabled()) return 0;
    HINSTANCE result = ShellExecuteW(IsWindow(window) ? window : nullptr, L"open", wide, nullptr, nullptr, SW_SHOWNORMAL);
    return reinterpret_cast<INT_PTR>(result) > 32 ? 0 : ERROR_OPEN_FAILED;
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
