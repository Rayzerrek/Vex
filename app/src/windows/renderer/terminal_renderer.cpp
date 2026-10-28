#include "terminal_renderer.h"
#include "terminal_shaders.generated.h"
#include <math.h>
#include <string.h>

static D3D12_RESOURCE_BARRIER terminalTransition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    return barrier;
}

HRESULT TerminalRenderer::initialize(ID3D12Device* device, ID2D1Factory* factory, const TerminalRenderer* sharedPipeline) {
    if (!device || !factory || deviceOwner) return E_INVALIDARG;
    HRESULT result = device->QueryInterface(IID_PPV_ARGS(&deviceOwner));
    if (FAILED(result)) return result;
    result = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    apartmentOwned = SUCCEEDED(result);
    if (FAILED(result) && result != RPC_E_CHANGED_MODE) return result;
    result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&imagingFactory));
    if (FAILED(result)) return result;
    result = imagingFactory->CreateBitmap(512, 512, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, &rasterBitmap);
    if (FAILED(result)) return result;
    const auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    result = factory->CreateWicBitmapRenderTarget(rasterBitmap.Get(), properties, &rasterTarget);
    if (FAILED(result)) return result;
    rasterTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    result = rasterTarget->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), &rasterBrush);
    if (FAILED(result)) return result;
    return createPipeline(sharedPipeline);
}

HRESULT TerminalRenderer::createPipelineState() {
    D3D12_DESCRIPTOR_RANGE range = {};
    range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; range.NumDescriptors = 1;
    D3D12_ROOT_PARAMETER parameters[2] = {};
    parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
    parameters[0].Constants.Num32BitValues = 2;
    parameters[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;
    parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    parameters[1].DescriptorTable.NumDescriptorRanges = 1;
    parameters[1].DescriptorTable.pDescriptorRanges = &range;
    parameters[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    D3D12_STATIC_SAMPLER_DESC sampler = {};
    sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    sampler.MaxLOD = D3D12_FLOAT32_MAX; sampler.MaxAnisotropy = 1;
    sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_ALWAYS;
    sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    D3D12_ROOT_SIGNATURE_DESC rootDescription = {};
    rootDescription.NumParameters = 2; rootDescription.pParameters = parameters;
    D3D12_STATIC_SAMPLER_DESC samplers[] = { sampler, sampler };
    samplers[1].ShaderRegister = 1;
    samplers[1].Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
    rootDescription.NumStaticSamplers = 2; rootDescription.pStaticSamplers = samplers;
    rootDescription.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
    ComPtr<ID3DBlob> signature, errors;
    HRESULT result = D3D12SerializeRootSignature(&rootDescription, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &errors);
    if (FAILED(result)) return result;
    result = deviceOwner->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&root));
    if (FAILED(result)) return result;
    const D3D12_INPUT_ELEMENT_DESC input[] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
        { "TEXCOORD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 16, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
        { "COLOR", 0, DXGI_FORMAT_R8G8B8A8_UNORM, 0, 32, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
        { "TYPE", 0, DXGI_FORMAT_R32_UINT, 0, 36, D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA, 1 },
    };
    static_assert(sizeof(Quad) == 40);
    D3D12_GRAPHICS_PIPELINE_STATE_DESC description = {};
    description.pRootSignature = root.Get();
    description.VS = { terminalVertexShader, sizeof(terminalVertexShader) };
    description.PS = { terminalPixelShader, sizeof(terminalPixelShader) };
    description.InputLayout = { input, 4 };
    description.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
    description.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
    description.RasterizerState.DepthClipEnable = TRUE;
    auto& blend = description.BlendState.RenderTarget[0];
    blend.BlendEnable = TRUE;
    blend.SrcBlend = D3D12_BLEND_SRC_ALPHA; blend.DestBlend = D3D12_BLEND_INV_SRC_ALPHA; blend.BlendOp = D3D12_BLEND_OP_ADD;
    blend.SrcBlendAlpha = D3D12_BLEND_ONE; blend.DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA; blend.BlendOpAlpha = D3D12_BLEND_OP_ADD;
    blend.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    description.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
    description.SampleMask = UINT_MAX; description.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    description.NumRenderTargets = 1; description.RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    return deviceOwner->CreateGraphicsPipelineState(&description, IID_PPV_ARGS(&pipeline));
}

HRESULT TerminalRenderer::createPipeline(const TerminalRenderer* sharedPipeline) {
    HRESULT result;
    if (sharedPipeline) {
        if (sharedPipeline->deviceOwner.Get() != deviceOwner.Get() || !sharedPipeline->root || !sharedPipeline->pipeline) return E_INVALIDARG;
        result = sharedPipeline->root.As(&root);
        if (SUCCEEDED(result)) result = sharedPipeline->pipeline.As(&pipeline);
    } else result = createPipelineState();
    if (FAILED(result)) return result;
    D3D12_DESCRIPTOR_HEAP_DESC heap = {};
    heap.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; heap.NumDescriptors = 1;
    heap.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    result = deviceOwner->CreateDescriptorHeap(&heap, IID_PPV_ARGS(&descriptors));
    if (FAILED(result)) return result;
    D3D12_HEAP_PROPERTIES memory = {}; memory.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC texture = {};
    texture.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; texture.Width = atlasSize; texture.Height = atlasSize;
    texture.DepthOrArraySize = 1; texture.MipLevels = 1; texture.Format = DXGI_FORMAT_R8_UNORM; texture.SampleDesc.Count = 1;
    result = deviceOwner->CreateCommittedResource(&memory, D3D12_HEAP_FLAG_NONE, &texture,
        D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, nullptr, IID_PPV_ARGS(&atlas));
    if (FAILED(result)) return result;
    D3D12_SHADER_RESOURCE_VIEW_DESC view = {};
    view.Format = texture.Format; view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    view.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; view.Texture2D.MipLevels = 1;
    deviceOwner->CreateShaderResourceView(atlas.Get(), &view, descriptors->GetCPUDescriptorHandleForHeapStart());
    for (auto& frame : frames) {
        result = deviceOwner->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&frame.allocator));
        if (FAILED(result)) return result;
    }
    result = deviceOwner->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, frames[0].allocator.Get(), pipeline.Get(), IID_PPV_ARGS(&commands));
    if (FAILED(result)) return result;
    return commands->Close();
}

