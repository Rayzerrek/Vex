#include "windows_capture.h"

void drawCapturedWindow(HWND window, HDC hdc, WindowCaptureRender render) {
    RECT bounds;
    if (!hdc || !GetClientRect(window, &bounds)) return;
    const int width = bounds.right - bounds.left;
    const int height = bounds.bottom - bounds.top;
    if (width <= 0 || height <= 0) return;

    HDC memDc = CreateCompatibleDC(nullptr);
    if (!memDc) return;
    BITMAPINFO bmi = {};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = width;
    bmi.bmiHeader.biHeight = height; // bottom-up DIB for standard BMP
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(memDc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dib || !bits) {
        if (dib) DeleteObject(dib);
        DeleteDC(memDc);
        return;
    }
    HGDIOBJ oldObj = SelectObject(memDc, dib);

    // Read back the real D3D12-backed scene, not a separate software-rendered approximation.
    if (render(bits, (UINT)width * 4))
        BitBlt(hdc, 0, 0, width, height, memDc, 0, 0, SRCCOPY);
    SelectObject(memDc, oldObj);
    DeleteObject(dib);
    DeleteDC(memDc);
}

bool saveWindowCaptureBmp(HWND window, WindowCaptureRender render) {
    RECT bounds;
    if (!GetClientRect(window, &bounds)) return false;
    const int width = bounds.right - bounds.left;
    const int height = bounds.bottom - bounds.top;
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return false;
    const DWORD pixelBytes = (DWORD)(width * height * 4);
    void* bits = HeapAlloc(GetProcessHeap(), 0, pixelBytes);
    if (!bits) return false;
    if (!render(bits, (UINT)width * 4)) {
        HeapFree(GetProcessHeap(), 0, bits);
        return false;
    }

    wchar_t path[32768] = {};
    DWORD envLen = GetEnvironmentVariableW(L"VEX_NATIVE_CAPTURE_BMP", path, 32768);
    if (!envLen || envLen >= 32768) {
        DWORD modLen = GetModuleFileNameW(nullptr, path, 32768);
        wchar_t* slash = modLen ? wcsrchr(path, L'\\') : nullptr;
        if (slash) {
            *slash = 0;
            wchar_t* parentSlash = wcsrchr(path, L'\\');
            if (parentSlash && (parentSlash - path) + 32 < 32768) {
                wcscpy(parentSlash + 1, L"verification\\.capture.bmp");
            }
        }
    }

    bool saved = false;
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        BITMAPFILEHEADER bfh = {};
        bfh.bfType = 0x4D42; // 'BM'
        bfh.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
        bfh.bfSize = bfh.bfOffBits + pixelBytes;
        BITMAPINFOHEADER bih = {};
        bih.biSize = sizeof(BITMAPINFOHEADER);
        bih.biWidth = width;
        bih.biHeight = height;
        bih.biPlanes = 1;
        bih.biBitCount = 32;
        bih.biCompression = BI_RGB;
        bih.biSizeImage = pixelBytes;
        DWORD written1 = 0, written2 = 0, written3 = 0;
        if (WriteFile(file, &bfh, sizeof(bfh), &written1, nullptr) &&
            WriteFile(file, &bih, sizeof(bih), &written2, nullptr) &&
            WriteFile(file, bits, pixelBytes, &written3, nullptr)) {
            saved = (written1 == sizeof(bfh) && written2 == sizeof(bih) && written3 == pixelBytes);
        }
        CloseHandle(file);
    }
    HeapFree(GetProcessHeap(), 0, bits);
    return saved;
}

