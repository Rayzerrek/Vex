#include "windows_renderer.h"
#include <d3d12sdklayers.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

WindowsRenderer::~WindowsRenderer() { shutdown(); }

bool WindowsRenderer::deviceLost(HRESULT result) {
    return result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET ||
        result == DXGI_ERROR_DEVICE_HUNG || result == D2DERR_RECREATE_TARGET;
}

HRESULT WindowsRenderer::initialize(HWND window, ID2D1Factory1* factory, bool forceWarp) {
    if (ready() || !factory || !IsWindow(window)) return E_INVALIDARG;
    HRESULT result = initializeDevice(window, factory, forceWarp);
    if (FAILED(result)) shutdown();
    return result;
}

HRESULT WindowsRenderer::initializeDevice(HWND window, ID2D1Factory1* factory, bool forceWarp) {
#ifdef VEX_GRAPHICS_DEBUG
    ComPtr<ID3D12Debug> debug;
    HRESULT result = D3D12GetDebugInterface(IID_PPV_ARGS(&debug));
    if (FAILED(result)) return result;
    debug->EnableDebugLayer();
#else
    HRESULT result;
#endif
    result = CreateDXGIFactory2(0, IID_PPV_ARGS(&dxgiFactory));
    if (FAILED(result)) return result;
    if (!forceWarp) {
        for (UINT index = 0; ; index++) {
            ComPtr<IDXGIAdapter1> adapter;
            result = dxgiFactory->EnumAdapters1(index, &adapter);
            if (result == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(result)) return result;
            DXGI_ADAPTER_DESC1 description = {};
            result = adapter->GetDesc1(&description);
            if (FAILED(result)) return result;
            if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            result = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
            if (SUCCEEDED(result)) break;
            if (result != DXGI_ERROR_UNSUPPORTED && result != E_INVALIDARG) return result;
        }
    }
    usesWarp = !device;
    if (usesWarp) {
        ComPtr<IDXGIAdapter> adapter;
        result = dxgiFactory->EnumWarpAdapter(IID_PPV_ARGS(&adapter));
        if (FAILED(result)) return result;
        result = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
        if (FAILED(result)) return result;
        OutputDebugStringA("Vex renderer: D3D12 WARP software adapter\n");
    }
    D3D12_COMMAND_QUEUE_DESC queueDescription = {};
    queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    result = device->CreateCommandQueue(&queueDescription, IID_PPV_ARGS(&queue));
    if (FAILED(result)) return result;
    result = device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
    if (FAILED(result)) return result;
    fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!fenceEvent) return HRESULT_FROM_WIN32(GetLastError());

    IUnknown* queues[] = { queue.Get() };
    result = D3D11On12CreateDevice(device.Get(), D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
        queues, 1, 0, &translationDevice, &translationContext, nullptr);
    if (FAILED(result)) return result;
    result = translationDevice.As(&interop);
    if (FAILED(result)) return result;
    ComPtr<IDXGIDevice> dxgiDevice;
    result = translationDevice.As(&dxgiDevice);
    if (FAILED(result)) return result;
    result = factory->CreateDevice(dxgiDevice.Get(), &drawingDevice);
    if (FAILED(result)) return result;
    result = drawingDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context);
    if (FAILED(result)) return result;

    RECT bounds;
    if (!GetClientRect(window, &bounds)) return HRESULT_FROM_WIN32(GetLastError());
    pixelWidth = bounds.right > 0 ? (UINT)bounds.right : 1;
    pixelHeight = bounds.bottom > 0 ? (UINT)bounds.bottom : 1;
    DXGI_SWAP_CHAIN_DESC1 description = {};
    description.Width = pixelWidth;
    description.Height = pixelHeight;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = bufferCount;
    // Every invalidated frame redraws the whole scene; no discarded buffer contents are reused.
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    ComPtr<IDXGISwapChain1> chain;
    result = dxgiFactory->CreateSwapChainForHwnd(queue.Get(), window, &description, nullptr, nullptr, &chain);
    if (FAILED(result)) return result;
    result = dxgiFactory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
    if (FAILED(result)) return result;
    result = chain.As(&swapChain);
    if (FAILED(result)) return result;
    D3D12_DESCRIPTOR_HEAP_DESC views = {};
    views.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; views.NumDescriptors = bufferCount;
    result = device->CreateDescriptorHeap(&views, IID_PPV_ARGS(&renderViews));
    if (FAILED(result)) return result;
    result = terminal.initialize(device.Get(), factory);
    if (FAILED(result)) return result;
    return createBuffers((float)GetDpiForWindow(window));
}