HRESULT TerminalRenderer::beginFrame(UINT index) {
    if (!deviceOwner || index >= frameCount || passOpen) return E_UNEXPECTED;
    frameIndex = index;
    frameTimeMs = GetTickCount64();
    auto& frame = frames[index];
    // beginFrame is called only after this slot's fence completes.
    releaseFrameImages(frame);
    trimCachedImages(frameTimeMs);
    frame.pageIndex = 0;
    for (UINT i = 0; i < frame.pageCount; i++) frame.pages[i].used = 0;
    return frame.allocator->Reset();
}

HRESULT TerminalRenderer::beginPass(ID3D12Resource* buffer, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_RECT clip, UINT width, UINT height, float dpi) {
    if (passOpen || !buffer || !width || !height || !isfinite(dpi) || dpi <= 0) return E_INVALIDARG;
    HRESULT result = commands->Reset(frames[frameIndex].allocator.Get(), pipeline.Get());
    if (FAILED(result)) return result;
    passBuffer = buffer; passOpen = true; pendingQuads = 0; scale = dpi / 96.0f;
    auto barrier = terminalTransition(buffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
    commands->ResourceBarrier(1, &barrier);
    commands->SetGraphicsRootSignature(root.Get());
    ID3D12DescriptorHeap* heaps[] = { descriptors.Get() };
    commands->SetDescriptorHeaps(1, heaps);
    commands->SetGraphicsRootDescriptorTable(1, descriptors->GetGPUDescriptorHandleForHeapStart());
    boundTexture = descriptors.Get();
    const float viewportSize[] = { (float)width, (float)height };
    commands->SetGraphicsRoot32BitConstants(0, 2, viewportSize, 0);
    D3D12_VIEWPORT viewport = { 0, 0, (float)width, (float)height, 0, 1 };
    commands->RSSetViewports(1, &viewport); commands->RSSetScissorRects(1, &clip);
    commands->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
    commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    rasterTarget->SetDpi(dpi, dpi);
    return S_OK;
}

HRESULT TerminalRenderer::allocateUpload(UINT bytes, UINT alignment, UINT& page, UINT& offset) {
    if (!passOpen || !bytes || bytes > pageBytes || !alignment || (alignment & (alignment - 1))) return E_INVALIDARG;
    auto& frame = frames[frameIndex];
    for (;;) {
        if (frame.pageIndex == maxPages) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
        page = frame.pageIndex;
        auto& upload = frame.pages[page];
        if (!upload.resource) {
            D3D12_HEAP_PROPERTIES heap = {}; heap.Type = D3D12_HEAP_TYPE_UPLOAD;
            D3D12_RESOURCE_DESC buffer = {};
            buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; buffer.Width = pageBytes;
            buffer.Height = 1; buffer.DepthOrArraySize = 1; buffer.MipLevels = 1;
            buffer.SampleDesc.Count = 1; buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            HRESULT result = deviceOwner->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upload.resource));
            if (FAILED(result)) return result;
            void* pointer = nullptr;
            const D3D12_RANGE noRead = { 0, 0 };
            result = upload.resource->Map(0, &noRead, &pointer);
            if (FAILED(result)) { upload.resource.Reset(); return result; }
            upload.mapped = static_cast<uint8_t*>(pointer);
            frame.pageCount++; uploadAllocations++;
        }
        offset = (upload.used + alignment - 1) & ~(alignment - 1);
        if (offset <= pageBytes - bytes) {
            upload.used = offset + bytes; upload.lastUsedMs = frameTimeMs;
            return S_OK;
        }
        frame.pageIndex++;
    }
}

