cbuffer Viewport : register(b0) { float2 viewportSize; };
Texture2D<float> glyphAtlas : register(t0);
SamplerState atlasSampler : register(s0);

struct Instance {
    float4 bounds : POSITION;
    float4 uv : TEXCOORD;
    float4 color : COLOR;
};
struct Fragment {
    float4 position : SV_Position;
    float2 uv : TEXCOORD;
    float4 color : COLOR;
};

Fragment terminalVertex(Instance input, uint vertex : SV_VertexID) {
    const float2 corners[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
    const float2 corner = corners[vertex];
    const float2 pixel = lerp(input.bounds.xy, input.bounds.zw, corner);
    Fragment output;
    output.position = float4(pixel.x * 2 / viewportSize.x - 1, 1 - pixel.y * 2 / viewportSize.y, 0, 1);
    output.uv = lerp(input.uv.xy, input.uv.zw, corner);
    output.color = input.color;
    return output;
}

float4 terminalPixel(Fragment input) : SV_Target {
    // Negative UV denotes a solid rectangle, without an atlas texture access.
    const float alpha = input.uv.x < 0 ? 1 : glyphAtlas.Sample(atlasSampler, input.uv);
    return float4(input.color.rgb, input.color.a * alpha);
}