HRESULT WindowsRenderer::createBuffers(float dpi) {
    targetDpi = dpi;
    context->SetDpi(dpi, dpi);
    const auto properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), dpi, dpi);
    for (UINT index = 0; index < bufferCount; index++) {
        auto& frame = frames[index];
        HRESULT result = swapChain->GetBuffer(index, IID_PPV_ARGS(&frame.buffer));
        if (FAILED(result)) return result;
        frame.rtv = renderViews->GetCPUDescriptorHandleForHeapStart();
        frame.rtv.ptr += (SIZE_T)index * device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        device->CreateRenderTargetView(frame.buffer.Get(), nullptr, frame.rtv);
        D3D11_RESOURCE_FLAGS flags = {};
        flags.BindFlags = D3D11_BIND_RENDER_TARGET;
        // No native D3D12 draw pass yet: 11On12 transitions PRESENT into its drawing states.
        result = interop->CreateWrappedResource(frame.buffer.Get(), &flags,
            D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_PRESENT, IID_PPV_ARGS(&frame.wrapped));
        if (FAILED(result)) return result;
        ComPtr<IDXGISurface> surface;
        result = frame.wrapped.As(&surface);
        if (FAILED(result)) return result;
        result = context->CreateBitmapFromDxgiSurface(surface.Get(), &properties, &frame.bitmap);
        if (FAILED(result)) return result;
    }
    return S_OK;
}

HRESULT WindowsRenderer::waitForFence(UINT64 value) {
    if (!value) return S_OK;
    UINT64 completed = fence->GetCompletedValue();
    if (completed == UINT64_MAX) return device->GetDeviceRemovedReason();
    if (completed >= value) return S_OK;
    HRESULT result = fence->SetEventOnCompletion(value, fenceEvent);
    if (FAILED(result)) return result;
    const DWORD status = WaitForSingleObject(fenceEvent, 5000);
    if (status != WAIT_OBJECT_0) {
        result = device->GetDeviceRemovedReason();
        if (FAILED(result)) return result;
        return HRESULT_FROM_WIN32(status == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError());
    }
    completed = fence->GetCompletedValue();
    if (completed == UINT64_MAX) return device->GetDeviceRemovedReason();
    return completed >= value ? S_OK : E_UNEXPECTED;
}

HRESULT WindowsRenderer::waitIdle() {
    if (!queue || !fence || !fenceEvent) return S_OK;
    HRESULT result = device->GetDeviceRemovedReason();
    if (FAILED(result)) return result;
    if (translationContext) translationContext->Flush();
    if (nextFence == UINT64_MAX) return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
    const UINT64 value = nextFence++;
    result = queue->Signal(fence.Get(), value);
    if (FAILED(result)) return result;
    return waitForFence(value);
}

void WindowsRenderer::releaseBuffers() {
    if (context) context->SetTarget(nullptr);
    if (translationContext) translationContext->ClearState();
    for (auto& frame : frames) {
        frame.bitmap.Reset();
        frame.wrapped.Reset();
    }
    // Wrapped resources use deferred destruction. Flush only after dropping every view/reference.
    if (translationContext) translationContext->Flush();
    for (auto& frame : frames) { frame.buffer.Reset(); frame.fenceValue = 0; }
}

HRESULT WindowsRenderer::resize(UINT width, UINT height, float dpi) {
    if (!ready() || phase != FramePhase::none || !width || !height || !isfinite(dpi) || dpi <= 0) return E_INVALIDARG;
    if (width == pixelWidth && height == pixelHeight && dpi == targetDpi) return S_OK;
    HRESULT result = waitIdle();
    if (FAILED(result)) return result;
    if (dpi != targetDpi) terminal.clearGlyphCache();
    releaseBuffers();
    if (width != pixelWidth || height != pixelHeight) {
        result = swapChain->ResizeBuffers(bufferCount, width, height, DXGI_FORMAT_UNKNOWN, 0);
        if (FAILED(result)) return result;
    }
    pixelWidth = width; pixelHeight = height;
    return createBuffers(dpi);
}

