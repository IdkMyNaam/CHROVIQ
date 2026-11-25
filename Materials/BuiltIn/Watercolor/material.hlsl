// Watercolor: two colors in soft paint blotches with darker edges and paper grain.
// P0 = color 1, P1 = color 2, P2.x = blotch size, P3.x = strength, P4.x = paper grain
float4 Material(float2 uv, float4 surface)
{
    float2 px = uv * SIZE / max(P2.x, 0.2);
    float n = fbm(px * 0.02);
    float m = fbm(px * 0.035 + n * 2.0);
    float3 paint = lerp(P0.rgb, P1.rgb, smoothstep(0.35, 0.65, m));
    float edge = (1.0 - smoothstep(0.0, 0.025, abs(m - 0.5))) * 0.25;
    float grain = (noise(uv * SIZE * 0.8) - 0.5) * P4.x * 0.15;
    return float4(lerp(surface.rgb, paint * (1.0 - edge), P3.x) + grain, 1);
}
