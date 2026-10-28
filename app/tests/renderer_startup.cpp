#include "windows_renderer.h"
#include "window_visibility.h"
#include <stdio.h>

// Hidden WARP fixture: exercise worker ownership without depending on a hardware adapter or UI input.
int testRendererStartup() {
    {
        WindowsRenderer cancelled;
        if (FAILED(cancelled.prepareDeviceAsync(true))) return 1;
        // Destruction before the first paint must join the worker before releasing its native owner.
    }
    WindowsRenderer renderer;
    if (FAILED(renderer.prepareDeviceAsync(true))) return 2;
    if (renderer.ready() || renderer.prepareDeviceAsync(true) != E_UNEXPECTED) return 3;
    UINT64 bytes = 0;
    if (renderer.videoMemoryUsage(true, bytes) != E_UNEXPECTED || renderer.debugValidationEnabled()) return 4;
    if (FAILED(renderer.finishDevicePreparation()) || FAILED(renderer.finishDevicePreparation())) return 5;
    if (renderer.ready() || FAILED(renderer.shutdown()) || FAILED(renderer.shutdown())) return 6;
    if (FAILED(renderer.prepareDeviceAsync(true)) || FAILED(renderer.shutdown())) return 7;

    RendererComPtr<ID2D1Factory1> factory;
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &factory))) return 8;
    HWND window = CreateWindowExW(0, L"STATIC", L"Vex renderer fixture", WS_POPUP, 0, 0, 32, 16, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return 9;
    int failure = 0;
    if (windowCanRender(nullptr, true) || windowCanRender(window, false) || !windowCanRender(window, true) ||
        !windowCanRender(window, true, true) || windowCanRender(window, false, true)) failure = 21;
    if (FAILED(renderer.prepareDeviceAsync(true))) failure = 10;
    if (!failure && renderer.initialize(window, factory.Get(), false) != E_INVALIDARG) failure = 11;
    if (!failure && (renderer.ready() || FAILED(renderer.prepareDeviceAsync(true)))) failure = 12;
    if (!failure && FAILED(renderer.initialize(window, factory.Get(), true))) failure = 13;
    if (!failure && (!renderer.ready() || !renderer.software() || renderer.width() != 32 || renderer.height() != 16)) failure = 14;
    uint8_t pixels[32 * 16 * 4];
    for (unsigned frame = 0; !failure && frame < 3; ++frame) {
        if (FAILED(renderer.beginFrame())) { failure = 15; break; }
        renderer.ui.Clear(D2D1::ColorF(1, 0, 0, 1));
        const HRESULT result = renderer.endFrame(pixels, 32 * 4);
        if (FAILED(result)) {
            fprintf(stderr, "renderer startup readback: 0x%08lx, %ux%u\n", (unsigned long)result, renderer.width(), renderer.height());
            failure = 16; break;
        }
        for (unsigned pixel = 0; pixel < 32 * 16; ++pixel) {
            if (pixels[pixel * 4] != 0 || pixels[pixel * 4 + 1] != 0 || pixels[pixel * 4 + 2] != 255 || pixels[pixel * 4 + 3] != 255) {
                failure = 17; break;
            }
        }
    }
    // Read back the actual material shader, including its square and rounded
    // variants. A compile-only check misses channel packing and edge coverage.
    if (!failure) {
        if (FAILED(renderer.beginFrame())) failure = 22;
        else {
            const auto size = renderer.ui.GetSize();
            renderer.ui.Clear(D2D1::ColorF(0x000000));
            renderer.ui.FillRoundedGradient(D2D1::RoundedRect(D2D1::RectF(0, 0, size.width / 2, size.height), 0, 0),
                D2D1::ColorF(0x204060), D2D1::ColorF(0x80a0c0));
            renderer.ui.FillRoundedGradient(D2D1::RoundedRect(D2D1::RectF(size.width / 2, 0, size.width, size.height), 4, 4),
                D2D1::ColorF(0x204060), D2D1::ColorF(0x80a0c0));
            if (FAILED(renderer.endFrame(pixels, 32 * 4))) failure = 23;
            else {
                // WindowsRenderer exposes bottom-up BGRA for the window BMP path.
                const auto pixel = [&](unsigned x, unsigned y, unsigned channel) { return pixels[((15 - y) * 32 + x) * 4 + channel]; };
                for (unsigned y = 1; y < 15; ++y) {
                    for (unsigned channel = 0; channel < 3; ++channel) {
                        if (pixel(8, y, channel) <= pixel(8, y - 1, channel) ||
                            pixel(8, y, channel) != pixel(24, y, channel)) failure = 24;
                    }
                }
                const int blueDelta = pixel(8, 8, 0) - pixel(8, 8, 2), greenDelta = pixel(8, 8, 1) - pixel(8, 8, 2);
                if (pixel(8, 0, 2) < 32 || pixel(8, 0, 2) > 42 || pixel(8, 15, 2) < 118 || pixel(8, 15, 2) > 128 ||
                    blueDelta < 63 || blueDelta > 65 || greenDelta < 31 || greenDelta > 33 ||
                    pixel(16, 0, 0) || pixel(31, 0, 0) || pixel(24, 8, 3) != 255) {
                    fprintf(stderr, "material pixels: top R=%u, bottom R=%u, B-R=%d, G-R=%d, corners=%u/%u, alpha=%u\n",
                        pixel(8, 0, 2), pixel(8, 15, 2), blueDelta, greenDelta, pixel(16, 0, 0), pixel(31, 0, 0), pixel(24, 8, 3));
                    failure = 25;
                }
            }
        }
    }
    if (FAILED(renderer.shutdown()) && !failure) failure = 18;
    // Recovery still supports synchronous initialization without an earlier warmup request.
    if (!failure && FAILED(renderer.initialize(window, factory.Get(), true))) failure = 19;
    if (FAILED(renderer.shutdown()) && !failure) failure = 20;
    DestroyWindow(window);
    return failure;
}
