// Painted lines: hand-drawn looking hatching over the background.
// P0 = ink color, P1.x = line distance (pixels), P2.x = angle (degrees), P3.x = strength
float4 Material(float2 uv, float4 surface)
{
    float2 px = uv * SIZE;
    float a = radians(P2.x);
    float2 across = float2(-sin(a), cos(a));
    float coord = dot(px, across) + noise(px * 0.05) * 3.0;
    float dist = abs(frac(coord / max(P1.x, 2.0)) - 0.5);
    float stroke = 1.0 - smoothstep(0.05, 0.14, dist);
    float fade = 0.55 + 0.45 * noise(px * 0.02 + 7.0);
    return float4(lerp(surface.rgb, P0.rgb, stroke * fade * P3.x), 1);
}
