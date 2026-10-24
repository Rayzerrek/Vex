#include "terminal_image.h"
#include "windows_platform.h"
#include "windows_com.h"
#include <wincodec.h>
#include <string.h>

namespace {
struct Apartment {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~Apartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};
}

extern "C" bool vex_terminal_decode_png(void*, const GhosttyAllocator* allocator, const uint8_t* data, size_t length, GhosttySysImage* out) {
    constexpr size_t maxBytes = 32 * 1024 * 1024;
    const uint8_t signature[] = { 137, 80, 78, 71, 13, 10, 26, 10 };
    if (!data || !out || length < sizeof(signature) || length > maxBytes || memcmp(data, signature, sizeof(signature))) return false;
    Apartment apartment;
    if (FAILED(apartment.result) && apartment.result != RPC_E_CHANGED_MODE) return false;
    RendererComPtr<IWICImagingFactory> factory;
    RendererComPtr<IWICStream> stream;
    RendererComPtr<IWICBitmapDecoder> decoder;
    RendererComPtr<IWICBitmapFrameDecode> frame;
    RendererComPtr<IWICFormatConverter> converter;
    HRESULT result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result)) result = factory->CreateStream(&stream);
    if (SUCCEEDED(result)) result = stream->InitializeFromMemory(const_cast<BYTE*>(data), (DWORD)length);
    if (SUCCEEDED(result)) result = factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
    GUID container = {};
    if (SUCCEEDED(result)) result = decoder->GetContainerFormat(&container);
    if (FAILED(result) || !IsEqualGUID(container, GUID_ContainerFormatPng)) return false;
    result = decoder->GetFrame(0, &frame);
    UINT width = 0, height = 0;
    if (SUCCEEDED(result)) result = frame->GetSize(&width, &height);
    if (FAILED(result) || !width || !height || width > 8192 || height > 8192 || (uint64_t)width * height * 4 > maxBytes) return false;
    result = factory->CreateFormatConverter(&converter);
    if (SUCCEEDED(result)) result = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
    if (FAILED(result)) return false;
    auto alloc = reinterpret_cast<decltype(&ghostty_alloc)>(vex_terminal_symbol("ghostty_alloc"));
    auto free = reinterpret_cast<decltype(&ghostty_free)>(vex_terminal_symbol("ghostty_free"));
    if (!alloc || !free) return false;
    const size_t bytes = (size_t)width * height * 4;
    auto pixels = alloc(allocator, bytes);
    if (!pixels) return false;
    result = converter->CopyPixels(nullptr, width * 4, (UINT)bytes, pixels);
    if (FAILED(result)) { free(allocator, pixels, bytes); return false; }
    *out = { width, height, pixels, bytes };
    return true;
}
