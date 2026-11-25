// Scribbles: loose pencil-like strokes in patches.
// P0 = ink color, P1.x = amount, P2.x = scale, P3.x = strength
float4 Material(float2 uv, float4 surface)
{
    float2 px = uv * SIZE / max(P2.x, 0.2);
    float n = fbm(px * 0.15);
    float strokes = abs(sin((px.x + px.y * 0.6) * 0.9 + n * 12.0));
    float patches = smoothstep(1.0 - P1.x, 1.0, fbm(px * 0.05 + 3.1) + 0.35);
    float ink = (1.0 - smoothstep(0.0, 0.25, strokes)) * patches;
    return float4(lerp(surface.rgb, P0.rgb, ink * P3.x), 1);
}
