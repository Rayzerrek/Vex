#pragma once
#include "windows_com.h"
#include <d3d12.h>
#include <d2d1.h>
#include <wincodec.h>
#include <stdint.h>

// Rasterizes one borrowed UTF-8 grapheme into a white-on-transparent software target.
using TerminalGlyphPainter = HRESULT (*)(ID2D1RenderTarget*, ID2D1SolidColorBrush*, const uint8_t*, size_t, uint32_t, float, float, float, float);

// One bounded glyph atlas shared by every pane; uploads belong to fence-protected frame slots.
class TerminalRenderer final {
public:
    // The optional source shares only immutable GPU pipeline state; atlases and frame uploads stay independent.
    HRESULT initialize(ID3D12Device* device, ID2D1Factory* factory, const TerminalRenderer* sharedPipeline = nullptr);
    void shutdown();
    HRESULT beginFrame(UINT index);
    HRESULT beginPass(ID3D12Resource* buffer, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_RECT clip, UINT width, UINT height, float dpi);
    HRESULT endPass(ID3D12CommandQueue* queue);
    HRESULT drawSolid(float left, float top, float right, float bottom, uint32_t color, uint8_t alpha = 255);
    HRESULT drawGlyph(float x, float y, float width, float height, const uint8_t* text, size_t length, uint32_t flags, uint32_t color, TerminalGlyphPainter painter, uint8_t alpha = 255);
    HRESULT drawRounded(float left, float top, float right, float bottom, float radius, float stroke, uint32_t color, uint8_t alpha);
    HRESULT drawLine(float x1, float y1, float x2, float y2, float stroke, bool roundCaps, uint32_t color, uint8_t alpha);
    HRESULT drawEllipse(float cx, float cy, float rx, float ry, float stroke, uint32_t color, uint8_t alpha);
    HRESULT setClip(D3D12_RECT clip);
    void clearGlyphCache();
    // Caller must verify this frame slot's fence completed; never frees recently used pages.
    void trimUnusedUploads(UINT index, ULONGLONG now);
    UINT64 glyphHits = 0, glyphMisses = 0, atlasResets = 0, drawCalls = 0, uploadAllocations = 0;
    UINT64 uploadCapacity() const;
    UINT64 uploadTrims = 0;
    static constexpr UINT atlasSize = 1024;
private:
    template<class T> using ComPtr = RendererComPtr<T>;
    static constexpr UINT frameCount = 2, pageBytes = 256 * 1024, maxPages = 256, maxGlyphs = 2048, cacheSlots = 4096;
    struct UploadPage { ComPtr<ID3D12Resource> resource; uint8_t* mapped = nullptr; UINT used = 0; ULONGLONG lastUsedMs = 0; };
    struct Frame {
        ComPtr<ID3D12CommandAllocator> allocator;
        UploadPage pages[maxPages];
        UINT pageCount = 0, pageIndex = 0;
    };
    struct Quad { float bounds[4], uv[4]; uint32_t color; uint32_t kind = 0; };
    struct Glyph {
        uint8_t* text = nullptr;
        UINT length = 0, x = 0, y = 0, width = 0, height = 0;
        uint32_t style = 0, phase = 0;
        UINT64 hash = 0;
    };
    ComPtr<ID3D12Device> deviceOwner;
    ComPtr<ID3D12RootSignature> root;
    ComPtr<ID3D12PipelineState> pipeline;
    ComPtr<ID3D12DescriptorHeap> descriptors;
    ComPtr<ID3D12Resource> atlas;
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<IWICImagingFactory> imagingFactory;
    ComPtr<IWICBitmap> rasterBitmap;
    ComPtr<ID2D1RenderTarget> rasterTarget;
    ComPtr<ID2D1SolidColorBrush> rasterBrush;
    Frame frames[frameCount];
    Glyph glyphs[cacheSlots];
    UINT frameIndex = 0, shelfX = 0, shelfY = 0, shelfHeight = 0, glyphCount = 0, keyBytes = 0;
    UINT pendingPage = 0, pendingOffset = 0, pendingQuads = 0;
    ULONGLONG frameTimeMs = 0;
    float scale = 1;
    bool apartmentOwned = false, passOpen = false;
    ID3D12Resource* passBuffer = nullptr;
    HRESULT createPipeline(const TerminalRenderer* sharedPipeline);
    HRESULT createPipelineState();
    HRESULT allocateUpload(UINT bytes, UINT alignment, UINT& page, UINT& offset);
    HRESULT appendQuad(const Quad& quad);
    void flushQuads();
    HRESULT uploadGlyph(Glyph& glyph);
};