void TerminalRenderer::flushQuads() {
    if (!pendingQuads) return;
    const auto& upload = frames[frameIndex].pages[pendingPage];
    D3D12_VERTEX_BUFFER_VIEW view = { upload.resource->GetGPUVirtualAddress() + pendingOffset, pendingQuads * (UINT)sizeof(Quad), sizeof(Quad) };
    commands->IASetVertexBuffers(0, 1, &view);
    commands->DrawInstanced(4, pendingQuads, 0, 0);
    drawCalls++; pendingQuads = 0;
}

void TerminalRenderer::bindTexture(ID3D12DescriptorHeap* heap) {
    if (boundTexture == heap) return;
    flushQuads();
    ID3D12DescriptorHeap* heaps[] = { heap };
    commands->SetDescriptorHeaps(1, heaps);
    commands->SetGraphicsRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
    boundTexture = heap;
}

HRESULT TerminalRenderer::appendQuad(const Quad& quad) {
    if (quad.kind != 5) bindTexture(descriptors.Get());
    UINT page, offset;
    HRESULT result = allocateUpload(sizeof(quad), 4, page, offset);
    if (FAILED(result)) return result;
    if (pendingQuads && (page != pendingPage || offset != pendingOffset + pendingQuads * sizeof(Quad))) flushQuads();
    if (!pendingQuads) { pendingPage = page; pendingOffset = offset; }
    Quad transformed = quad;
    // Scale cached atlas quads, not fonts: animation must never populate a new atlas per frame.
    transformed.bounds[0] = quad.bounds[0] * previewScale + previewX * scale;
    transformed.bounds[1] = quad.bounds[1] * previewScale + previewY * scale;
    transformed.bounds[2] = quad.bounds[2] * previewScale + previewX * scale;
    transformed.bounds[3] = quad.bounds[3] * previewScale + previewY * scale;
    memcpy(frames[frameIndex].pages[page].mapped + offset, &transformed, sizeof(transformed));
    pendingQuads++;
    if (recording) recordQuad(quad);
    return S_OK;
}

void TerminalRenderer::recordQuad(const Quad& quad) {
    auto& entry = *recording;
    if (entry.count == entry.capacity) {
        if (entry.capacity == maxGeometryQuads) { recording = nullptr; return; }
        const UINT capacity = entry.capacity ? entry.capacity * 2 : 256;
        auto memory = static_cast<Quad*>(entry.quads
            ? HeapReAlloc(GetProcessHeap(), 0, entry.quads, (SIZE_T)capacity * sizeof(Quad))
            : HeapAlloc(GetProcessHeap(), 0, (SIZE_T)capacity * sizeof(Quad)));
        // Caching is optional: allocation pressure must not prevent ordinary drawing.
        if (!memory) { recording = nullptr; return; }
        entry.quads = memory; entry.capacity = capacity;
    }
    entry.quads[entry.count++] = quad;
}

HRESULT TerminalRenderer::replayGeometry(const Geometry& entry) {
    bindTexture(descriptors.Get());
    UINT copied = 0;
    while (copied < entry.count) {
        const UINT count = entry.count - copied < pageBytes / sizeof(Quad)
            ? entry.count - copied : pageBytes / (UINT)sizeof(Quad);
        UINT page, offset;
        HRESULT result = allocateUpload(count * sizeof(Quad), 4, page, offset);
        if (FAILED(result)) return result;
        if (pendingQuads && (page != pendingPage || offset != pendingOffset + pendingQuads * sizeof(Quad))) flushQuads();
        if (!pendingQuads) { pendingPage = page; pendingOffset = offset; }
        auto* output = reinterpret_cast<Quad*>(frames[frameIndex].pages[page].mapped + offset);
        memcpy(output, entry.quads + copied, count * sizeof(Quad));
        if (previewScale != 1 || previewX != 0 || previewY != 0) {
            for (UINT i = 0; i < count; ++i) {
                output[i].bounds[0] = output[i].bounds[0] * previewScale + previewX * scale;
                output[i].bounds[1] = output[i].bounds[1] * previewScale + previewY * scale;
                output[i].bounds[2] = output[i].bounds[2] * previewScale + previewX * scale;
                output[i].bounds[3] = output[i].bounds[3] * previewScale + previewY * scale;
            }
        }
        pendingQuads += count; copied += count;
    }
    return S_OK;
}

