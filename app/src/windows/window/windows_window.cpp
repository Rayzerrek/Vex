#include "windows_platform.h"
#include "windows_capture.h"
#include "windows_dialog.h"
#include "windows_notification.h"
#include "drawing_color.h"
#include "windows_renderer.h"
#include "windows_chrome.h"
#include "terminal_drawing.h"
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
static uint32_t uiVerificationMode;
static UiRenderer* target;
static UiBrush uiBrush;
static UiBrush* brush = &uiBrush;
static IDWriteTextFormat* terminalFonts[4];
static IDWriteTextFormat* chromeFont;
static IDWriteTextFormat* notificationFont;
static IDWriteTextFormat* paneTitleFont;
static IDWriteTextFormat* chromeBoldFont;
static IDWriteTextFormat* badgeFont;
static constexpr bool roundCaps = true;
static float cellWidth, cellHeight, clientWidth, clientHeight;
static float terminalFontSize = 14.0f;
static float dpiScale = 1.0f;
static uint32_t tabStripCount = 1;
static float tabWidthDip = 160;
enum class FrameSchedule { none, posted, synchronized, output };
static FrameSchedule frameSchedule = FrameSchedule::none;
static bool firstOutput, firstFrame, startupCloaked;
static bool cursorWakeArmed;
static ULONGLONG frameDeadline;
static int platformError;
static LARGE_INTEGER startupCounter, counterFrequency;
static HANDLE outputFrameTimer;
static HMONITOR outputMonitor;
static LONGLONG outputFrameTicks, nextOutputFrame;
static uint16_t pendingSurrogate;
static DWORD clipboardSequence;
static unsigned clipboardRetries;
static bool clipboardListening;
static uint64_t clipboardUpdates, clipboardTextUpdates;
static DWORD clipboardOwnerPid, clipboardReadError;
static uint32_t clipboardToken;
static ULONGLONG copyCheckDeadline;
// Timer 4 belongs to terminal notifications; copy validation must not replace it.
static constexpr UINT_PTR copyCheckTimer = 6;
static constexpr UINT_PTR clipboardRetryTimer = 5;
static uint64_t paintCount;
static LARGE_INTEGER frameRequestCounter;
static UINT64 frameRequestDelayUs, sceneDurationUs;
static bool measureFrameTiming;
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
    0xe6e9f0, 0x898d98, 0x8b7cff,
    true
};

HWND vex_native_window() { return window; }

template<class T> static void releaseCom(T*& object) {
    if (object) { object->Release(); object = nullptr; }
}
static void rectangle(float x, float y, float width, float height, uint32_t color) {
    brush->SetColor(rgb(color));
    target->FillRectangle(D2D1::RectF(x, y, x + width, y + height), brush);
}
static constexpr float tabStripLeft = 8;
static float tabStripEnd() { return tabStripLeft + tabStripCount * (tabWidthDip + 4); }
static float plusLeft() { return fmaxf(tabStripEnd(), clientWidth - 166.0f); }
extern "C" int vex_window_tab_strip(uint32_t count, float tab_width) {
    if (!count || count > 8 || !isfinite(tab_width) || tab_width < 24 || tab_width > 200) return ERROR_INVALID_PARAMETER;
    tabStripCount = count; tabWidthDip = tab_width;
    return 0;
}
extern "C" int vex_draw_tab_insertion_marker(float x) {
    if (!target || !brush || !isfinite(x) || x < tabStripLeft || x > tabStripEnd()) return ERROR_INVALID_PARAMETER;
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
    target = nullptr;
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
        if (SUCCEEDED(result) && !firstFrame) startupMilestone("renderer ready");
        if (FAILED(result)) return result;
        rendererGeneration++;
    }
    target = renderer.drawingTarget();
    return S_OK;
}
static HRESULT resizeRenderer(UINT width, UINT height, float dpi) {
    if (rendererResetOnNextResize) {
        rendererResetOnNextResize = false;
        return DXGI_ERROR_DEVICE_RESET;
    }
    return renderer.resize(width, height, dpi);
}
static void updateOutputCadence() {
    HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    if (monitor == outputMonitor && outputFrameTicks) return;
    outputMonitor = monitor;
    MONITORINFOEXW info = {}; info.cbSize = sizeof(info);
    DEVMODEW mode = {}; mode.dmSize = sizeof(mode);
    UINT refresh = 60;
    if (GetMonitorInfoW(monitor, &info) && EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode) &&
        mode.dmDisplayFrequency >= 24 && mode.dmDisplayFrequency <= 1000) refresh = mode.dmDisplayFrequency;
    outputFrameTicks = counterFrequency.QuadPart / refresh;
}
static void updateWindowMetrics() {
    updateOutputCadence();
    RECT bounds; GetClientRect(window, &bounds);
    float scale = GetDpiForWindow(window) / 96.0f;
    dpiScale = scale > 0.0f ? scale : 1.0f;
    clientWidth = bounds.right / scale;
    clientHeight = bounds.bottom / scale;
    callbacks.resize(callbacks.context, clientWidth, clientHeight, cellWidth, cellHeight, scale);
}
static void renderScheduledFrame() {
    KillTimer(window, 1);
    if (outputFrameTimer) CancelWaitableTimer(outputFrameTimer);
    frameSchedule = FrameSchedule::none;
    InvalidateRect(window, nullptr, FALSE); UpdateWindow(window);
}
extern "C" void vex_window_invalidate() {
    if (!IsWindow(window)) return;
    ULONGLONG now = GetTickCount64();
    if (frameSchedule != FrameSchedule::posted) {
        frameDeadline = now + 8;
        KillTimer(window, 1);
        if (outputFrameTimer) CancelWaitableTimer(outputFrameTimer);
        frameSchedule = FrameSchedule::posted;
        // A posted frame coalesces queued changes without Win32 timer-resolution input delay.
        if (!PostMessageW(window, WM_APP + 10, 0, 0)) { platformError = (int)GetLastError(); DestroyWindow(window); }
    } else if (now >= frameDeadline) renderScheduledFrame();
}
static void invalidateOutputFrame() {
    if (!IsWindow(window)) return;
    if (frameSchedule == FrameSchedule::posted || frameSchedule == FrameSchedule::synchronized) {
        vex_window_invalidate();
        return;
    }
    LARGE_INTEGER now; QueryPerformanceCounter(&now);
    if (now.QuadPart >= nextOutputFrame) {
        if (frameSchedule == FrameSchedule::output) renderScheduledFrame();
        else vex_window_invalidate();
        return;
    }
    if (frameSchedule == FrameSchedule::output) return;
    // Isolated output stays immediate. Only a sustained stream is paced to the current monitor.
    if (!outputFrameTimer) outputFrameTimer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!outputFrameTimer) { vex_window_invalidate(); return; }
    LARGE_INTEGER due;
    due.QuadPart = -((nextOutputFrame - now.QuadPart) * 10000000 / counterFrequency.QuadPart + 1);
    if (!SetWaitableTimer(outputFrameTimer, &due, 0, nullptr, nullptr, FALSE)) { vex_window_invalidate(); return; }
    frameDeadline = GetTickCount64() + (ULONGLONG)((-due.QuadPart + 9999) / 10000);
    frameSchedule = FrameSchedule::output;
}

