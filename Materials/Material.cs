using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace WindowsCustomizer;

public enum MaterialParameterType { Slider, Color }

/// <summary>One setting of a material, shown in the designer (a slider or a color). Up to 8 per material: P0..P7 in HLSL.</summary>
public sealed class MaterialParameter
{
    public string Name { get; set; } = "Value";
    public MaterialParameterType Type { get; set; } = MaterialParameterType.Slider;
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    /// <summary>A number for sliders, #RRGGBB / #AARRGGBB for colors.</summary>
    public string Default { get; set; } = "0.5";
    public string? Hint { get; set; }
}

/// <summary>
/// A material: a folder with material.hlsl (the shader) and material.json (name, description, settings,
/// optional texture). Built-in ones are next to the app (Materials\), your own in %AppData%\WindowsCustomizer\Materials.
///
/// The shader writes one function: <c>float4 Material(float2 uv, float4 surface)</c>. uv is 0..1 over the
/// surface, surface is its own color (the design's background, not premultiplied). It returns a color with
/// alpha (not premultiplied); the shape (rounded corners) is kept automatically. Available:
/// TIME (seconds), SIZE (pixels), MOUSE (0..1), HOVER (1 while the mouse is over it), Accent (color),
/// P0..P7 (the settings: sliders in .x, colors as rgba), Texture (sampler, if the material has a texture),
/// and helpers hash21, noise, fbm, hsv2rgb.
/// </summary>
public sealed class Material
{
    public string Name { get; set; } = "Material";
    public string? Description { get; set; }
    /// <summary>Changes over time (the app redraws it at the frame-rate limit).</summary>
    public bool Animated { get; set; }
    public List<MaterialParameter> Parameters { get; set; } = new();
    /// <summary>A picture in the material's folder, available in the shader as Texture.</summary>
    public string? Texture { get; set; }

    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public bool BuiltIn { get; set; }
    [JsonIgnore] public string ShaderFile => Path.Combine(Folder, "material.hlsl");
}

/// <summary>Finds materials, compiles them (cached), and makes the effect for a surface.</summary>
internal static class Materials
{
    public static readonly string UserFolder = Path.Combine(AppSettings.Folder, "Materials");
    public static readonly string BuiltInFolder = Path.Combine(AppContext.BaseDirectory, "Materials");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Built-in first, then your own (your folder can't replace a built-in name).</summary>
    public static List<Material> LoadAll()
    {
        var all = new List<Material>();
        void From(string root, bool builtIn)
        {
            if (!Directory.Exists(root)) return;
            foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                var json = Path.Combine(folder, "material.json");
                if (!File.Exists(json) || !File.Exists(Path.Combine(folder, "material.hlsl"))) continue;
                try
                {
                    var material = JsonSerializer.Deserialize<Material>(File.ReadAllText(json), JsonOptions);
                    if (material == null || all.Any(m => m.Name.Equals(material.Name, StringComparison.CurrentCultureIgnoreCase))) continue;
                    material.Folder = folder;
                    material.BuiltIn = builtIn;
                    material.Parameters = material.Parameters.Take(8).ToList();
                    all.Add(material);
                }
                catch (Exception ex) { AppTrace.Log($"Material {folder}: {ex.Message}"); }
            }
        }
        From(BuiltInFolder, builtIn: true);
        From(UserFolder, builtIn: false);
        return all;
    }

    public static Material? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : LoadAll().FirstOrDefault(m => m.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    // ---------- Compiling ----------

    private const string Header = """
        sampler2D SurfaceInput : register(s0);
        sampler2D Texture : register(s1);
        float4 TimeInfo : register(c0);
        float4 SizeInfo : register(c1);
        float4 MouseInfo : register(c2);
        float4 Accent : register(c3);
        float4 P0 : register(c4);
        float4 P1 : register(c5);
        float4 P2 : register(c6);
        float4 P3 : register(c7);
        float4 P4 : register(c8);
        float4 P5 : register(c9);
        float4 P6 : register(c10);
        float4 P7 : register(c11);
        #define TIME TimeInfo.x
        #define SIZE SizeInfo.xy
        #define MOUSE MouseInfo.xy
        #define HOVER MouseInfo.z
        float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
        float noise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(hash21(i), hash21(i + float2(1, 0)), u.x), lerp(hash21(i + float2(0, 1)), hash21(i + float2(1, 1)), u.x), u.y);
        }
        float fbm(float2 p) { float v = 0, a = 0.5; for (int i = 0; i < 4; i++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
        float3 hsv2rgb(float3 c) { float3 p = abs(frac(c.xxx + float3(1, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0); return c.z * lerp(1, saturate(p - 1), c.y); }

        """;

    private const string Footer = """

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 raw = tex2D(SurfaceInput, uv);
            float4 surface = float4(raw.rgb / max(raw.a, 0.0001), raw.a);
            float4 c = Material(uv, surface);
            float a = saturate(c.a) * raw.a;
            return float4(saturate(c.rgb) * a, a);
        }
        """;

    private static readonly Dictionary<string, (DateTime Written, PixelShader? Shader, string? Error)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The compiled shader (cached until the file changes), or why it doesn't compile.</summary>
    public static PixelShader? Compile(Material material, out string? error)
    {
        error = null;
        var written = File.Exists(material.ShaderFile) ? File.GetLastWriteTimeUtc(material.ShaderFile) : DateTime.MinValue;
        if (Cache.TryGetValue(material.ShaderFile, out var cached) && cached.Written == written)
        {
            error = cached.Error;
            return cached.Shader;
        }

        PixelShader? shader = null;
        try
        {
            string body = File.ReadAllText(material.ShaderFile);
            // #line: the compiler's error line numbers point into material.hlsl, not into our added part.
            string source = Header + "#line 1 \"material.hlsl\"\n" + body + Footer;
            var bytes = ShaderCompiler.Compile(source, "material.hlsl", out error);
            if (bytes != null)
            {
                shader = new PixelShader();
                shader.SetStreamSource(new MemoryStream(bytes));
                shader.Freeze();
            }
        }
        catch (Exception ex) { error = ex.Message; }
        if (error != null) AppTrace.Log($"Material \"{material.Name}\" doesn't compile: {error}");
        Cache[material.ShaderFile] = (written, shader, error);
        return shader;
    }

    /// <summary>The effect for a surface with the design's settings for this material (null if it doesn't compile).</summary>
    public static MaterialEffect? CreateEffect(Material material, IReadOnlyDictionary<string, string>? values, Color accent, out string? error)
    {
        var shader = Compile(material, out error);
        if (shader == null) return null;
        var effect = new MaterialEffect(shader) { Accent = accent, Animated = material.Animated };
        for (int i = 0; i < material.Parameters.Count; i++)
        {
            var parameter = material.Parameters[i];
            string text = values != null && values.TryGetValue(parameter.Name, out var v) ? v : parameter.Default;
            effect.SetParameter(i, parameter, text);
        }
        if (material.Texture is { Length: > 0 } texture && File.Exists(Path.Combine(material.Folder, texture)))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.Combine(material.Folder, texture));
                image.EndInit();
                image.Freeze();
                effect.Texture = new ImageBrush(image) { TileMode = TileMode.Tile };
            }
            catch (Exception ex) { AppTrace.Log($"Material \"{material.Name}\" texture: {ex.Message}"); }
        }
        return effect;
    }
}