HRESULT WindowsRenderer::beginFrame() {
    if (!ready() || phase != FramePhase::none) return E_UNEXPECTED;
    frameError = S_OK;
    lastFrameMs = GetTickCount64();
    frameIndex = swapChain->GetCurrentBackBufferIndex();
    if (frameIndex >= bufferCount) return E_UNEXPECTED;
    auto& frame = frames[frameIndex];
    HRESULT result = waitForFence(frame.fenceValue);
    if (SUCCEEDED(result)) result = terminal.beginFrame(frameIndex);
    if (FAILED(result)) return result;
    ID3D11Resource* resources[] = { frame.wrapped.Get() };
    interop->AcquireWrappedResources(resources, 1);
    context->SetTarget(frame.bitmap.Get());
    context->BeginDraw();
    phase = FramePhase::ui;
    return S_OK;
}

HRESULT WindowsRenderer::terminalResult(HRESULT result) {
    if (FAILED(result) && SUCCEEDED(frameError)) frameError = result;
    return result;
}

HRESULT WindowsRenderer::beginTerminalPass(D3D12_RECT clip) {
    if (phase != FramePhase::ui) return terminalResult(E_UNEXPECTED);
    HRESULT result = context->EndDraw();
    context->SetTarget(nullptr);
    ID3D11Resource* resources[] = { frames[frameIndex].wrapped.Get() };
    interop->ReleaseWrappedResources(resources, 1);
    translationContext->Flush();
    phase = FramePhase::none;
    if (SUCCEEDED(result)) result = terminal.beginPass(frames[frameIndex].buffer.Get(), frames[frameIndex].rtv, clip, pixelWidth, pixelHeight, targetDpi);
    if (SUCCEEDED(result)) phase = FramePhase::terminal;
    return terminalResult(result);
}

HRESULT WindowsRenderer::endTerminalPass() {
    if (phase != FramePhase::terminal) return terminalResult(E_UNEXPECTED);
    HRESULT result = terminal.endPass(queue.Get());
    phase = FramePhase::none;
    if (SUCCEEDED(result)) {
        ID3D11Resource* resources[] = { frames[frameIndex].wrapped.Get() };
        interop->AcquireWrappedResources(resources, 1);
        context->SetTarget(frames[frameIndex].bitmap.Get());
        context->BeginDraw();
        phase = FramePhase::ui;
    }
    return terminalResult(result);
}

HRESULT WindowsRenderer::readPixels(void* pixels, UINT stride) {
    if (!pixels || stride != pixelWidth * 4) return E_INVALIDARG;
    ComPtr<ID3D11Texture2D> source;
    HRESULT result = frames[frameIndex].wrapped.As(&source);
    if (FAILED(result)) return result;
    D3D11_TEXTURE2D_DESC description;
    source->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.MiscFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    result = translationDevice->CreateTexture2D(&description, nullptr, &staging);
    if (FAILED(result)) return result;
    translationContext->CopyResource(staging.Get(), source.Get());
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    result = translationContext->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(result)) return result;
    if (mapped.RowPitch < stride) { translationContext->Unmap(staging.Get(), 0); return E_UNEXPECTED; }
    for (UINT row = 0; row < pixelHeight; row++) {
        memcpy(static_cast<uint8_t*>(pixels) + (size_t)(pixelHeight - row - 1) * stride,
            static_cast<const uint8_t*>(mapped.pData) + (size_t)row * mapped.RowPitch, stride);
    }
    translationContext->Unmap(staging.Get(), 0);
    return S_OK;
}

HRESULT WindowsRenderer::endFrame(void* pixels, UINT stride) {
    if (phase == FramePhase::terminal) endTerminalPass();
    if (phase != FramePhase::ui) return FAILED(frameError) ? frameError : E_UNEXPECTED;
    HRESULT result = context->EndDraw();
    if (SUCCEEDED(result)) result = frameError;
    context->SetTarget(nullptr);
    if (SUCCEEDED(result) && pixels) result = readPixels(pixels, stride);
    ID3D11Resource* resources[] = { frames[frameIndex].wrapped.Get() };
    interop->ReleaseWrappedResources(resources, 1);
    translationContext->Flush();
    phase = FramePhase::none;
    // Always release the acquired resource, including failed EndDraw/readback paths.
    if (FAILED(result)) return result;
    if (nextFence == UINT64_MAX) return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
    const UINT64 value = nextFence++;
    result = queue->Signal(fence.Get(), value);
    if (FAILED(result)) return result;
    frames[frameIndex].fenceValue = value;
    result = swapChain->Present(1, 0);
    if (SUCCEEDED(result)) presented++;
    return result;
}