static BOOL nextWindowMessage(MSG& message) {
    for (;;) {
        // Sent messages can arm/cancel the timer while PeekMessage dispatches them, even without queued input.
        const bool outputPending = frameSchedule == FrameSchedule::output;
        const DWORD status = MsgWaitForMultipleObjectsEx(outputPending ? 1 : 0, outputPending ? &outputFrameTimer : nullptr,
            INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (status == WAIT_FAILED) return -1;
        if (outputPending && status == WAIT_OBJECT_0) {
            if (frameSchedule == FrameSchedule::output) renderScheduledFrame();
            continue;
        }
        if (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) return message.message == WM_QUIT ? 0 : 1;
    }
}

extern "C" void vex_window_defer_synchronized() {
    if (!IsWindow(window)) return;
    ULONGLONG now = GetTickCount64();
    if (frameSchedule != FrameSchedule::synchronized) {
        frameDeadline = now + 100;
        if (outputFrameTimer) CancelWaitableTimer(outputFrameTimer);
        if (!SetTimer(window, 1, 100, nullptr)) { platformError = (int)GetLastError(); DestroyWindow(window); }
        else frameSchedule = FrameSchedule::synchronized;
    } else if (now >= frameDeadline) renderScheduledFrame();
}
extern "C" void vex_window_close() { if (IsWindow(window)) PostMessageW(window, WM_CLOSE, 1, 0); }
extern "C" uint64_t vex_window_paint_count() { return paintCount; }
extern "C" bool vex_window_cursor_wake_armed() { return cursorWakeArmed; }
static bool motionPreferenceKnown = false, motionEnabled = false;
extern "C" bool vex_window_animations_enabled() {
    if (!motionPreferenceKnown) {
        BOOL enabled = FALSE;
        motionEnabled = SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0) && enabled;
        motionPreferenceKnown = true;
    }
    return motionEnabled;
}
extern "C" uint32_t vex_window_wheel_scroll_lines() {
    UINT lines = 3;
    SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
    return lines;
}
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

