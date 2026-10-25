#pragma once
#include "windows_com.h"
#include "terminal_renderer.h"
#include "ui_renderer.h"
#include <d2d1_1.h>
#include <d3d12.h>
#include <dxgi1_4.h>

// Window-thread owner after device preparation joins. UI, terminal and capture share one D3D12 queue.
class WindowsRenderer final {
public:
    WindowsRenderer() = default;
    WindowsRenderer(const WindowsRenderer&) = delete;
    WindowsRenderer& operator=(const WindowsRenderer&) = delete;
    ~WindowsRenderer();

    // Only adapter/device/queue creation runs off-thread; HWND and D2D resources stay on the window thread.
    HRESULT prepareDeviceAsync(bool forceWarp);
    HRESULT finishDevicePreparation();
    HRESULT initialize(HWND window, ID2D1Factory1* factory, bool forceWarp, void (*milestone)(const char*) = nullptr);
    HRESULT resize(UINT width, UINT height, float dpi);
    HRESULT beginFrame();
    // Optional BGRA readback is bottom-up, tightly packed, and only used by desktop capture.
    HRESULT endFrame(void* pixels = nullptr, UINT stride = 0);
    HRESULT shutdown();
    HRESULT beginTerminalPass(D3D12_RECT clip);
    HRESULT endTerminalPass();
    HRESULT terminalResult(HRESULT result);
    TerminalRenderer terminal;
    UiRenderer ui;
    UiRenderer* drawingTarget() { return &ui; }
    bool ready() const { return swapChain.Get() != nullptr; }
    bool software() const { return ready() && usesWarp; }
    UINT width() const { return pixelWidth; }
    UINT height() const { return pixelHeight; }
    UINT64 framesPresented() const { return presented; }
    UINT64 debugErrorCount() const;
    bool debugValidationEnabled() const;
    HRESULT videoMemoryUsage(bool local, UINT64& bytes) const;
    HRESULT trimUnusedTerminalUploads();
    static bool deviceLost(HRESULT result);

private:
    template<class T> using ComPtr = RendererComPtr<T>;
    static constexpr UINT bufferCount = 2;
    struct Frame {
        ComPtr<ID3D12Resource> buffer;
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = {};
        UINT64 fenceValue = 0;
    };
    ComPtr<IDXGIFactory4> dxgiFactory;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence;
    ComPtr<IDXGISwapChain3> swapChain;
    ComPtr<ID3D12DescriptorHeap> renderViews;
    Frame frames[bufferCount];
    HANDLE fenceEvent = nullptr;
    HANDLE deviceThread = nullptr;
    HRESULT devicePreparationResult = S_OK;
    bool devicePreparationRequested = false;
    bool deviceForceWarp = false;
    UINT64 nextFence = 1;
    UINT64 presented = 0;
    UINT pixelWidth = 0, pixelHeight = 0, frameIndex = 0;
    float targetDpi = 0;
    bool usesWarp = false;
    enum class FramePhase { none, ui, terminal };
    FramePhase phase = FramePhase::none;
    HRESULT frameError = S_OK;

    static DWORD WINAPI prepareDeviceWorker(void* context);
    HRESULT initializeAdapter(bool forceWarp);
    HRESULT initializeDevice(HWND window, ID2D1Factory1* factory, bool forceWarp, void (*milestone)(const char*));
    HRESULT createBuffers(float dpi);
    HRESULT waitForFence(UINT64 value);
    HRESULT waitIdle();
    HRESULT readPixels(void* pixels, UINT stride);
    void releaseBuffers();
    void releaseDevice();
};
