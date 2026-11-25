// Iridescent: slowly shifting rainbow colors, like a soap bubble; brighter where the mouse is.
// P0.x = speed, P1.x = color intensity, P2.x = brightness, P3.x = mix with the background
float4 Material(float2 uv, float4 surface)
{
    float aspect = SIZE.x / max(SIZE.y, 1);
    float2 p = float2(uv.x * aspect, uv.y);
    float t = TIME * P0.x;
    float hue = frac(p.x * 0.06 + p.y * 0.2 + t * 0.05 + 0.08 * sin(p.x * 0.8 + t * 0.6));
    float3 rainbow = hsv2rgb(float3(hue, P1.x, P2.x));
    float d = distance(p, float2(MOUSE.x * aspect, MOUSE.y));
    float glow = HOVER * 0.3 * saturate(1.0 - d * 0.7);
    return float4(lerp(surface.rgb, rainbow, P3.x) + glow, 1);
}