static void drawNewTabButton() {
    const float by = 8.0f;
    const float bx = plusLeft() + 1.0f;
    const float sx = plusLeft() + 24.0f;
    const bool enabled = tabStripCount < 8;
    const bool groupHot = (enabled && hoveredNewTab) || hoveredShellPicker;
    if (groupHot) {
        brush->SetColor(D2D1::ColorF(chromePalette.border, chromePalette.is_dark ? 0.55f : 0.45f));
        target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx, by, sx + 15.0f, by + 28.0f), 6.0f, 6.0f), brush, 1.0f);
    }
    // New Tab button [ + ]
    if (enabled && hoveredNewTab) {
        brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(bx + 1.0f, by + 1.0f, bx + 22.0f, by + 27.0f), 5.0f, 5.0f), brush);
    }
    const float cx = bx + 11.0f;
    const float cy = by + 14.0f;
    brush->SetColor(rgb(enabled ? (hoveredNewTab ? chromePalette.accent : chromePalette.text_primary) : chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(cx - 4.5f, cy), D2D1::Point2F(cx + 4.5f, cy), brush, 1.35f, roundCaps);
    target->DrawLine(D2D1::Point2F(cx, cy - 4.5f), D2D1::Point2F(cx, cy + 4.5f), brush, 1.35f, roundCaps);

    // Subtle divider between [+] and [v]
    brush->SetColor(D2D1::ColorF(chromePalette.border, groupHot ? 0.65f : 0.32f));
    target->FillRectangle(D2D1::RectF(sx - 0.5f, by + 6.0f, sx + 0.5f, by + 22.0f), brush);

    // Shell Picker dropdown caret [ v ]
    if (hoveredShellPicker) {
        brush->SetColor(rgb(chromePalette.inactive_tab_hover_bg));
        target->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(sx + 0.5f, by + 1.0f, sx + 14.0f, by + 27.0f), 4.0f, 4.0f), brush);
    }
    const float scx = sx + 7.5f;
    brush->SetColor(rgb(hoveredShellPicker ? chromePalette.accent : chromePalette.text_muted));
    target->DrawLine(D2D1::Point2F(scx - 3.0f, cy - 1.5f), D2D1::Point2F(scx, cy + 1.8f), brush, 1.25f, roundCaps);
    target->DrawLine(D2D1::Point2F(scx, cy + 1.8f), D2D1::Point2F(scx + 3.0f, cy - 1.5f), brush, 1.25f, roundCaps);
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
        target->DrawLine(D2D1::Point2F(cx - 5.0f, cy), D2D1::Point2F(cx + 5.0f, cy), brush, 1.3f, roundCaps);
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
            target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 5.0f, cy - 2.0f, cx + 2.2f, cy + 5.0f), 1.6f, 1.6f), brush, 1.25f);
            target->DrawLine(D2D1::Point2F(cx - 2.2f, cy - 2.0f), D2D1::Point2F(cx - 2.2f, cy - 4.8f), brush, 1.25f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx - 2.2f, cy - 4.8f), D2D1::Point2F(cx + 4.8f, cy - 4.8f), brush, 1.25f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx + 4.8f, cy - 4.8f), D2D1::Point2F(cx + 4.8f, cy + 2.2f), brush, 1.25f, roundCaps);
            target->DrawLine(D2D1::Point2F(cx + 2.2f, cy + 2.2f), D2D1::Point2F(cx + 4.8f, cy + 2.2f), brush, 1.25f, roundCaps);
        } else {
            target->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cx - 4.8f, cy - 4.8f, cx + 4.8f, cy + 4.8f), 1.8f, 1.8f), brush, 1.25f);
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
        target->DrawLine(D2D1::Point2F(cx - 4.5f, cy - 4.5f), D2D1::Point2F(cx + 4.5f, cy + 4.5f), brush, 1.35f, roundCaps);
        target->DrawLine(D2D1::Point2F(cx + 4.5f, cy - 4.5f), D2D1::Point2F(cx - 4.5f, cy + 4.5f), brush, 1.35f, roundCaps);
    }
}

static void renderUiVerificationScene() {
    const float unit = target->GetSize().width / renderer.width();
    const auto rect = [unit](float l, float t, float r, float b) { return D2D1::RectF(l * unit, t * unit, r * unit, b * unit); };
    const auto point = [unit](float x, float y) { return D2D1::Point2F(x * unit, y * unit); };
    UiBrush paint;
    target->Clear(rgb(0x101010));
    paint.SetColor(rgb(0xff0000));
    target->FillRoundedRectangle(D2D1::RoundedRect(rect(20, 60, 180, 130), 16 * unit, 16 * unit), &paint);
    paint.SetColor(rgb(0x0000ff));
    target->DrawRoundedRectangle(D2D1::RoundedRect(rect(220, 60, 380, 130), 16 * unit, 16 * unit), &paint, 4 * unit);
    paint.SetColor(rgb(0x00ff00));
    target->DrawLine(point(20, 160), point(180, 180), &paint, 8 * unit, true);
    paint.SetColor(rgb(0xffcc00));
    target->FillEllipse(D2D1::Ellipse(point(260, 180), 40 * unit, 20 * unit), &paint);
    paint.SetColor(rgb(0x0000ff));
    target->DrawEllipse(D2D1::Ellipse(point(380, 180), 40 * unit, 20 * unit), &paint, 4 * unit);
    target->PushAxisAlignedClip(rect(20, 220, 160, 300), D2D1_ANTIALIAS_MODE_ALIASED);
    paint.SetColor(rgb(0xff8000)); target->FillRectangle(rect(0, 200, 200, 320), &paint);
    target->PushAxisAlignedClip(rect(60, 240, 120, 280), D2D1_ANTIALIAS_MODE_ALIASED);
    paint.SetColor(rgb(0x00ffff)); target->FillRectangle(rect(0, 200, 200, 320), &paint);
    target->PopAxisAlignedClip();
    paint.SetColor(rgb(0xff0000)); target->FillRectangle(rect(140, 260, 180, 280), &paint);
    target->PopAxisAlignedClip();
    paint.SetColor(rgb(0x0000ff)); target->FillRectangle(rect(180, 250, 190, 260), &paint);
    paint.SetColor(D2D1::ColorF(0xff0000, 0.5f)); target->FillRectangle(rect(220, 220, 300, 280), &paint);
    paint.SetColor(rgb(0xffffff));
    if (uiVerificationMode == 2) {
        for (unsigned i = 0; i < 2300; i++) {
            wchar_t text[16]; swprintf(text, 16, L"%04u", i);
            target->DrawText(text, (UINT32)wcslen(text), chromeFont, rect(20, 340, 60, 374), &paint, D2D1_DRAW_TEXT_OPTIONS_CLIP);
        }
        paint.SetColor(rgb(0x101010)); target->FillRectangle(rect(20, 330, 1060, 380), &paint);
        paint.SetColor(rgb(0xffffff));
    }
    wchar_t text[192];
    for (auto& ch : text) ch = L'M';
    target->DrawText(text, 192, chromeFont, rect(20, 340, 1060, 380), &paint, D2D1_DRAW_TEXT_OPTIONS_CLIP);
}