HRESULT WindowsRenderer::trimTerminalUploadsIfIdle() {
    if (!ready() || phase != FramePhase::none || GetTickCount64() - lastFrameMs < 2000 || !terminal.hasExcessUploads()) return S_OK;
    HRESULT result = waitIdle();
    if (SUCCEEDED(result)) terminal.trimUploads();
    return result;
}

HRESULT WindowsRenderer::videoMemoryUsage(bool local, UINT64& bytes) const {
    if (!device || !dxgiFactory) return E_UNEXPECTED;
    ComPtr<IDXGIAdapter3> adapter;
    HRESULT result = dxgiFactory->EnumAdapterByLuid(device->GetAdapterLuid(), IID_PPV_ARGS(&adapter));
    if (FAILED(result)) return result;
    DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
    result = adapter->QueryVideoMemoryInfo(0, local ? DXGI_MEMORY_SEGMENT_GROUP_LOCAL : DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &info);
    if (SUCCEEDED(result)) bytes = info.CurrentUsage;
    return result;
}

bool WindowsRenderer::debugValidationEnabled() const {
    ComPtr<ID3D12InfoQueue> messages;
    return device && SUCCEEDED(device.As(&messages));
}

UINT64 WindowsRenderer::debugErrorCount() const {
#ifdef VEX_GRAPHICS_DEBUG
    ComPtr<ID3D12InfoQueue> messages;
    if (device && SUCCEEDED(device.As(&messages))) {
        UINT64 errors = 0;
        wchar_t logPath[32768];
        const DWORD length = GetEnvironmentVariableW(L"VEX_NATIVE_GRAPHICS_LOG", logPath, 32768);
        HANDLE log = length && length < 32768 ? CreateFileW(logPath, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr) : INVALID_HANDLE_VALUE;
        for (UINT64 index = 0; index < messages->GetNumStoredMessages(); index++) {
            alignas(D3D12_MESSAGE) uint8_t storage[4096];
            SIZE_T size = sizeof(storage);
            auto message = reinterpret_cast<D3D12_MESSAGE*>(storage);
            if (FAILED(messages->GetMessage(index, message, &size))) { errors++; continue; }
            if (log != INVALID_HANDLE_VALUE) {
                DWORD written;
                char prefix[80];
                const int count = snprintf(prefix, sizeof(prefix), "[severity=%u id=%u] ", (unsigned)message->Severity, (unsigned)message->ID);
                if (count > 0 && (size_t)count < sizeof(prefix)) WriteFile(log, prefix, (DWORD)count, &written, nullptr);
                WriteFile(log, message->pDescription, (DWORD)strlen(message->pDescription), &written, nullptr);
                WriteFile(log, "\r\n", 2, &written, nullptr);
            }
            // Keep performance warnings in the diagnostic log; fail on validation errors/corruption.
            if (message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR) errors++;
        }
        if (log != INVALID_HANDLE_VALUE) CloseHandle(log);
        return errors;
    }
#endif
    return 0;
}

void WindowsRenderer::releaseDevice() {
    terminal.shutdown();
    releaseBuffers();
    renderViews.Reset();
    context.Reset(); drawingDevice.Reset(); interop.Reset();
    translationContext.Reset(); translationDevice.Reset();
    swapChain.Reset(); fence.Reset(); queue.Reset(); device.Reset(); dxgiFactory.Reset();
    if (fenceEvent) { CloseHandle(fenceEvent); fenceEvent = nullptr; }
    pixelWidth = 0; pixelHeight = 0; nextFence = 1; phase = FramePhase::none;
}

HRESULT WindowsRenderer::shutdown() {
    HRESULT result = waitIdle();
    if (FAILED(result) && !deviceLost(result)) {
        // We cannot safely destroy native D3D12 resources still referenced by a live GPU.
        OutputDebugStringA("Vex renderer: GPU queue could not be drained; terminating without unsafe resource teardown\n");
        RaiseFailFastException(nullptr, nullptr, 0);
    }
    releaseDevice();
    return result;
}
