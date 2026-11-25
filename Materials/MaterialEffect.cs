using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace WindowsCustomizer;

/// <summary>
/// A material's shader as a WPF effect, with the standard inputs (time, size, mouse, accent, 8 settings,
/// a texture). Apply it to an element that only draws the surface (not its icons and text).
/// </summary>
public sealed class MaterialEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(MaterialEffect), 0);
    public static readonly DependencyProperty TextureProperty = RegisterPixelShaderSamplerProperty("Texture", typeof(MaterialEffect), 1);
    public static readonly DependencyProperty TimeInfoProperty = Register("TimeInfo", 0);
    public static readonly DependencyProperty SizeInfoProperty = Register("SizeInfo", 1);
    public static readonly DependencyProperty MouseInfoProperty = Register("MouseInfo", 2);
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register("Accent", typeof(Color), typeof(MaterialEffect), new UIPropertyMetadata(Colors.DodgerBlue, PixelShaderConstantCallback(3)));

    private static readonly DependencyProperty[] ParameterProperties =
        Enumerable.Range(0, 8).Select(i => Register($"P{i}", 4 + i)).ToArray();

    private static DependencyProperty Register(string name, int register) =>
        DependencyProperty.Register(name, typeof(Point4D), typeof(MaterialEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(register)));

    /// <summary>Changes over time: the <see cref="MaterialClock"/> advances <see cref="Seconds"/>.</summary>
    public bool Animated { get; init; }

    public MaterialEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TextureProperty);
        UpdateShaderValue(TimeInfoProperty);
        UpdateShaderValue(SizeInfoProperty);
        UpdateShaderValue(MouseInfoProperty);
        UpdateShaderValue(AccentProperty);
        foreach (var p in ParameterProperties) UpdateShaderValue(p);
        // The surface's own picture is the shader's input (s0).
        Input = ImplicitInput;
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Texture { get => (Brush)GetValue(TextureProperty); set => SetValue(TextureProperty, value); }
    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public double Seconds
    {
        get => ((Point4D)GetValue(TimeInfoProperty)).X;
        set => SetValue(TimeInfoProperty, new Point4D(value, 0, 0, 0));
    }

    public void SetSize(double width, double height) =>
        SetValue(SizeInfoProperty, new Point4D(width, height, width > 0 ? 1 / width : 0, height > 0 ? 1 / height : 0));

    /// <summary>The mouse over the surface (0..1), or null when it isn't over it.</summary>
    public void SetMouse(Point? relative) =>
        SetValue(MouseInfoProperty, relative is { } p ? new Point4D(p.X, p.Y, 1, 0) : new Point4D(MouseX, MouseY, 0, 0));

    private double MouseX => ((Point4D)GetValue(MouseInfoProperty)).X;
    private double MouseY => ((Point4D)GetValue(MouseInfoProperty)).Y;

    /// <summary>A setting: a slider value (in .x) or a color (rgba 0..1).</summary>
    public void SetParameter(int index, MaterialParameter parameter, string text)
    {
        if (index < 0 || index >= ParameterProperties.Length) return;
        Point4D value;
        if (parameter.Type == MaterialParameterType.Color)
        {
            var color = TaskbarView.ParseColor(text, TaskbarView.ParseColor(parameter.Default, Colors.White));
            value = new Point4D(color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0);
        }
        else
        {
            double number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
                : double.TryParse(parameter.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            value = new Point4D(number, 0, 0, 0);
        }
        SetValue(ParameterProperties[index], value);
    }
}