static void renderScene(double timeMs) {
    beginWindowChromeFrame({ target, textFactory, chromeFont, chromeBoldFont, badgeFont, paneTitleFont, chromePalette, tabWidthDip, tabStripCount, hoveredTabIndex, hoveredTabClose,
        timeMs, vex_window_animations_enabled() && GetForegroundWindow() == window && IsWindowEnabled(window) });
    beginTerminalDrawingFrame({ &renderer, drawingFactory, { terminalFonts[0], terminalFonts[1], terminalFonts[2], terminalFonts[3] }, dpiScale, cellWidth, cellHeight, chromePalette.pane_bg });
    target->Clear(rgb(chromePalette.window_bg));
    rectangle(0.0f, 0.0f, clientWidth, 44.0f, chromePalette.titlebar_bg);
    drawNewTabButton();
    callbacks.paint(callbacks.context);
    drawCaptionButtons();
    drawTerminalDialogBackdrop(*target, *brush, dpiScale);
    drawTerminalNotification(*target, *brush, chromeBoldFont, notificationFont, chromePalette);
    if (uiVerificationMode) renderUiVerificationScene();
}

static bool drawWindow(void* pixels = nullptr, UINT stride = 0) {
    RECT bounds;
    if (!IsWindow(window) || IsIconic(window) || !GetClientRect(window, &bounds) || bounds.right <= 0 || bounds.bottom <= 0) return false;
    HRESULT result = createRenderTarget();
    if (SUCCEEDED(result)) result = renderer.beginFrame();
    if (FAILED(result)) { rendererFault(result); return false; }
    if (frameSchedule == FrameSchedule::posted || frameSchedule == FrameSchedule::output) {
        if (outputFrameTimer) CancelWaitableTimer(outputFrameTimer);
        frameSchedule = FrameSchedule::none;
    }
    paintCount++;
    LARGE_INTEGER sceneBegin; QueryPerformanceCounter(&sceneBegin);
    if (measureFrameTiming) {
        if (frameRequestCounter.QuadPart) {
            frameRequestDelayUs = (UINT64)((sceneBegin.QuadPart - frameRequestCounter.QuadPart) * 1000000 / counterFrequency.QuadPart);
            frameRequestCounter.QuadPart = 0;
        }
    }
    renderScene(1000.0 * sceneBegin.QuadPart / counterFrequency.QuadPart);
    if (measureFrameTiming) {
        LARGE_INTEGER sceneEnd; QueryPerformanceCounter(&sceneEnd);
        sceneDurationUs = (UINT64)((sceneEnd.QuadPart - sceneBegin.QuadPart) * 1000000 / counterFrequency.QuadPart);
    }
    result = renderer.endFrame(pixels, stride);
    if (FAILED(result)) { rendererFault(result); return false; }
    rendererRecoveryAttempts = 0;
    // Preparation and a blocking Present already consume the interval; do not add another full period afterward.
    nextOutputFrame = sceneBegin.QuadPart + outputFrameTicks;
    if ((windowChromeAnimationPending() || windowChromeEffectsPending()) && frameSchedule != FrameSchedule::synchronized) invalidateOutputFrame();
    if (SUCCEEDED(result) && !firstFrame) {
        firstFrame = true;
        startupMilestone("first frame");
        if (startupCloaked) {
            // Let DWM consume the prepared surface before exposing the non-client frame.
            DwmFlush();
            const BOOL cloak = FALSE;
            DwmSetWindowAttribute(window, DWMWA_CLOAK, &cloak, sizeof(cloak));
            startupCloaked = false;
        }
    }
    return SUCCEEDED(result);
}

static void updateCaptureScale() {
    const float dpi = (float)GetDpiForWindow(window);
    dpiScale = dpi > 0.0f ? (dpi / 96.0f) : 1.0f;
}
static void drawToDc(HDC dc) {
    if (!drawingFactory || !window || !dc) return;
    updateCaptureScale();
    drawCapturedWindow(window, dc, drawWindow);
}
static bool saveCaptureBmp() {
    if (!drawingFactory || !window) return false;
    updateCaptureScale();
    return saveWindowCaptureBmp(window, drawWindow);
}

