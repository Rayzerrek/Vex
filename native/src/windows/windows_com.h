#pragma once
#include <windows.h>

// COM ownership at the native seam, without linking an STL/C++ runtime.
template<class T> class RendererComPtr final {
public:
    RendererComPtr() = default;
    RendererComPtr(const RendererComPtr&) = delete;
    RendererComPtr& operator=(const RendererComPtr&) = delete;
    ~RendererComPtr() { Reset(); }
    T* Get() const { return pointer; }
    T* operator->() const { return pointer; }
    explicit operator bool() const { return pointer != nullptr; }
    T** operator&() { return &pointer; }
    void Reset() { if (pointer) { pointer->Release(); pointer = nullptr; } }
    template<class U> HRESULT As(U** result) const { return pointer->QueryInterface(IID_PPV_ARGS(result)); }
private:
    T* pointer = nullptr;
};
