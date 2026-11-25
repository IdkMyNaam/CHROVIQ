// Tinted glass: the background with a color tint, a soft diagonal sheen, light from the top edge and a fine grain.
// Make the bar's background see-through (for example #A01C1C1C) and turn on "Blur behind the bar" for real frosted glass.
// P0 = tint color, P1.x = tint strength, P2.x = sheen, P3.x = grain
float4 Material(float2 uv, float4 surface)
{
    float3 col = lerp(surface.rgb, P0.rgb, P1.x);
    float sheen = saturate(1.0 - abs(uv.x * 0.7 + uv.y * 0.6 - 0.55) * 3.0);
    col += sheen * sheen * P2.x * 0.18;
    col += (1.0 - uv.y) * 0.06;
    col += (hash21(floor(uv * SIZE)) - 0.5) * P3.x * 0.06;
    return float4(col, 1);
}
