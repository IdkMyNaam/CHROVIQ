// Refraction glass (Direct3D, Shader Model 5): a glass slab over what's behind. Flat in the middle, rounding off
// toward the edges like a lens, so the picture bends there; each color bends a little differently (fringes).
// Pass 1 softens what's behind at half size; pass 2 bends it and adds the light.
// P0.x = thickness, P1.x = softness, P2.x = ripples, P3.x = color fringe, P4.x = highlight, P5 = tint, P6.x = tint strength

float4 Soften(float2 uv)
{
    float2 step = max(P1.x, 0.0) * 1.5 / max(SIZE, 1);
    float3 sum = 0;
    float total = 0;
    [unroll] for (int y = -2; y <= 2; y++)
    [unroll] for (int x = -2; x <= 2; x++)
    {
        float weight = exp(-0.35 * (x * x + y * y));
        sum += BEHIND(uv + float2(x, y) * step).rgb * weight;
        total += weight;
    }
    return float4(sum / total, 1);
}

// The glass's height at a point: a quarter circle over the bevel near each edge, flat inside, plus ripples.
float Height(float2 uv)
{
    float2 px = uv * SIZE;
    float edge = min(min(px.x, SIZE.x - px.x), min(px.y, SIZE.y - px.y));
    float bevel = max(min(SIZE.x, SIZE.y) * 0.45, 1);
    float t = saturate(edge / bevel);
    float h = sqrt(t * (2.0 - t));
    h += (fbm(px / 70.0) - 0.5) * P2.x * 0.25;
    return h;
}

float4 Refract(float2 uv)
{
    // The slope of the glass (per pixel): where it slopes, the picture behind is pushed sideways.
    float2 e = 1.0 / max(SIZE, 1);
    float2 slope = float2(Height(uv + float2(e.x, 0)) - Height(uv - float2(e.x, 0)),
                          Height(uv + float2(0, e.y)) - Height(uv - float2(0, e.y))) * 0.5;
    float2 shift = -slope * P0.x * 35.0 * e * min(SIZE.x, SIZE.y) * 0.25;

    // Red, green and blue bend a little differently.
    float fringe = P3.x * 0.35;
    float3 col;
    col.r = PREVIOUS(uv + shift * (1.0 + fringe)).r;
    col.g = PREVIOUS(uv + shift).g;
    col.b = PREVIOUS(uv + shift * (1.0 - fringe)).b;

    col = lerp(col, P5.rgb, P6.x);

    // Light: a highlight where the glass faces the light (top left), and a thin rim.
    float3 normal = normalize(float3(-slope * 18.0, 1));
    float shine = pow(saturate(dot(normal, normalize(float3(-0.35, -0.6, 0.7)))), 24);
    float2 px = uv * SIZE;
    float edge = min(min(px.x, SIZE.x - px.x), min(px.y, SIZE.y - px.y));
    col += shine * P4.x * 0.45 + saturate(1.0 - edge / 1.5) * P4.x * 0.2;
    return float4(col, 1);
}
