// Mouse glow: a soft light that follows the mouse over the bar.
// P0 = glow color, P1.x = size, P2.x = strength
float4 Material(float2 uv, float4 surface)
{
    float aspect = SIZE.x / max(SIZE.y, 1);
    float d = distance(float2(uv.x * aspect, uv.y), float2(MOUSE.x * aspect, MOUSE.y));
    float glow = HOVER * saturate(1.0 - d / max(P1.x, 0.1));
    return float4(lerp(surface.rgb, P0.rgb, glow * glow * P2.x), 1);
}
