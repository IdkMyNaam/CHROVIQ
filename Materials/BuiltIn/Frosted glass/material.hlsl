// Frosted glass (Direct3D, Shader Model 5): a real Gaussian blur of what's behind the surface, in two passes at
// half size (across, then down), then tint, saturation, a fine grain and light on the edges.
// P0.x = blur (pixels), P1 = tint, P2.x = tint strength, P3.x = saturation, P4.x = grain, P5.x = edge light

static const int TAPS = 21;

// One direction of the blur. The offsets are in surface pixels, so it looks the same at any pass size.
float3 BlurLine(float2 uv, float2 direction, bool behind)
{
    float radius = max(P0.x, 0.5);
    float sigma = radius / 2.5;
    float3 sum = 0;
    float total = 0;
    [unroll] for (int i = 0; i < TAPS; i++)
    {
        float offset = (i - (TAPS - 1) * 0.5) / ((TAPS - 1) * 0.5) * radius;
        float weight = exp(-0.5 * offset * offset / (sigma * sigma));
        float2 p = uv + direction * offset / max(SIZE, 1);
        sum += (behind ? BEHIND(p).rgb : PREVIOUS(p).rgb) * weight;
        total += weight;
    }
    return sum / total;
}

float4 BlurAcross(float2 uv) { return float4(BlurLine(uv, float2(1, 0), true), 1); }
float4 BlurDown(float2 uv) { return float4(BlurLine(uv, float2(0, 1), false), 1); }

float4 Finish(float2 uv)
{
    float3 col = PREVIOUS(uv).rgb;
    float grey = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(grey.xxx, col, P3.x);
    col = lerp(col, P1.rgb, P2.x);
    // A fine, still grain (like acrylic's noise), so large flat areas don't band.
    col += (hash21(floor(uv * SIZE)) - 0.5) * P4.x * 0.05;
    // Light: a thin line on the edges, a little more at the top.
    float2 px = uv * SIZE;
    float edge = min(min(px.x, SIZE.x - px.x), min(px.y, SIZE.y - px.y));
    col += saturate(1.0 - edge / 1.5) * P5.x * 0.25 + (1.0 - uv.y) * P5.x * 0.04;
    return float4(col, 1);
}
