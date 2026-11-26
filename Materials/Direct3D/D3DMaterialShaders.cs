using System.IO;
using System.Text.RegularExpressions;

namespace WindowsCustomizer;

/// <summary>
/// Compiles a Direct3D material: each pass is a function <c>float4 Name(float2 uv)</c> in material.hlsl, compiled to
/// Shader Model 5 (ps_5_0). Available in every pass:
/// TIME, SIZE (the surface in pixels), MOUSE (0..1), HOVER, Accent, SURFACE (the design's background color),
/// P0..P7 (the settings), PASS_SIZE and TEXEL (this pass's size in pixels and one pixel in uv),
/// BEHIND(uv) (what's behind the surface: the wallpaper, or live glass), PREVIOUS(uv) (the previous pass),
/// TEXTURE(uv) (the material's picture, repeated), and the helpers hash21, noise, fbm, hsv2rgb.
/// The last pass returns the color with its alpha (not premultiplied); the surface's shape is kept automatically.
/// </summary>
internal static class D3DMaterialShaders
{
    private const string Header = """
        Texture2D BehindTexture : register(t0);
        Texture2D PreviousTexture : register(t1);
        Texture2D MaterialTexture : register(t2);
        SamplerState Clamped : register(s0);
        SamplerState Repeated : register(s1);
        cbuffer Values : register(b0)
        {
            float4 TimeInfo;
            float4 SizeInfo;
            float4 MouseInfo;
            float4 Accent;
            float4 SurfaceColor;
            float4 PassInfo;
            float4 P0, P1, P2, P3, P4, P5, P6, P7;
        };
        #define TIME TimeInfo.x
        #define SIZE SizeInfo.xy
        #define MOUSE MouseInfo.xy
        #define HOVER MouseInfo.z
        #define SURFACE SurfaceColor
        #define PASS_SIZE PassInfo.xy
        #define TEXEL PassInfo.zw
        #define BEHIND(p) float4(BehindTexture.Sample(Clamped, p).rgb, 1)
        #define PREVIOUS(p) PreviousTexture.Sample(Clamped, p)
        #define TEXTURE(p) MaterialTexture.Sample(Repeated, p)
        float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
        float noise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(hash21(i), hash21(i + float2(1, 0)), u.x), lerp(hash21(i + float2(0, 1)), hash21(i + float2(1, 1)), u.x), u.y);
        }
        float fbm(float2 p) { float v = 0, a = 0.5; for (int i = 0; i < 5; i++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
        float3 hsv2rgb(float3 c) { float3 p = abs(frac(c.xxx + float3(1, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0); return c.z * lerp(1, saturate(p - 1), c.y); }

        """;

    /// <summary>The one vertex shader: a triangle that covers the whole target, with uv 0..1.</summary>
    public const string VertexSource = """
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V main(uint id : SV_VertexID)
        {
            V v;
            v.uv = float2((id << 1) & 2, id & 2);
            v.pos = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return v;
        }
        """;

    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$");

    private static readonly Dictionary<string, (DateTime Written, List<byte[]>? Passes, string? Error)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The passes of a material, compiled (cached until material.hlsl changes), or why not.</summary>
    public static List<byte[]>? Compile(Material material, out string? error)
    {
        error = null;
        var written = File.Exists(material.ShaderFile) ? File.GetLastWriteTimeUtc(material.ShaderFile) : DateTime.MinValue;
        string key = material.ShaderFile + "|" + string.Join(",", PassesOf(material).Select(p => p.Entry));
        if (Cache.TryGetValue(key, out var cached) && cached.Written == written) { error = cached.Error; return cached.Passes; }

        List<byte[]>? compiled = new();
        try
        {
            string body = File.ReadAllText(material.ShaderFile);
            var passes = PassesOf(material);
            for (int i = 0; i < passes.Count; i++)
            {
                string entry = passes[i].Entry.Trim();
                if (!Identifier.IsMatch(entry)) { error = $"\"{entry}\" isn't a valid function name for a pass."; compiled = null; break; }
                bool last = i == passes.Count - 1;
                // The last pass: premultiplied, as the surface shows it. Earlier passes: as the function returns it.
                string main = last
                    ? $"\nfloat4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {{ float4 c = {entry}(uv); float a = saturate(c.a); return float4(saturate(c.rgb) * a, a); }}\n"
                    : $"\nfloat4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {{ return {entry}(uv); }}\n";
                string source = Header + "#line 1 \"material.hlsl\"\n" + body + main;
                var bytes = ShaderCompiler.Compile(source, "material.hlsl", "main", "ps_5_0", out error);
                if (bytes == null) { error = $"Pass \"{entry}\": {error}"; compiled = null; break; }
                compiled.Add(bytes);
            }
        }
        catch (Exception ex) { error = ex.Message; compiled = null; }
        if (error != null) AppTrace.Log($"Material \"{material.Name}\" (Direct3D) doesn't compile: {error}");
        Cache[key] = (written, compiled, error);
        return compiled;
    }

    /// <summary>The material's passes; none listed means one pass, the function Material.</summary>
    public static List<MaterialPass> PassesOf(Material material) =>
        material.Passes is { Count: > 0 } passes ? passes.Take(8).ToList() : new List<MaterialPass> { new() };
}
