# Making a CHROVIQ material

A material draws a surface (a taskbar, the start menu, a menu, a widget, ...) with a GPU pixel shader written in
HLSL. Each material is a folder with `material.json` and `material.hlsl`:

- The ones that come with CHROVIQ: `Materials\` next to the app (`BuiltIn\` in this repository).
- Your own: `%AppData%\WindowsCustomizer\Materials\<name>\`. Copy a built-in one, give it a new `Name` in
  `material.json`, and edit `material.hlsl`. Reopen the material settings to see the changes.

If a shader doesn't compile, the surface shows its plain background and the settings show the compiler's message,
with line numbers of `material.hlsl`.

There are two kinds: **WPF materials** (the default: one pass, Shader Model 3) and **Direct3D materials**
(several passes, Shader Model 5, for heavier effects such as real blurs and refraction).

## material.json

```json
{
  "Name": "My material",
  "Description": "What it looks like.",
  "Animated": false,
  "UsesWallpaper": true,
  "Texture": "pattern.png",
  "Parameters": [
    { "Name": "Strength", "Type": "Slider", "Min": 0, "Max": 1, "Default": "0.5", "Hint": "Shown under the slider." },
    { "Name": "Tint", "Type": "Color", "Default": "#FFFFFF" }
  ]
}
```

| Field | What it does |
|---|---|
| `Animated` | Redrawn over time (at the frame-rate limit in Appearance; paused while a fullscreen app or game is in front). |
| `UsesWallpaper` | The shader reads what's behind the surface: the wallpaper picture, or with live glass what's really behind it. |
| `Texture` | A picture in the folder, available in the shader. |
| `Parameters` | Up to 8 settings (sliders and colors), `P0` to `P7` in the shader, in this order. |

## WPF materials

`material.hlsl` has one function:

```hlsl
float4 Material(float2 uv, float4 surface)
{
    return float4(lerp(surface.rgb, Accent.rgb, P0.x), 1);
}
```

`uv` is 0..1 over the surface, `surface` its own color (the design's background). Return a color with alpha (not
premultiplied); the surface's shape is kept automatically. Available: `TIME`, `SIZE`, `MOUSE`, `HOVER`, `Accent`,
`P0`..`P7` (sliders in `.x`, colors as rgba), `Texture` (a sampler), `WALLPAPER(uv)`, and the helpers `hash21`,
`noise`, `fbm` and `hsv2rgb`. They're compiled to Shader Model 3 (ps_3_0), as WPF runs them: one pass, and a limit of
about 512 instructions.

## Direct3D materials

Add `"Renderer": "direct3d"` and list the passes. Each pass is a function `float4 Name(float2 uv)` in
`material.hlsl`, compiled to Shader Model 5 (ps_5_0), and drawn with Direct3D 11. Every pass after the first can read
the one before it, and a pass can run at a share of the size (`Scale`, 0.1 to 1), which keeps blurs cheap. The last
pass is the result, always at full size.

```json
{
  "Name": "My blur",
  "Renderer": "direct3d",
  "UsesWallpaper": true,
  "Passes": [
    { "Entry": "BlurAcross", "Scale": 0.5 },
    { "Entry": "BlurDown", "Scale": 0.5 },
    { "Entry": "Finish" }
  ]
}
```

Without `Passes`, there's one pass: the function `Material(float2 uv)`.

Available in every pass:

| Name | What it is |
|---|---|
| `TIME`, `SIZE`, `MOUSE`, `HOVER`, `Accent`, `P0`..`P7` | As in WPF materials (`SIZE` is the surface in pixels). |
| `SURFACE` | The surface's own color (the design's background). |
| `BEHIND(uv)` | What's behind the surface (with `"UsesWallpaper": true`). |
| `PREVIOUS(uv)` | The result of the previous pass. |
| `TEXTURE(uv)` | The material's picture, repeated. |
| `PASS_SIZE`, `TEXEL` | This pass's size in pixels, and one pixel in uv. |
| `hash21`, `noise`, `fbm`, `hsv2rgb` | The same helpers. |

The last pass returns its color with alpha (not premultiplied); the surface's shape is kept automatically.

See **Frosted glass** (a two-pass Gaussian blur, then tint and grain) and **Refraction glass** (a glass slab that
bends what's behind) for complete examples.

If Direct3D 11 isn't available (a very old graphics card, or a remote desktop), a Direct3D material shows the plain
background, and the log says why.
