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
    if (SUCCEEDED(result)) result = ui.batches.initialize(device.Get(), factory, &terminal);
    if (FAILED(result)) return result;
    return createBuffers((float)GetDpiForWindow(window));
}

HRESULT WindowsRenderer::createBuffers(float dpi) {
    targetDpi = dpi;
    for (UINT index = 0; index < bufferCount; index++) {
        auto& frame = frames[index];
        HRESULT result = swapChain->GetBuffer(index, IID_PPV_ARGS(&frame.buffer));
        if (FAILED(result)) return result;
        frame.rtv = renderViews->GetCPUDescriptorHandleForHeapStart();
        frame.rtv.ptr += (SIZE_T)index * device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        device->CreateRenderTargetView(frame.buffer.Get(), nullptr, frame.rtv);
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
    if (nextFence == UINT64_MAX) return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
    const UINT64 value = nextFence++;
    result = queue->Signal(fence.Get(), value);
    if (FAILED(result)) return result;
    return waitForFence(value);
}

void WindowsRenderer::releaseBuffers() {
    for (auto& frame : frames) { frame.buffer.Reset(); frame.fenceValue = 0; }
}

HRESULT WindowsRenderer::resize(UINT width, UINT height, float dpi) {
    if (!ready() || phase != FramePhase::none || !width || !height || !isfinite(dpi) || dpi <= 0) return E_INVALIDARG;
    if (width == pixelWidth && height == pixelHeight && dpi == targetDpi) return S_OK;
    HRESULT result = waitIdle();
    if (FAILED(result)) return result;
    if (dpi != targetDpi) { terminal.clearGlyphCache(); ui.batches.clearGlyphCache(); }
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
    frameIndex = swapChain->GetCurrentBackBufferIndex();
    if (frameIndex >= bufferCount) return E_UNEXPECTED;
    auto& frame = frames[frameIndex];
    HRESULT result = waitForFence(frame.fenceValue);
    if (SUCCEEDED(result)) result = terminal.beginFrame(frameIndex);
    if (SUCCEEDED(result)) result = ui.batches.beginFrame(frameIndex);
    if (FAILED(result)) return result;
    ui.resetFrame(pixelWidth, pixelHeight, targetDpi);
    const D3D12_RECT clip = { 0, 0, (LONG)pixelWidth, (LONG)pixelHeight };
    result = ui.batches.beginPass(frame.buffer.Get(), frame.rtv, clip, pixelWidth, pixelHeight, targetDpi);
    if (SUCCEEDED(result)) phase = FramePhase::ui;
    return result;
}

HRESULT WindowsRenderer::terminalResult(HRESULT result) {
    if (FAILED(result) && SUCCEEDED(frameError)) frameError = result;
    return result;
}

HRESULT WindowsRenderer::beginTerminalPass(D3D12_RECT clip) {
    if (phase != FramePhase::ui) return terminalResult(E_UNEXPECTED);
    HRESULT result = ui.batches.endPass(queue.Get());
    if (SUCCEEDED(result)) result = ui.result();
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
        const D3D12_RECT clip = { 0, 0, (LONG)pixelWidth, (LONG)pixelHeight };
        result = ui.batches.beginPass(frames[frameIndex].buffer.Get(), frames[frameIndex].rtv, clip, pixelWidth, pixelHeight, targetDpi);
        if (SUCCEEDED(result)) { phase = FramePhase::ui; result = ui.restoreClip(); }
    }
    return terminalResult(result);
}

HRESULT WindowsRenderer::readPixels(void* pixels, UINT stride) {
    if (!pixels || stride != pixelWidth * 4) return E_INVALIDARG;
    auto& frame = frames[frameIndex];
    const auto description = frame.buffer->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = {};
    UINT64 bytes = 0;
    device->GetCopyableFootprints(&description, 0, 1, 0, &footprint, nullptr, nullptr, &bytes);
    if (!bytes || bytes > SIZE_MAX || footprint.Footprint.RowPitch < stride) return E_UNEXPECTED;
    D3D12_HEAP_PROPERTIES heap = {}; heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC buffer = {};
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; buffer.Width = bytes;
    buffer.Height = 1; buffer.DepthOrArraySize = 1; buffer.MipLevels = 1;
    buffer.SampleDesc.Count = 1; buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> staging;
    HRESULT result = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer,
        D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&staging));
    if (FAILED(result)) return result;
    ComPtr<ID3D12CommandAllocator> allocator;
    result = device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
    if (FAILED(result)) return result;
    ComPtr<ID3D12GraphicsCommandList> copy;
    result = device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&copy));
    if (FAILED(result)) return result;
    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = frame.buffer.Get();
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    copy->ResourceBarrier(1, &barrier);
    D3D12_TEXTURE_COPY_LOCATION from = {}, to = {};
    from.pResource = frame.buffer.Get(); from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    to.pResource = staging.Get(); to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; to.PlacedFootprint = footprint;
    copy->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    copy->ResourceBarrier(1, &barrier);
    result = copy->Close();
    if (FAILED(result)) return result;
    ID3D12CommandList* lists[] = { copy.Get() }; queue->ExecuteCommandLists(1, lists);
    result = waitIdle();
    if (FAILED(result)) {
        // Capture-local buffers cannot be released while a live GPU may still reference them.
        if (!deviceLost(result)) RaiseFailFastException(nullptr, nullptr, 0);
        return result;
    }
    void* mapped = nullptr;
    const D3D12_RANGE read = { 0, (SIZE_T)bytes };
    result = staging->Map(0, &read, &mapped);
    if (FAILED(result)) return result;
    if (!mapped) return E_UNEXPECTED;
    for (UINT row = 0; row < pixelHeight; row++) {
        memcpy(static_cast<uint8_t*>(pixels) + (size_t)(pixelHeight - row - 1) * stride,
            static_cast<const uint8_t*>(mapped) + footprint.Offset + (size_t)row * footprint.Footprint.RowPitch, stride);
    }
    const D3D12_RANGE noWrites = { 0, 0 }; staging->Unmap(0, &noWrites);
    return S_OK;
}

HRESULT WindowsRenderer::endFrame(void* pixels, UINT stride) {
    if (phase == FramePhase::terminal) endTerminalPass();
    if (phase != FramePhase::ui) return FAILED(frameError) ? frameError : E_UNEXPECTED;
    HRESULT result = ui.batches.endPass(queue.Get());
    phase = FramePhase::none;
    if (SUCCEEDED(result)) result = frameError;
    if (SUCCEEDED(result)) result = ui.result();
    if (SUCCEEDED(result) && pixels) result = readPixels(pixels, stride);
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

HRESULT WindowsRenderer::trimUnusedTerminalUploads() {
    if (!ready() || phase != FramePhase::none) return S_OK;
    const UINT64 completed = fence->GetCompletedValue();
    if (completed == UINT64_MAX) return device->GetDeviceRemovedReason();
    const ULONGLONG now = GetTickCount64();
    // Maintenance never waits on the GPU or treats cursor-only presents as upload activity.
    for (UINT index = 0; index < bufferCount; index++) {
        if (completed >= frames[index].fenceValue) {
            terminal.trimUnusedUploads(index, now);
            ui.batches.trimUnusedUploads(index, now);
        }
    }
    return S_OK;
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
    terminal.shutdown(); ui.batches.shutdown();
    releaseBuffers();
    renderViews.Reset();
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
