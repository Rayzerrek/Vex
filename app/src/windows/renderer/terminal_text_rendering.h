// Gamma coefficients adapted from Microsoft Terminal's atlas/dwrite_helpers.cpp.
// Copyright (c) Microsoft Corporation. Licensed under MIT; see THIRD-PARTY-NOTICES.md.
#pragma once
#include "windows_com.h"
#include <dwrite_1.h>
#include <math.h>

struct TerminalTextRendering {
    RendererComPtr<IDWriteRenderingParams1> linear;
    float gamma[4] = {};
    float contrast = 0;

    HRESULT initialize() {
        RendererComPtr<IDWriteFactory> factory;
        RendererComPtr<IDWriteFactory1> factory1;
        RendererComPtr<IDWriteRenderingParams> defaults;
        RendererComPtr<IDWriteRenderingParams1> defaults1;
        HRESULT result = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&factory));
        if (SUCCEEDED(result)) result = factory.As(&factory1);
        if (SUCCEEDED(result)) result = factory->CreateRenderingParams(&defaults);
        if (SUCCEEDED(result)) result = defaults.As(&defaults1);
        if (FAILED(result)) return result;
        contrast = defaults1->GetGrayscaleEnhancedContrast();
        // Color-dependent correction belongs at compositing time, not in a white
        // atlas mask. Otherwise dark/colored text inherits white text's correction.
        result = factory1->CreateCustomRenderingParams(1, 0, 0, defaults->GetClearTypeLevel(),
            defaults->GetPixelGeometry(), defaults->GetRenderingMode(), &linear);
        if (FAILED(result)) return result;
        static constexpr float coefficients[13][4] = {
            { 0, 0, 0, 0 },
            { 0.0166f, -0.0807f, 0.2227f, -0.0751f },
            { 0.0350f, -0.1760f, 0.4325f, -0.1370f },
            { 0.0543f, -0.2821f, 0.6302f, -0.1876f },
            { 0.0739f, -0.3963f, 0.8167f, -0.2287f },
            { 0.0933f, -0.5161f, 0.9926f, -0.2616f },
            { 0.1121f, -0.6395f, 1.1588f, -0.2877f },
            { 0.1300f, -0.7649f, 1.3159f, -0.3080f },
            { 0.1469f, -0.8911f, 1.4644f, -0.3234f },
            { 0.1627f, -1.0170f, 1.6051f, -0.3347f },
            { 0.1773f, -1.1420f, 1.7385f, -0.3426f },
            { 0.1908f, -1.2652f, 1.8650f, -0.3476f },
            { 0.2031f, -1.3864f, 1.9851f, -0.3501f },
        };
        const int index = (int)fminf(22, fmaxf(10, floorf(defaults->GetGamma() * 10 + 0.5f))) - 10;
        for (unsigned i = 0; i < 4; ++i)
            gamma[i] = coefficients[index][i] * (i % 2 ? 256.0f / 255 : 65536.0f / (255 * 255));
        return S_OK;
    }
};