// Return an owned UTF-8 buffer. No application callback runs while the clipboard
// is locked: callbacks can write to it again or pump window messages.
static DWORD readClipboardText(uint8_t** output, size_t* length) {
    *output = nullptr;
    *length = 0;
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
                            if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, (int)units, (char*)bytes, size, nullptr, nullptr)) {
                                *output = bytes;
                                *length = (size_t)size;
                            } else {
                                error = GetLastError();
                                HeapFree(GetProcessHeap(), 0, bytes);
                            }
                        }
                    }
                }
                GlobalUnlock(data);
            }
        }
        CloseClipboard();
    }
    return error;
}
static void pasteClipboard() {
    uint8_t* bytes;
    size_t length;
    const DWORD error = readClipboardText(&bytes, &length);
    if (bytes) {
        callbacks.paste(callbacks.context, bytes, length);
        HeapFree(GetProcessHeap(), 0, bytes);
    }
    if (error) {
        char message[160];
        snprintf(message, sizeof(message), "Vex native paste failed (Win32 %lu). Clipboard text must be valid Unicode and at most 1 MiB.", (unsigned long)error);
        vex_dialog_warning(VexDialogText{reinterpret_cast<const uint8_t*>(message), strlen(message)});
    }
}
extern "C" void vex_window_paste_clipboard() { pasteClipboard(); }
extern "C" void vex_window_schedule_copy_check(uint32_t delay_ms) {
    const ULONGLONG deadline = GetTickCount64() + delay_ms;
    if (copyCheckDeadline && copyCheckDeadline <= deadline) return;
    if (SetTimer(window, copyCheckTimer, delay_ms ? delay_ms : 1, nullptr)) copyCheckDeadline = deadline;
}
static void clipboardUpdated() {
    ++clipboardUpdates;
    KillTimer(window, clipboardRetryTimer);
    const DWORD sequence = GetClipboardSequenceNumber();
    if (sequence == clipboardSequence || GetForegroundWindow() != window) return;
    const HWND owner = GetClipboardOwner();
    DWORD pid = 0;
    if (owner) GetWindowThreadProcessId(owner, &pid);
    clipboardOwnerPid = pid;
    // Vex already handles its own selections and OSC writes synchronously.
    if (pid == GetCurrentProcessId()) { clipboardSequence = sequence; return; }
    const uint32_t token = pid ? vex_pty_clipboard_token(pid) : 0;
    clipboardToken = token;
    // WSL/remote clipboard relays can own the HWND outside the PTY's job.
    // Token 0 is untrusted: Zig requires tighter input correlation and unchanged
    // visible source text instead of identifying a relay by executable name.
    uint8_t* bytes;
    size_t length;
    const DWORD error = readClipboardText(&bytes, &length);
    clipboardReadError = error;
    if (error == ERROR_BUSY || error == ERROR_ACCESS_DENIED) {
        if (++clipboardRetries <= 5) SetTimer(window, clipboardRetryTimer, 20, nullptr);
        return;
    }
    clipboardSequence = sequence;
    clipboardRetries = 0;
    // Unattributed providers are accepted only if Zig can associate the text
    // with a recent operation in the active pane.
    if (!error && bytes && GetClipboardSequenceNumber() == sequence && callbacks.clipboard) {
        ++clipboardTextUpdates;
        callbacks.clipboard(callbacks.context, token, bytes, length);
    }
    if (bytes) HeapFree(GetProcessHeap(), 0, bytes);
}
static uint32_t currentModifiers() {
    return (GetKeyState(VK_SHIFT) < 0 ? 1u : 0u) | (GetKeyState(VK_CONTROL) < 0 ? 2u : 0u) | (GetKeyState(VK_MENU) < 0 ? 4u : 0u);
}
static bool handleKey(uint32_t key) {
    uint32_t mods = currentModifiers();
    if ((key == 'V' && mods == 3) || (key == VK_INSERT && mods == 1)) { pasteClipboard(); return true; }
    return callbacks.key(callbacks.context, key, mods);
}
static void updateTopChromeHover(float x, float y);
static void setPointerCursor(uint32_t response) {
    const bool chromeButton = hoveredCaptionButton != 0 || hoveredNewTab || hoveredShellPicker || hoveredTabIndex != -1;
    uint16_t resource = response == VEX_POINTER_RIGHT ? 32644 : response == VEX_POINTER_DOWN ? 32645 :
        response == VEX_POINTER_LINK || chromeButton ? 32649 :
        response == VEX_POINTER_SELECTION || response == VEX_POINTER_TEXT ? 32513 : 32512;
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
    updateTopChromeHover(x, y);
    setPointerCursor(callbacks.pointer(callbacks.context, VEX_POINTER_HOVER, x, y, currentModifiers()));
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
    setPointerCursor(response);
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
        } else if (x >= plusLeft() && x < plusLeft() + 24.0f && y >= 7.0f && y <= 37.0f) {
            nextNewTab = true;
        } else if (x >= plusLeft() + 24.0f && x < plusLeft() + 42.0f && y >= 7.0f && y <= 37.0f) {
            nextShellPicker = true;
        } else if (x >= tabStripLeft && x < tabStripEnd() && y >= 7.0f && y <= 37.0f) {
            uint32_t idx = (uint32_t)((x - tabStripLeft) / (tabWidthDip + 4.0f));
            float local = x - tabStripLeft - idx * (tabWidthDip + 4.0f);
            if (idx < tabStripCount && local >= 0.0f && local < tabWidthDip) {
                nextTab = (int)idx;
                nextTabClose = local > tabWidthDip - 22.0f;
            }
        }
    }
    if (nextCaption != hoveredCaptionButton ||
        nextNewTab != hoveredNewTab || nextShellPicker != hoveredShellPicker ||
        nextTab != hoveredTabIndex || nextTabClose != hoveredTabClose) {
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
        if (point.y / scale < 44 && x < clientWidth - 120 && (x < tabStripLeft || (x >= tabStripEnd() && x < plusLeft()) || x >= plusLeft() + 42)) return HTCAPTION;
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
            updateTopChromeHover(x, y);
            setPointerCursor(callbacks.pointer(callbacks.context, VEX_POINTER_HOVER, x, y, currentModifiers()));
            return TRUE;
        }
        break;
    case WM_LBUTTONDBLCLK: {
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float x = GET_X_LPARAM(lparam) / scale, y = GET_Y_LPARAM(lparam) / scale;
        if (y < 44 && x >= tabStripLeft && x < tabStripEnd()) {
            uint32_t index = (uint32_t)((x - tabStripLeft) / (tabWidthDip + 4));
            float local = x - tabStripLeft - index * (tabWidthDip + 4);
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
        if (message == WM_LBUTTONDOWN && dismissTerminalNotificationAt(x, y)) return 0;
        if (message != WM_MBUTTONDOWN && y < 44 && x >= clientWidth - 120) {
            pressedCaptionButton = (x >= clientWidth - 40) ? 3 : (x >= clientWidth - 80) ? 2 : 1;
            vex_window_invalidate();
            return 0;
        }
        if (y < 44 && x >= tabStripLeft && x < tabStripEnd()) {
            uint32_t index = (uint32_t)((x - tabStripLeft) / (tabWidthDip + 4));
            float local = x - tabStripLeft - index * (tabWidthDip + 4);
            if (index >= tabStripCount || local >= tabWidthDip) return 0;
            bool close = message == WM_MBUTTONDOWN || local > tabWidthDip - 22;
            if (close) callbacks.action(callbacks.context, 16, index);
            else beginPointerGesture(hwnd, x, y);
        } else if (y < 44 && x >= plusLeft() && x < plusLeft() + 24) callbacks.action(callbacks.context, 0, 0);
        else if (y < 44 && x >= plusLeft() + 24 && x < plusLeft() + 42) callbacks.action(callbacks.context, 18, 0);
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
        else if (response == VEX_POINTER_NORMAL || response == VEX_POINTER_LINK || response == VEX_POINTER_TEXT) cancelPointerGesture();
        updateTopChromeHover(x, y);
        setPointerCursor(response);
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
    case WM_DISPLAYCHANGE:
        outputMonitor = nullptr;
        if (window) updateOutputCadence();
        break;
    case WM_WINDOWPOSCHANGED:
        if (window) updateOutputCadence();
        repositionTerminalDialogs();
        break;
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
        renderer.ui.batches.clearGlyphCache();
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
        updateWindowMetrics(); repositionTerminalDialogs(); InvalidateRect(hwnd, nullptr, FALSE);
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
            if (visible) invalidateOutputFrame();
            else if (frameSchedule != FrameSchedule::none && GetTickCount64() >= frameDeadline) renderScheduledFrame();
        }
        return 0;
    }
    case WM_APP + 2: return selftestEnabled() && wparam <= UINT32_MAX ? (LRESULT)callbacks.probe(callbacks.context, (uint32_t)wparam) : 0;
    case WM_APP + 3:
        if (wparam <= UINT32_MAX && vex_pty_exists((uint32_t)wparam))
            callbacks.session_error(callbacks.context, (uint32_t)wparam, lparam ? (uint32_t)lparam : ERROR_WRITE_FAULT);
        return 0;
    case WM_APP + 4:
        if (wparam <= UINT32_MAX && vex_pty_exists((uint32_t)wparam)) callbacks.exited(callbacks.context, (uint32_t)wparam, (uint32_t)lparam);
        return 0;
    case WM_APP + 5:
        if (selftestEnabled() && wparam <= 19 && lparam >= 0 && (uint64_t)lparam <= UINT32_MAX) callbacks.action(callbacks.context, (uint32_t)wparam, (uint32_t)lparam);
        return 0;
    case WM_APP + 6: {
        uint32_t source = (uint32_t)(wparam & 0xff), operation = (uint32_t)(wparam >> 8);
        if (!selftestEnabled() || wparam > 0x8ff || operation > 8 || source >= tabStripCount) return 0;
        float scale = GetDpiForWindow(hwnd) / 96.0f;
        float startX = (tabStripLeft + source * (tabWidthDip + 4) + fminf(40, (tabWidthDip - 22) / 2)) * scale;
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
    case WM_APP + 10:
        if (frameSchedule == FrameSchedule::posted) renderScheduledFrame();
        return 0;
    case WM_APP + 9:
        if (!selftestEnabled()) return 0;
        measureFrameTiming = true;
        QueryPerformanceCounter(&frameRequestCounter);
        if (wparam == 1) vex_window_defer_synchronized();
        else if (wparam == 3) invalidateOutputFrame();
        else if (wparam == 2) {
            for (int i = 0; i < 20; ++i) vex_window_invalidate();
            InvalidateRect(hwnd, nullptr, FALSE);
            UpdateWindow(hwnd);
        } else vex_window_invalidate();
        return 1;
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
            case 18: return (LRESULT)renderer.ui.batches.drawCalls;
            case 19: return (LRESULT)renderer.ui.batches.glyphMisses;
            case 22: return (LRESULT)renderer.ui.batches.glyphHits;
            case 23: return (LRESULT)renderer.ui.batches.uploadAllocations;
            case 24: return (LRESULT)renderer.ui.batches.uploadCapacity();
            case 25: return TerminalRenderer::atlasSize * TerminalRenderer::atlasSize;
            case 26: return (LRESULT)renderer.ui.batches.atlasResets;
            case 27: return (LRESULT)frameRequestDelayUs;
            case 28: return (LRESULT)sceneDurationUs;
            case 50: return (LRESULT)renderer.terminal.geometryHits;
            case 51: return (LRESULT)renderer.terminal.geometryBuilds;
            case 52: return (LRESULT)renderer.terminal.geometryCapacity();
            case 53: renderer.terminal.geometryEnabled = false; return 1;
            case 54: renderer.terminal.geometryEnabled = true; return 1;
            case 55: return outputFrameTicks ? (LRESULT)(counterFrequency.QuadPart / outputFrameTicks) : 0;
            case 56: return windowChromeAnimationPending() ? 1 : 0;
            case 57: return (LRESULT)lroundf(windowChromeDiagnostics().tabSelectionX * 1000);
            case 58: return vex_window_animations_enabled() ? 1 : 0;
            case 59: return clipboardListening ? 1 : 0;
            case 60: return (LRESULT)clipboardUpdates;
            case 61: return (LRESULT)clipboardTextUpdates;
            case 62: return clipboardOwnerPid;
            case 63: return clipboardToken;
            case 64: return clipboardReadError;
            case 65: return terminalNotificationVisible() ? 1 : 0;
            case 66: {
                const uint64_t token = callbacks.probe(callbacks.context, 12);
                return token <= UINT32_MAX && vex_pty_test_delay_close((uint32_t)token) ? 1 : 0;
            }
            case 40: uiVerificationMode = 1; vex_window_invalidate(); return 1;
            case 41: uiVerificationMode = 0; vex_window_invalidate(); return 1;
            case 42: uiVerificationMode = 2; vex_window_invalidate(); return 1;
            case 30: return (LRESULT)windowChromeDiagnostics().activeTabIconKey;
            case 31: return (LRESULT)windowChromeDiagnostics().focusedPaneIconKey;
            case 32: return (LRESULT)windowChromeDiagnostics().activeTabIconCount;
            case 20: case 21: {
                UINT64 bytes = 0;
                return SUCCEEDED(renderer.videoMemoryUsage(wparam == 20, bytes)) ? (LRESULT)bytes : -1;
            }
        }
        return 0;
    case WM_CLIPBOARDUPDATE:
        clipboardRetries = 0;
        clipboardUpdated();
        return 0;
    case WM_TIMER:
        if (wparam == copyCheckTimer) {
            KillTimer(hwnd, copyCheckTimer);
            copyCheckDeadline = 0;
            if (callbacks.tick) callbacks.tick(callbacks.context);
            return 0;
        }
        if (wparam == clipboardRetryTimer) { clipboardUpdated(); return 0; }
        if (wparam == 4) { clearTerminalNotification(); return 0; }
        if (wparam == 3) {
            KillTimer(hwnd, 3); cursorWakeArmed = false;
            callbacks.cursor_wake(callbacks.context);
        } else if (wparam == 2) {
            HRESULT result = renderer.trimUnusedTerminalUploads();
            if (FAILED(result)) { rendererFault(result); return 0; }
            if (callbacks.tick) callbacks.tick(callbacks.context);
        } else if (wparam == 1 && frameSchedule == FrameSchedule::synchronized && GetTickCount64() >= frameDeadline) {
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
        callbacks.scroll(callbacks.context, -GET_WHEEL_DELTA_WPARAM(wparam), point.x / scale, point.y / scale, currentModifiers());
        return 0;
    }
    case WM_CLOSE:
        cancelPointerGesture();
        if (wparam != 1 && callbacks.close_requested && !callbacks.close_requested(callbacks.context)) return 0;
        DestroyWindow(hwnd);
        return 0;
    case WM_SETTINGCHANGE:
        motionPreferenceKnown = false;
        InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    case WM_DESTROY: clearTerminalNotification(); destroyTerminalDialogs(); cancelPointerGesture(); RemoveClipboardFormatListener(hwnd); KillTimer(hwnd, 1); KillTimer(hwnd, 2); KillTimer(hwnd, 3); KillTimer(hwnd, copyCheckTimer); KillTimer(hwnd, clipboardRetryTimer); copyCheckDeadline = 0; cursorWakeArmed = false; PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

