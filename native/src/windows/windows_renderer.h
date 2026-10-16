#pragma once
#include "windows_com.h"
#include "terminal_renderer.h"
#include <d2d1_1.h>
#include <d3d11on12.h>
#include <d3d12.h>
#include <dxgi1_4.h>

// Window-thread owner. D3D12 owns presentation; Direct2D uses the same queue via 11On12.
class WindowsRenderer final {
public:
    WindowsRenderer() = default;
    WindowsRenderer(const WindowsRenderer&) = delete;
    WindowsRenderer& operator=(const WindowsRenderer&) = delete;
    ~WindowsRenderer();

    HRESULT initialize(HWND window, ID2D1Factory1* factory, bool forceWarp);
    HRESULT resize(UINT width, UINT height, float dpi);
    HRESULT beginFrame();
    // Optional BGRA readback is bottom-up, tightly packed, and only used by desktop capture.
    HRESULT endFrame(void* pixels = nullptr, UINT stride = 0);
    HRESULT shutdown();
    HRESULT beginTerminalPass(D3D12_RECT clip);
    HRESULT endTerminalPass();
    HRESULT terminalResult(HRESULT result);
    TerminalRenderer terminal;
    ID2D1RenderTarget* drawingTarget() const { return context.Get(); }
    bool ready() const { return swapChain.Get() != nullptr; }
    bool software() const { return usesWarp; }
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
        ComPtr<ID3D11Resource> wrapped;
        ComPtr<ID2D1Bitmap1> bitmap;
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = {};
        UINT64 fenceValue = 0;
    };
    ComPtr<IDXGIFactory4> dxgiFactory;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12Fence> fence;
    ComPtr<IDXGISwapChain3> swapChain;
    ComPtr<ID3D12DescriptorHeap> renderViews;
    ComPtr<ID3D11Device> translationDevice;
    ComPtr<ID3D11DeviceContext> translationContext;
    ComPtr<ID3D11On12Device> interop;
    ComPtr<ID2D1Device> drawingDevice;
    ComPtr<ID2D1DeviceContext> context;
    Frame frames[bufferCount];
    HANDLE fenceEvent = nullptr;
    UINT64 nextFence = 1;
    UINT64 presented = 0;
    UINT pixelWidth = 0, pixelHeight = 0, frameIndex = 0;
    float targetDpi = 0;
    bool usesWarp = false;
    enum class FramePhase { none, ui, terminal };
    FramePhase phase = FramePhase::none;
    HRESULT frameError = S_OK;

    HRESULT initializeDevice(HWND window, ID2D1Factory1* factory, bool forceWarp);
    HRESULT createBuffers(float dpi);
    HRESULT waitForFence(UINT64 value);
    HRESULT waitIdle();
    HRESULT readPixels(void* pixels, UINT stride);
    void releaseBuffers();
    void releaseDevice();
};
