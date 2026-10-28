#pragma once
#include "windows_com.h"
#include <d3d12.h>
#include <d2d1.h>
#include <wincodec.h>
#include <stdint.h>

// Rasterizes one borrowed UTF-8 grapheme into a white-on-transparent software target.
using TerminalGlyphPainter = HRESULT (*)(ID2D1RenderTarget*, ID2D1SolidColorBrush*, const uint8_t*, size_t, uint32_t, float, float, float, float);
using TerminalGlyphMeasurer = HRESULT (*)(const uint8_t*, size_t, uint32_t, float, float, D2D1_RECT_F&);

// One bounded glyph atlas shared by every pane; uploads belong to fence-protected frame slots.
class TerminalRenderer final {
public:
    // The optional source shares only immutable GPU pipeline state; atlases and frame uploads stay independent.
    HRESULT initialize(ID3D12Device* device, ID2D1Factory* factory, const TerminalRenderer* sharedPipeline = nullptr);
    void shutdown();
    HRESULT beginFrame(UINT index);
    HRESULT beginPass(ID3D12Resource* buffer, D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_RECT clip, UINT width, UINT height, float dpi);
    HRESULT endPass(ID3D12CommandQueue* queue);
    HRESULT drawImage(uint64_t generation, const uint8_t* rgba, UINT width, UINT height, const float (&bounds)[4], const float (&uv)[4]);
    UINT64 imageHits = 0, imageUploads = 0;
    HRESULT drawSolid(float left, float top, float right, float bottom, uint32_t color, uint8_t alpha = 255);
    HRESULT drawGlyph(float x, float y, float width, float height, const uint8_t* text, size_t length, uint32_t flags, uint32_t color, TerminalGlyphPainter painter, uint8_t alpha = 255, bool cellAligned = false, TerminalGlyphMeasurer measurer = nullptr);
    HRESULT drawRounded(float left, float top, float right, float bottom, float radius, float stroke, uint32_t color, uint8_t alpha);
    HRESULT drawRoundedGradient(float left, float top, float right, float bottom, float radius, uint32_t topColor, uint32_t bottomColor, uint8_t alpha);
    HRESULT drawLine(float x1, float y1, float x2, float y2, float stroke, bool roundCaps, uint32_t color, uint8_t alpha);
    HRESULT drawEllipse(float cx, float cy, float rx, float ry, float stroke, uint32_t color, uint8_t alpha);
    HRESULT setClip(D3D12_RECT clip);
    void clearGlyphCache();
    void setPreviewTransform(float factor = 1, float x = 0, float y = 0) { previewScale = factor; previewX = x; previewY = y; }
    // A hit replays immutable CPU instances into this slot's fence-protected uploads.
    HRESULT beginGeometry(uint64_t generation, const uint32_t (&key)[14], const float (&geometry)[4], bool& reused);
    void endGeometry(bool complete);
    UINT64 geometryHits = 0, geometryBuilds = 0;
    UINT64 geometryCapacity() const;
    bool geometryEnabled = true;
    // Caller must verify this frame slot's fence completed; never frees recently used pages.
    void trimUnusedUploads(UINT index, ULONGLONG now, bool releaseWarmReserve = false);
    UINT64 imageUploadCapacity() const;
    UINT64 cachedImageCapacity() const { return imageBytes; }
    UINT64 glyphHits = 0, glyphMisses = 0, atlasResets = 0, drawCalls = 0, uploadAllocations = 0;
    UINT64 uploadCapacity() const;
    UINT64 uploadTrims = 0;
    static constexpr UINT atlasSize = 1024;
private:
    template<class T> using ComPtr = RendererComPtr<T>;
    static constexpr UINT frameCount = 2, pageBytes = 256 * 1024, maxPages = 256, maxGlyphs = 2048, cacheSlots = 4096;
    struct UploadPage { ComPtr<ID3D12Resource> resource; uint8_t* mapped = nullptr; UINT used = 0; ULONGLONG lastUsedMs = 0; };
    static constexpr UINT maxImages = 128;
    struct ImageResource {
        ComPtr<ID3D12Resource> texture;
        ComPtr<ID3D12DescriptorHeap> view;
        void reset() { texture.Reset(); view.Reset(); }
        HRESULT retain(const ImageResource& source) {
            reset();
            HRESULT result = source.texture.As(&texture);
            if (SUCCEEDED(result)) result = source.view.As(&view);
            if (FAILED(result)) reset();
            return result;
        }
    };
    struct CachedImage {
        ImageResource resource;
        UINT64 generation = 0, bytes = 0;
        ULONGLONG lastUsed = 0;
        void reset() { resource.reset(); generation = bytes = lastUsed = 0; }
    };
    CachedImage images[maxImages];
    UINT64 imageBytes = 0;
    struct Frame {
        ComPtr<ID3D12CommandAllocator> allocator;
        ImageResource images[maxImages];
        ComPtr<ID3D12Resource> imageUploads[maxImages];
        UINT imageCount = 0;
        UINT64 imageUploadBytes = 0;
        UploadPage pages[maxPages];
        UINT pageCount = 0, pageIndex = 0;
    };
    struct Quad { float bounds[4], uv[4]; uint32_t color; uint32_t kind = 0; };
    static constexpr UINT geometrySlots = 8, maxGeometryQuads = 65536;
    struct Geometry {
        Quad* quads = nullptr;
        UINT count = 0, capacity = 0;
        uint32_t key[14] = {};
        float geometry[4] = {};
        UINT64 generation = 0, atlasGeneration = 0, lastUsed = 0;
        bool valid = false;
    };
    Geometry geometries[geometrySlots];
    Geometry* recording = nullptr;
    UINT64 geometryClock = 0;
    struct Glyph {
        uint8_t* text = nullptr;
        UINT length = 0, x = 0, y = 0, width = 0, height = 0;
        float layoutWidth = 0, layoutHeight = 0, offsetX = 0, offsetY = 0;
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
    float previewScale = 1, previewX = 0, previewY = 0;
    bool apartmentOwned = false, passOpen = false;
    ID3D12Resource* passBuffer = nullptr;
    ID3D12DescriptorHeap* boundTexture = nullptr;
    void bindTexture(ID3D12DescriptorHeap* heap);
    HRESULT createPipeline(const TerminalRenderer* sharedPipeline);
    HRESULT createPipelineState();
    HRESULT allocateUpload(UINT bytes, UINT alignment, UINT& page, UINT& offset);
    HRESULT appendQuad(const Quad& quad);
    void flushQuads();
    void recordQuad(const Quad& quad);
    HRESULT replayGeometry(const Geometry& geometry);
    HRESULT uploadGlyph(Glyph& glyph);
    HRESULT uploadImage(CachedImage& image, const uint8_t* rgba, UINT width, UINT height);
    void releaseFrameImages(Frame& frame);
    void trimCachedImages(ULONGLONG now);
};