static wchar_t terminalFontFamily[128] = L"Cascadia Mono";
static HRESULT rebuildTerminalFonts(float fontSize) {
    if (!textFactory || !isfinite(fontSize)) return E_INVALIDARG;
    const float clamped = fminf(32.0f, fmaxf(9.0f, roundf(fontSize)));
    IDWriteTextFormat* newFonts[4] = {};
    HRESULT result = S_OK;
    for (int i = 0; i < 4 && SUCCEEDED(result); i++) {
        result = textFactory->CreateTextFormat(terminalFontFamily, nullptr, (i & 1) ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
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
extern "C" int vex_window_set_font_preferences(const uint8_t* family, float font_size) {
    wchar_t wide[128] = {};
    if (!family || !MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(family), -1, wide, 128)) return E_INVALIDARG;
    if (!isfinite(font_size)) return E_INVALIDARG;
    if (wcscmp(terminalFontFamily, wide) == 0 && terminalFontSize == fminf(32.0f, fmaxf(9.0f, roundf(font_size)))) return 0;
    wchar_t previous[128]; memcpy(previous, terminalFontFamily, sizeof(previous));
    memcpy(terminalFontFamily, wide, sizeof(wide));
    if (!textFactory) {
        // Startup already knows the preferences; create only the chosen fonts and grid metrics.
        terminalFontSize = fminf(32.0f, fmaxf(9.0f, roundf(font_size)));
        return 0;
    }
    int result = vex_window_set_font_size(font_size);
    if (result) memcpy(terminalFontFamily, previous, sizeof(previous));
    return result;
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
    startupMilestone("application ready");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HRESULT result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &drawingFactory);
    if (FAILED(result)) return (int)result;
    result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)&textFactory);
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
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12.5f, L"en-us", &notificationFont);
    if (SUCCEEDED(result)) result = notificationFont->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 14.0f, L"en-us", &paneTitleFont);
    if (SUCCEEDED(result)) result = configureChromeFormat(paneTitleFont);
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12.0f, L"en-us", &chromeBoldFont);
    if (SUCCEEDED(result)) result = configureChromeFormat(chromeBoldFont);
    if (SUCCEEDED(result)) result = textFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 10.0f, L"en-us", &badgeFont);
    if (SUCCEEDED(result)) result = configureChromeFormat(badgeFont);
    if (SUCCEEDED(result)) result = rebuildTerminalFonts(terminalFontSize);
    if (SUCCEEDED(result)) startupMilestone("fonts ready");
    HICON iconBig = nullptr, iconSmall = nullptr;
    loadAppIcons(iconBig, iconSmall);
    if (SUCCEEDED(result)) {
        WNDCLASSEXW klass = {};
        klass.cbSize = sizeof(klass); klass.style = CS_DBLCLKS; klass.lpfnWndProc = windowProcedure;
        klass.hInstance = GetModuleHandleW(nullptr); klass.lpszClassName = L"VexNativeTerminal";
        klass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
        klass.hIcon = iconBig ? iconBig : LoadIconW(nullptr, MAKEINTRESOURCEW(32512));
        klass.hIconSm = iconSmall ? iconSmall : klass.hIcon;
        if (!RegisterClassExW(&klass)) platformError = (int)GetLastError();
        else {
            const float initialScale = GetDpiForSystem() / 96.0f;
            window = CreateWindowExW(0, klass.lpszClassName, L"Vex", WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, (int)(1120 * initialScale), (int)(700 * initialScale), nullptr, nullptr, klass.hInstance, nullptr);
            if (!window) platformError = (int)GetLastError();
            else {
                clipboardSequence = GetClipboardSequenceNumber();
                clipboardListening = AddClipboardFormatListener(window) != FALSE;
                initializeTerminalDialogs(window, callbacks);
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
                const BOOL cloak = TRUE;
                startupCloaked = SUCCEEDED(DwmSetWindowAttribute(window, DWMWA_CLOAK, &cloak, sizeof(cloak)));
                if (!startupCloaked) drawWindow();
                ShowWindow(window, SW_SHOW); UpdateWindow(window);
                MSG message; BOOL status;
                while ((status = nextWindowMessage(message)) > 0) {
                    if (routeTerminalDialogMessage(message)) continue;
                    if (message.message == WM_KEYDOWN || message.message == WM_SYSKEYDOWN) {
                        // Do not TranslateMessage a consumed workspace chord into terminal control bytes.
                        if (DispatchMessageW(&message) != 1) TranslateMessage(&message);
                    } else { TranslateMessage(&message); DispatchMessageW(&message); }
                }
                if (status < 0) platformError = (int)GetLastError();
            }
        }
    } else platformError = (int)result;
    if (outputFrameTimer) { CloseHandle(outputFrameTimer); outputFrameTimer = nullptr; }
    destroyTerminalDialogs();
    if (iconBig) DestroyIcon(iconBig);
    if (iconSmall) DestroyIcon(iconSmall);
    HRESULT shutdown = releaseRenderer();
    if (FAILED(shutdown) && !WindowsRenderer::deviceLost(shutdown) && !platformError) platformError = (int)shutdown;
    releaseCom(chromeFont); releaseCom(notificationFont); releaseCom(paneTitleFont); releaseCom(chromeBoldFont); releaseCom(badgeFont);
    for (auto& font : terminalFonts) releaseCom(font);
    releaseCom(textFactory); releaseCom(drawingFactory);
    return platformError;
}

extern "C" void vex_window_error(const char* message) {
    MessageBoxA(IsWindow(window) ? window : nullptr, message, "Vex native rewrite", MB_OK | MB_ICONERROR);
    if (IsWindow(window)) PostMessageW(window, WM_CLOSE, 1, 0);
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