HRESULT TerminalRenderer::beginGeometry(uint64_t generation, const uint32_t (&key)[12], const float (&geometry)[4], bool& reused) {
    reused = false;
    if (!passOpen || recording) return E_UNEXPECTED;
    if (!geometryEnabled) return S_OK;
    Geometry* entry = nullptr;
    Geometry* oldest = &geometries[0];
    for (auto& candidate : geometries) {
        if (candidate.key[0] == key[0] && candidate.key[8] == key[8]) { entry = &candidate; break; }
        if (candidate.lastUsed < oldest->lastUsed) oldest = &candidate;
    }
    if (!entry) entry = oldest;
    entry->lastUsed = ++geometryClock;
    if (entry->valid && entry->generation == generation && entry->atlasGeneration == atlasResets &&
        memcmp(entry->key, key, sizeof(entry->key)) == 0 && memcmp(entry->geometry, geometry, sizeof(entry->geometry)) == 0) {
        HRESULT result = replayGeometry(*entry);
        if (SUCCEEDED(result)) { reused = true; geometryHits++; }
        return result;
    }
    entry->valid = false; entry->count = 0;
    entry->generation = generation; entry->atlasGeneration = atlasResets;
    memcpy(entry->key, key, sizeof(entry->key)); memcpy(entry->geometry, geometry, sizeof(entry->geometry));
    recording = entry;
    return S_OK;
}

void TerminalRenderer::endGeometry(bool complete) {
    if (!recording) return;
    // Mid-build eviction can reuse UVs already recorded. Never retain that mixed atlas generation.
    recording->valid = complete && recording->atlasGeneration == atlasResets;
    if (recording->valid) geometryBuilds++;
    recording = nullptr;
}

UINT64 TerminalRenderer::geometryCapacity() const {
    UINT64 bytes = 0;
    for (const auto& entry : geometries) bytes += (UINT64)entry.capacity * sizeof(Quad);
    return bytes;
}

HRESULT TerminalRenderer::uploadImage(CachedImage& image, const uint8_t* rgba, UINT width, UINT height) {
    D3D12_HEAP_PROPERTIES heap = {}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC texture = {};
    texture.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    texture.Width = width; texture.Height = height;
    texture.DepthOrArraySize = 1; texture.MipLevels = 1;
    texture.Format = DXGI_FORMAT_R8G8B8A8_UNORM; texture.SampleDesc.Count = 1;
    HRESULT result = deviceOwner->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &texture,
        D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&image.resource.texture));
    D3D12_DESCRIPTOR_HEAP_DESC description = {};
    description.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    description.NumDescriptors = 1; description.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (SUCCEEDED(result)) result = deviceOwner->CreateDescriptorHeap(&description, IID_PPV_ARGS(&image.resource.view));
    if (FAILED(result)) return result;
    D3D12_SHADER_RESOURCE_VIEW_DESC view = {};
    view.Format = texture.Format; view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    view.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    view.Texture2D.MipLevels = 1;
    deviceOwner->CreateShaderResourceView(image.resource.texture.Get(), &view, image.resource.view->GetCPUDescriptorHandleForHeapStart());
    auto& frame = frames[frameIndex];
    const UINT slot = frame.imageCount;
    result = frame.images[slot].retain(image.resource);
    if (FAILED(result)) return result;
    frame.imageCount++;
    // Image transfers must not exhaust the pages reserved for glyphs and terminal geometry.
    const UINT pitch = (width * 4 + 255) & ~255u;
    const UINT64 bytes = (UINT64)pitch * height;
    heap.Type = D3D12_HEAP_TYPE_UPLOAD;
    D3D12_RESOURCE_DESC buffer = {};
    buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; buffer.Width = bytes;
    buffer.Height = 1; buffer.DepthOrArraySize = 1; buffer.MipLevels = 1;
    buffer.SampleDesc.Count = 1; buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    result = deviceOwner->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer,
        D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&frame.imageUploads[slot]));
    if (FAILED(result)) return result;
    void* mapped = nullptr;
    const D3D12_RANGE noRead = { 0, 0 };
    result = frame.imageUploads[slot]->Map(0, &noRead, &mapped);
    if (FAILED(result)) return result;
    auto destination = static_cast<uint8_t*>(mapped);
    for (UINT row = 0; row < height; ++row) memcpy(destination + (size_t)row * pitch, rgba + (size_t)row * width * 4, width * 4);
    frame.imageUploads[slot]->Unmap(0, nullptr);
    frame.imageUploadBytes += bytes;
    flushQuads();
    D3D12_TEXTURE_COPY_LOCATION from = {}, to = {};
    from.pResource = frame.imageUploads[slot].Get(); from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    from.PlacedFootprint.Footprint = { texture.Format, width, height, 1, pitch };
    to.pResource = image.resource.texture.Get(); to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    commands->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
    auto barrier = terminalTransition(image.resource.texture.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    commands->ResourceBarrier(1, &barrier);
    return S_OK;
}

