// Aurora: two colors flowing in soft waves, like northern lights.
// P0 = color 1, P1 = color 2, P2.x = speed, P3.x = waves, P4.x = strength
float4 Material(float2 uv, float4 surface)
{
    float aspect = SIZE.x / max(SIZE.y, 1);
    float2 p = float2(uv.x * aspect, uv.y);
    float t = TIME * P2.x * 0.3;
    float w = sin(p.x * P3.x * 0.6 + t + fbm(p * 1.5 + t * 0.4) * 3.0) * 0.5 + 0.5;
    float band = smoothstep(0.2, 0.8, w) * (0.6 + 0.4 * sin(p.x * 0.3 - t));
    float3 aurora = lerp(P0.rgb, P1.rgb, saturate(w + uv.y * 0.3));
    return float4(lerp(surface.rgb, aurora, band * P4.x), 1);
}
