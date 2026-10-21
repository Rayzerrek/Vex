cbuffer Viewport : register(b0) { float2 viewportSize; };
Texture2D<float> glyphAtlas : register(t0);
SamplerState atlasSampler : register(s0);

struct Instance {
    float4 bounds : POSITION;
    float4 uv : TEXCOORD;
    float4 color : COLOR;
    uint kind : TYPE;
};
struct Fragment {
    float4 position : SV_Position;
    float2 uv : TEXCOORD;
    float4 color : COLOR;
    nointerpolation float4 bounds : TEXCOORD1;
    nointerpolation float4 parameters : TEXCOORD2;
    nointerpolation uint kind : TYPE;
};

Fragment terminalVertex(Instance input, uint vertex : SV_VertexID) {
    const float2 corners[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
    const float2 corner = corners[vertex];
    const float2 pixel = lerp(input.bounds.xy, input.bounds.zw, corner);
    Fragment output;
    output.position = float4(pixel.x * 2 / viewportSize.x - 1, 1 - pixel.y * 2 / viewportSize.y, 0, 1);
    output.uv = lerp(input.uv.xy, input.uv.zw, corner);
    output.color = input.color;
    output.bounds = input.bounds;
    output.parameters = input.uv;
    output.kind = input.kind;
    return output;
}

float boxDistance(float2 local, float2 halfSize, float radius) {
    const float2 q = abs(local) - halfSize + radius;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float4 terminalPixel(Fragment input) : SV_Target {
    float alpha = 1;
    if (input.kind == 0) {
        // Negative UV denotes a solid rectangle, without an atlas texture access.
        alpha = input.uv.x < 0 ? 1 : glyphAtlas.Sample(atlasSampler, input.uv);
    } else {
        const float2 center = (input.bounds.xy + input.bounds.zw) * 0.5;
        const float2 extent = (input.bounds.zw - input.bounds.xy) * 0.5;
        float distance;
        if (input.kind == 1) {
            const float stroke = input.parameters.y;
            const float2 halfSize = extent - 1 - stroke * 0.5;
            const float radius = min(input.parameters.x, min(halfSize.x, halfSize.y));
            distance = boxDistance(input.position.xy - center, halfSize, radius);
            if (stroke > 0) distance = abs(distance) - stroke * 0.5;
        } else if (input.kind == 4) {
            const float stroke = input.parameters.x;
            const float2 radii = max(extent - 1 - stroke * 0.5, 0.001);
            const float2 p = abs(input.position.xy - center);
            const float k0 = length(p / radii);
            const float k1 = length(p / (radii * radii));
            distance = k1 > 0 ? k0 * (k0 - 1) / k1 : -min(radii.x, radii.y);
            if (stroke > 0) distance = abs(distance) - stroke * 0.5;
        } else {
            const float2 a = input.parameters.xy, b = input.parameters.zw;
            const float2 direction = b - a;
            const float segmentLength = length(direction);
            const float halfStroke = input.bounds.z - max(a.x, b.x) - 1;
            const float2 axis = segmentLength > 0 ? direction / segmentLength : float2(1, 0);
            const float2 relative = input.position.xy - (a + b) * 0.5;
            const float2 local = float2(dot(relative, axis), dot(relative, float2(-axis.y, axis.x)));
            distance = input.kind == 2
                ? length(float2(max(abs(local.x) - segmentLength * 0.5, 0), local.y)) - halfStroke
                : boxDistance(local, float2(segmentLength * 0.5, halfStroke), 0);
        }
        alpha = saturate(0.5 - distance);
    }
    return float4(input.color.rgb, input.color.a * alpha);
}
