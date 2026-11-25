// Clear glass: the wallpaper behind the surface, bent like a thick glass pane (stronger toward the edges),
// softly blurred, with a hint of color fringe, a tint, and light on the edges.
// Uses WALLPAPER(uv) ("UsesWallpaper": true in material.json). Give the bar a solid background (alpha FF):
// the glass replaces it.
// P0.x = bend, P1.x = blur, P2 = tint color, P3.x = tint strength, P4.x = edge light
float4 Material(float2 uv, float4 surface)
{
    float2 c = uv - 0.5;
    float aspect = SIZE.x / max(SIZE.y, 1);
    // Thicker toward the edges: pushes the picture inward there, like a lens.
    float2 edgeDistance = float2(abs(c.x) * aspect, abs(c.y)) / float2(0.5 * aspect, 0.5);
    float bendAmount = pow(saturate(max(edgeDistance.x * 0.15 + edgeDistance.y, edgeDistance.y)), 3) * P0.x;
    float2 p = 0.5 + c * (1.0 - bendAmount * 0.35);

    // A soft blur: a few taps around the point.
    float2 step = P1.x * 3.0 / max(SIZE, 1);
    float3 col = WALLPAPER(p).rgb * 0.36
               + (WALLPAPER(p + float2(step.x, 0)).rgb + WALLPAPER(p - float2(step.x, 0)).rgb
               +  WALLPAPER(p + float2(0, step.y)).rgb + WALLPAPER(p - float2(0, step.y)).rgb) * 0.16;

    // A touch of color fringe where the glass bends most.
    float2 fringe = c * bendAmount * 0.03;
    col.r = lerp(col.r, WALLPAPER(p + fringe).r, 0.6);
    col.b = lerp(col.b, WALLPAPER(p - fringe).b, 0.6);

    col = lerp(col, P2.rgb, P3.x);
    float rim = pow(saturate(max(edgeDistance.x / aspect * 0.9, edgeDistance.y)), 10);
    col += rim * P4.x * 0.35 + (1.0 - uv.y) * P4.x * 0.06;
    return float4(col, 1);
}