HRESULT TerminalRenderer::drawImage(uint64_t generation, const uint8_t* rgba, UINT width, UINT height, const float (&bounds)[4], const float (&uv)[4]) {
    if (!passOpen || recording || !generation || !rgba || !width || !height || width > 8192 || height > 8192 ||
        (UINT64)width * height * 4 > 32 * 1024 * 1024) return E_INVALIDARG;
    for (UINT i = 0; i < 4; ++i) if (!isfinite(bounds[i]) || !isfinite(uv[i])) return E_INVALIDARG;
    if (bounds[2] <= bounds[0] || bounds[3] <= bounds[1]) return S_OK;
    auto& frame = frames[frameIndex];
    CachedImage* cached = nullptr;
    for (auto& image : images) if (image.generation == generation) { cached = &image; break; }
    if (!cached) {
        // A full image working set must not turn another pane's output into a fatal frame error.
        if (frame.imageCount == maxImages) return S_OK;
        const UINT64 uploadBytes = (UINT64)((width * 4 + 255) & ~255u) * height;
        if (uploadBytes > 64 * 1024 * 1024 - frame.imageUploadBytes) return S_OK;
        const UINT64 bytes = (UINT64)width * height * 4;
        for (;;) {
            CachedImage* oldest = nullptr;
            const bool mustEvict = imageBytes + bytes > 64 * 1024 * 1024;
            for (auto& image : images) {
                if (mustEvict && !image.resource.texture) continue;
                if (!oldest || image.lastUsed < oldest->lastUsed) oldest = &image;
            }
            if (!oldest) return E_UNEXPECTED;
            if (oldest->resource.texture) { imageBytes -= oldest->bytes; oldest->reset(); }
            if (imageBytes + bytes <= 64 * 1024 * 1024) { cached = oldest; break; }
        }
        HRESULT result = uploadImage(*cached, rgba, width, height);
        if (FAILED(result)) { cached->reset(); return result; }
        cached->generation = generation; cached->bytes = bytes;
        imageBytes += bytes; imageUploads++;
    } else {
        bool retained = false;
        for (UINT i = 0; i < frame.imageCount; ++i) if (frame.images[i].texture.Get() == cached->resource.texture.Get()) { retained = true; break; }
        if (!retained) {
            if (frame.imageCount == maxImages) return S_OK;
            HRESULT result = frame.images[frame.imageCount].retain(cached->resource);
            if (FAILED(result)) return result;
            frame.imageCount++;
        }
        imageHits++;
    }
    cached->lastUsed = frameTimeMs;
    // Consecutive placements of the same texture share an instanced draw.
    bindTexture(cached->resource.view.Get());
    Quad quad = { { bounds[0] * scale, bounds[1] * scale, bounds[2] * scale, bounds[3] * scale },
        { uv[0], uv[1], uv[2], uv[3] }, 0xffffffff, 5 };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::drawSolid(float left, float top, float right, float bottom, uint32_t color, uint8_t alpha) {
    if (!passOpen || !isfinite(left) || !isfinite(top) || !isfinite(right) || !isfinite(bottom)) return E_INVALIDARG;
    if (right <= left || bottom <= top) return S_OK;
    Quad quad = { { left * scale, top * scale, right * scale, bottom * scale }, { -1, -1, -1, -1 },
        ((UINT)alpha << 24) | ((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255) };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::setClip(D3D12_RECT clip) {
    if (!passOpen || clip.left > clip.right || clip.top > clip.bottom) return E_INVALIDARG;
    flushQuads();
    commands->RSSetScissorRects(1, &clip);
    return S_OK;
}

HRESULT TerminalRenderer::drawRounded(float left, float top, float right, float bottom, float radius, float stroke, uint32_t color, uint8_t alpha) {
    if (!passOpen || !isfinite(left) || !isfinite(top) || !isfinite(right) || !isfinite(bottom) || !isfinite(radius) || !isfinite(stroke) || radius < 0 || stroke < 0) return E_INVALIDARG;
    if (right <= left || bottom <= top) return S_OK;
    const float pad = stroke * scale * 0.5f + 1;
    Quad quad = { { left * scale - pad, top * scale - pad, right * scale + pad, bottom * scale + pad },
        { radius * scale, stroke * scale, 0, 0 },
        ((UINT)alpha << 24) | ((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255), 1 };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::drawRoundedGradient(float left, float top, float right, float bottom, float radius, uint32_t topColor, uint32_t bottomColor, uint8_t alpha) {
    if (!passOpen || !isfinite(left) || !isfinite(top) || !isfinite(right) || !isfinite(bottom) || !isfinite(radius) || radius < 0) return E_INVALIDARG;
    if (right <= left || bottom <= top) return S_OK;
    // RGB24 is exactly representable as a float. Reuse the unused fill parameters
    // so materials share the existing instance layout, pipeline and draw batch.
    Quad quad = { { left * scale - 1, top * scale - 1, right * scale + 1, bottom * scale + 1 },
        { radius * scale, 0, (float)(bottomColor & 0xffffff), 0 },
        ((UINT)alpha << 24) | ((topColor & 255) << 16) | (topColor & 0xff00) | ((topColor >> 16) & 255), 6 };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::drawLine(float x1, float y1, float x2, float y2, float stroke, bool roundCaps, uint32_t color, uint8_t alpha) {
    if (!passOpen || !isfinite(x1) || !isfinite(y1) || !isfinite(x2) || !isfinite(y2) || !isfinite(stroke) || stroke <= 0) return E_INVALIDARG;
    const float pad = stroke * scale * 0.5f + 1;
    Quad quad = { { fminf(x1, x2) * scale - pad, fminf(y1, y2) * scale - pad, fmaxf(x1, x2) * scale + pad, fmaxf(y1, y2) * scale + pad },
        { x1 * scale, y1 * scale, x2 * scale, y2 * scale },
        ((UINT)alpha << 24) | ((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255), roundCaps ? 2u : 3u };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::drawEllipse(float cx, float cy, float rx, float ry, float stroke, uint32_t color, uint8_t alpha) {
    if (!passOpen || !isfinite(cx) || !isfinite(cy) || !isfinite(rx) || !isfinite(ry) || !isfinite(stroke) || rx <= 0 || ry <= 0 || stroke < 0) return E_INVALIDARG;
    const float pad = stroke * scale * 0.5f + 1;
    Quad quad = { { (cx - rx) * scale - pad, (cy - ry) * scale - pad, (cx + rx) * scale + pad, (cy + ry) * scale + pad },
        { stroke * scale, 0, 0, 0 },
        ((UINT)alpha << 24) | ((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255), 4 };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::uploadGlyph(Glyph& glyph) {
    // A copy may reuse atlas coordinates after eviction. Finish every earlier draw before the write.
    flushQuads();
    const UINT pitch = (glyph.width + 255) & ~255u;
    UINT page, offset;
    HRESULT result = allocateUpload(pitch * glyph.height, D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT, page, offset);
    if (FAILED(result)) return result;
    WICRect rect = { 0, 0, (INT)glyph.width, (INT)glyph.height };
    ComPtr<IWICBitmapLock> lock;
    result = rasterBitmap->Lock(&rect, WICBitmapLockRead, &lock);
    if (FAILED(result)) return result;
    UINT stride, size; BYTE* source = nullptr;
    result = lock->GetStride(&stride);
    if (SUCCEEDED(result)) result = lock->GetDataPointer(&size, &source);
    if (FAILED(result)) return result;
    if (!source || stride < glyph.width * 4 || size < (glyph.height - 1) * stride + glyph.width * 4) return E_UNEXPECTED;
    uint8_t* destination = frames[frameIndex].pages[page].mapped + offset;
    for (UINT y = 0; y < glyph.height; y++) {
        for (UINT x = 0; x < glyph.width; x++) destination[y * pitch + x] = source[y * stride + x * 4 + 3];
    }
    auto barrier = terminalTransition(atlas.Get(), D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST);
    commands->ResourceBarrier(1, &barrier);
    D3D12_TEXTURE_COPY_LOCATION from = {}, to = {};
    from.pResource = frames[frameIndex].pages[page].resource.Get(); from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    from.PlacedFootprint.Offset = offset;
    from.PlacedFootprint.Footprint = { DXGI_FORMAT_R8_UNORM, glyph.width, glyph.height, 1, pitch };
    to.pResource = atlas.Get(); to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    commands->CopyTextureRegion(&to, glyph.x, glyph.y, 0, &from, nullptr);
    barrier = terminalTransition(atlas.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    commands->ResourceBarrier(1, &barrier);
    return S_OK;
}

HRESULT TerminalRenderer::drawGlyph(float x, float y, float width, float height, const uint8_t* text, size_t length, uint32_t flags, uint32_t color, TerminalGlyphPainter painter, uint8_t alpha, bool cellAligned) {
    if (!passOpen || !text || !length || length > 65536 || !painter || !isfinite(x) || !isfinite(y) || !isfinite(width) || !isfinite(height) || width <= 0 || height <= 0) return E_INVALIDARG;
    // Cell graphics must share the background's rounded screen edges. Quantizing
    // their atlas-local phase independently can move an edge into the next cell.
    const float left = cellAligned ? roundf(x * scale) : floorf(x * scale);
    const float top = cellAligned ? roundf(y * scale) : floorf(y * scale);
    const UINT phaseX = cellAligned ? 0 : (UINT)fminf(3, floorf((x * scale - left) * 4));
    const UINT phaseY = cellAligned ? 0 : (UINT)fminf(3, floorf((y * scale - top) * 4));
    const float rasterWidth = cellAligned ? roundf((x + width) * scale) - left : width * scale;
    const float rasterHeight = cellAligned ? roundf((y + height) * scale) - top : height * scale;
    if (rasterWidth <= 0 || rasterHeight <= 0) return S_OK;
    const UINT w = (UINT)ceilf(rasterWidth) + 1, h = (UINT)ceilf(rasterHeight) + 1;
    if (w > 512 || h > 512) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    const UINT phase = phaseX | (phaseY << 2) | (cellAligned ? 16u : 0u), style = flags & 3;
    UINT64 hash = 14695981039346656037ull;
    for (size_t i = 0; i < length; i++) hash = (hash ^ text[i]) * 1099511628211ull;
    hash ^= ((UINT64)w << 32) | ((UINT64)h << 16) | (phase << 2) | style;
    UINT slot = (UINT)(hash % cacheSlots);
    while (glyphs[slot].text) {
        const auto& candidate = glyphs[slot];
        if (candidate.hash == hash && candidate.length == length && candidate.width == w && candidate.height == h && candidate.style == style && candidate.phase == phase && memcmp(candidate.text, text, length) == 0) break;
        slot = (slot + 1) % cacheSlots;
    }
    if (!glyphs[slot].text) {
        UINT nextX = shelfX, nextY = shelfY, nextHeight = shelfHeight;
        if (nextX + w + 2 > atlasSize) { nextX = 0; nextY += nextHeight; nextHeight = 0; }
        if (glyphCount == maxGlyphs || keyBytes + length > 1024 * 1024 || nextY + h + 2 > atlasSize) {
            flushQuads(); clearGlyphCache();
            nextX = 0; nextY = 0; nextHeight = 0; slot = (UINT)(hash % cacheSlots);
        }
        auto key = static_cast<uint8_t*>(HeapAlloc(GetProcessHeap(), 0, length));
        if (!key) return E_OUTOFMEMORY;
        memcpy(key, text, length);
        Glyph glyph;
        glyph.text = key; glyph.length = (UINT)length; glyph.hash = hash; glyph.width = w; glyph.height = h;
        glyph.x = nextX + 1; glyph.y = nextY + 1; glyph.style = style; glyph.phase = phase;
        rasterTarget->BeginDraw();
        rasterTarget->PushAxisAlignedClip(D2D1::RectF(0, 0, w / scale, h / scale), D2D1_ANTIALIAS_MODE_ALIASED);
        rasterTarget->Clear(D2D1::ColorF(0, 0.0f));
        HRESULT result = painter(rasterTarget.Get(), rasterBrush.Get(), text, length, flags,
            phaseX * 0.25f / scale, phaseY * 0.25f / scale,
            cellAligned ? rasterWidth / scale : width, cellAligned ? rasterHeight / scale : height);
        rasterTarget->PopAxisAlignedClip();
        const HRESULT ended = rasterTarget->EndDraw();
        if (SUCCEEDED(result)) result = ended;
        if (SUCCEEDED(result)) result = uploadGlyph(glyph);
        if (FAILED(result)) { HeapFree(GetProcessHeap(), 0, key); return result; }
        glyphs[slot] = glyph;
        shelfX = nextX + w + 2; shelfY = nextY; shelfHeight = nextHeight > h + 2 ? nextHeight : h + 2;
        glyphCount++; keyBytes += (UINT)length; glyphMisses++;
    } else glyphHits++;
    const auto& glyph = glyphs[slot];
    Quad quad = { { left, top, left + w, top + h },
        { (float)glyph.x / atlasSize, (float)glyph.y / atlasSize, (float)(glyph.x + w) / atlasSize, (float)(glyph.y + h) / atlasSize },
        ((UINT)alpha << 24) | ((color & 255) << 16) | (color & 0xff00) | ((color >> 16) & 255) };
    return appendQuad(quad);
}

HRESULT TerminalRenderer::endPass(ID3D12CommandQueue* queue) {
    if (!passOpen || !queue) return E_UNEXPECTED;
    flushQuads();
    auto barrier = terminalTransition(passBuffer, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
    commands->ResourceBarrier(1, &barrier);
    HRESULT result = commands->Close();
    if (SUCCEEDED(result)) { ID3D12CommandList* lists[] = { commands.Get() }; queue->ExecuteCommandLists(1, lists); }
    passOpen = false; passBuffer = nullptr; boundTexture = nullptr;
    return result;
}

void TerminalRenderer::clearGlyphCache() {
    for (auto& glyph : glyphs) {
        if (glyph.text) HeapFree(GetProcessHeap(), 0, glyph.text);
        glyph = {};
    }
    shelfX = shelfY = shelfHeight = glyphCount = keyBytes = 0;
    atlasResets++;
}

UINT64 TerminalRenderer::uploadCapacity() const {
    UINT64 total = 0;
    for (const auto& frame : frames) total += (UINT64)frame.pageCount * pageBytes;
    return total;
}

UINT64 TerminalRenderer::imageUploadCapacity() const {
    UINT64 total = 0;
    for (const auto& frame : frames) total += frame.imageUploadBytes;
    return total;
}

void TerminalRenderer::releaseFrameImages(Frame& frame) {
    for (UINT i = 0; i < frame.imageCount; ++i) {
        frame.images[i].reset();
        frame.imageUploads[i].Reset();
    }
    frame.imageCount = 0;
    frame.imageUploadBytes = 0;
}

void TerminalRenderer::trimCachedImages(ULONGLONG now) {
    // In-flight slots retain their own texture references until their fences complete.
    for (auto& image : images) {
        if (image.resource.texture && now - image.lastUsed > 2000) {
            imageBytes -= image.bytes;
            image.reset();
        }
    }
}

void TerminalRenderer::trimUnusedUploads(UINT index, ULONGLONG now, bool releaseWarmReserve) {
    if (index >= frameCount || passOpen) return;
    auto& frame = frames[index];
    // An idle/hidden window may never call beginFrame again to retire image transfers.
    releaseFrameImages(frame);
    trimCachedImages(now);
    const UINT previousCount = frame.pageCount;
    // Keep a warm reserve and a contiguous prefix so allocation can reuse the remaining pages.
    while (frame.pageCount > (releaseWarmReserve ? 0u : 2u)) {
        auto& upload = frame.pages[frame.pageCount - 1];
        if (now - upload.lastUsedMs < 2000) break;
        upload.resource->Unmap(0, nullptr);
        upload.resource.Reset(); upload.mapped = nullptr; upload.used = 0; upload.lastUsedMs = 0;
        frame.pageCount--;
    }
    if (frame.pageCount != previousCount) { frame.pageIndex = 0; uploadTrims++; }
}

void TerminalRenderer::shutdown() {
    recording = nullptr;
    for (auto& entry : geometries) {
        if (entry.quads) HeapFree(GetProcessHeap(), 0, entry.quads);
        entry = {};
    }
    clearGlyphCache();
    for (auto& image : images) image.reset();
    imageBytes = 0;
    for (auto& frame : frames) {
        for (auto& image : frame.images) image.reset();
        for (auto& upload : frame.imageUploads) upload.Reset();
        frame.imageCount = 0;
        frame.imageUploadBytes = 0;
        for (auto& upload : frame.pages) {
            if (upload.resource && upload.mapped) upload.resource->Unmap(0, nullptr);
            upload.resource.Reset(); upload.mapped = nullptr; upload.used = 0;
        }
        frame.allocator.Reset(); frame.pageCount = frame.pageIndex = 0;
    }
    commands.Reset(); atlas.Reset(); descriptors.Reset(); pipeline.Reset(); root.Reset();
    rasterBrush.Reset(); rasterTarget.Reset(); rasterBitmap.Reset(); imagingFactory.Reset(); deviceOwner.Reset();
    if (apartmentOwned) { CoUninitialize(); apartmentOwned = false; }
    passOpen = false;
}
