#include "terminal_renderer.h"
#include <dxgi1_4.h>
#include <string.h>

// Offscreen WARP test: no window, desktop interaction or hardware GPU is required.
int testKittyImageRenderer() {
    RendererComPtr<IDXGIFactory4> factory;
    RendererComPtr<IDXGIAdapter> adapter;
    RendererComPtr<ID3D12Device> device;
    RendererComPtr<ID2D1Factory> d2d;
    RendererComPtr<ID3D12CommandQueue> queue;
    RendererComPtr<ID3D12Fence> fence;
    RendererComPtr<ID3D12Resource> target, readback;
    RendererComPtr<ID3D12DescriptorHeap> views;
    RendererComPtr<ID3D12CommandAllocator> allocator;
    RendererComPtr<ID3D12GraphicsCommandList> copy;
    HRESULT result = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result)) result = factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter));
    if (SUCCEEDED(result)) result = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
    if (SUCCEEDED(result)) result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &d2d);
    D3D12_COMMAND_QUEUE_DESC description = {};
    if (SUCCEEDED(result)) result = device->CreateCommandQueue(&description, IID_PPV_ARGS(&queue));
    if (SUCCEEDED(result)) result = device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
    if (FAILED(result)) return 1;
    D3D12_HEAP_PROPERTIES heap = {}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC texture = {};
    texture.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; texture.Width = 32; texture.Height = 16;
    texture.DepthOrArraySize = texture.MipLevels = 1; texture.SampleDesc.Count = 1;
    texture.Format = DXGI_FORMAT_B8G8R8A8_UNORM; texture.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    result = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &texture, D3D12_RESOURCE_STATE_PRESENT, nullptr, IID_PPV_ARGS(&target));
    D3D12_DESCRIPTOR_HEAP_DESC view = {}; view.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; view.NumDescriptors = 1;
    if (SUCCEEDED(result)) result = device->CreateDescriptorHeap(&view, IID_PPV_ARGS(&views));
    if (FAILED(result)) return 2;
    device->CreateRenderTargetView(target.Get(), nullptr, views->GetCPUDescriptorHandleForHeapStart());
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC buffer = {};
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; buffer.Width = 256 * 16;
    buffer.Height = buffer.DepthOrArraySize = buffer.MipLevels = 1; buffer.SampleDesc.Count = 1;
    buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    result = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback));
    if (SUCCEEDED(result)) result = device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
    if (SUCCEEDED(result)) result = device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&copy));
    if (FAILED(result)) return 3;
    copy->Close();
    const HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!event) return 4;
    TerminalRenderer renderer;
    result = renderer.initialize(device.Get(), d2d.Get());
    int failure = FAILED(result) ? 5 : 0;
    const float bounds[] = { 0, 0, 32, 16 }, uv[] = { 0, 0, 1, 1 };
    const uint8_t red[] = { 255, 0, 0, 128 }, green[] = { 0, 255, 0, 255 };
    UINT64 serial = 0;
    for (UINT frame = 0; !failure && frame < 4; ++frame) {
        result = renderer.beginFrame(frame % 2);
        if (SUCCEEDED(result)) result = renderer.beginPass(target.Get(), views->GetCPUDescriptorHandleForHeapStart(), { 0, 0, 32, 16 }, 32, 16, 96);
        if (SUCCEEDED(result)) result = renderer.drawSolid(0, 0, 32, 16, 0);
        if (SUCCEEDED(result)) result = renderer.drawImage(frame < 2 ? 1 : 2, frame < 2 ? red : green, 1, 1, bounds, uv);
        // Exercise a following glyph-atlas/solid batch after descriptor heap restoration.
        if (SUCCEEDED(result)) result = renderer.drawSolid(30, 0, 32, 16, 0x0000ff);
        if (SUCCEEDED(result)) result = renderer.endPass(queue.Get());
        if (FAILED(result)) { failure = 6; break; }
        result = allocator->Reset();
        if (SUCCEEDED(result)) result = copy->Reset(allocator.Get(), nullptr);
        if (FAILED(result)) { failure = 7; break; }
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = target.Get(); barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT; barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        copy->ResourceBarrier(1, &barrier);
        D3D12_TEXTURE_COPY_LOCATION source = {}, destination = {};
        source.pResource = target.Get(); source.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        destination.pResource = readback.Get(); destination.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        destination.PlacedFootprint.Footprint = { texture.Format, 32, 16, 1, 256 };
        copy->CopyTextureRegion(&destination, 0, 0, 0, &source, nullptr);
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE; barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        copy->ResourceBarrier(1, &barrier);
        result = copy->Close();
        if (FAILED(result)) { failure = 8; break; }
        ID3D12CommandList* lists[] = { copy.Get() }; queue->ExecuteCommandLists(1, lists);
        result = queue->Signal(fence.Get(), ++serial);
        if (SUCCEEDED(result)) result = fence->SetEventOnCompletion(serial, event);
        if (FAILED(result) || WaitForSingleObject(event, 15000) != WAIT_OBJECT_0) { failure = 9; break; }
        void* memory = nullptr;
        const D3D12_RANGE range = { 0, 256 * 16 };
        result = readback->Map(0, &range, &memory);
        if (FAILED(result)) { failure = 10; break; }
        const auto pixels = static_cast<const uint8_t*>(memory);
        const uint8_t expected[] = { 0, (uint8_t)(frame < 2 ? 0 : 255), (uint8_t)(frame < 2 ? 128 : 0) };
        if (memcmp(pixels + 8 * 256 + 16 * 4, expected, 3) || pixels[8 * 256 + 31 * 4] != 255) failure = 11;
        const D3D12_RANGE noWrite = { 0, 0 }; readback->Unmap(0, &noWrite);
        if (renderer.imageUploads != (frame < 2 ? 1u : 2u)) failure = 12;
    }
    if (!failure) {
        constexpr UINT width = 4096, height = 2048;
        const size_t bytes = (size_t)width * height * 4;
        auto pixels = static_cast<uint8_t*>(HeapAlloc(GetProcessHeap(), 0, bytes));
        if (!pixels) failure = 13;
        else {
            memset(pixels, 255, bytes);
            result = renderer.beginFrame(0);
            if (SUCCEEDED(result)) result = renderer.beginPass(target.Get(), views->GetCPUDescriptorHandleForHeapStart(), { 0, 0, 32, 16 }, 32, 16, 96);
            // Two maximal images fill the image-transfer budget, but must leave text uploads usable.
            if (SUCCEEDED(result)) result = renderer.drawImage(10, pixels, width, height, bounds, uv);
            if (SUCCEEDED(result)) result = renderer.drawImage(11, pixels, width, height, bounds, uv);
            if (SUCCEEDED(result)) result = renderer.drawImage(12, green, 1, 1, bounds, uv);
            if (SUCCEEDED(result)) result = renderer.drawSolid(0, 0, 32, 16, 0);
            if (SUCCEEDED(result)) result = renderer.endPass(queue.Get());
            if (FAILED(result) || renderer.imageUploads != 4) failure = 14;
            if (!failure) {
                // Reclaim only the other completed slot. Eviction here must retain textures still used by slot 0.
                result = renderer.beginFrame(1);
                if (SUCCEEDED(result)) result = renderer.beginPass(target.Get(), views->GetCPUDescriptorHandleForHeapStart(), { 0, 0, 32, 16 }, 32, 16, 96);
                if (SUCCEEDED(result)) result = renderer.drawImage(12, green, 1, 1, bounds, uv);
                if (SUCCEEDED(result)) result = renderer.endPass(queue.Get());
                if (FAILED(result) || renderer.imageUploads != 5) failure = 15;
            }
            HeapFree(GetProcessHeap(), 0, pixels);
        }
    }
    // Drain before releasing upload pages and per-frame retained textures, including failure paths.
    result = queue->Signal(fence.Get(), ++serial);
    if (SUCCEEDED(result)) result = fence->SetEventOnCompletion(serial, event);
    if (FAILED(result) || WaitForSingleObject(event, INFINITE) != WAIT_OBJECT_0 || FAILED(device->GetDeviceRemovedReason())) {
        if (!failure) failure = 16;
    }
    renderer.shutdown();
    CloseHandle(event);
    return failure;
}
