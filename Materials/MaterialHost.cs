using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// Puts a material on a background layer (a Border that only draws the surface, behind the content) and keeps
/// its inputs up to date: size, mouse (from <paramref name="mouseArea"/>), and the clock for animated ones.
/// Used by the taskbar and the Alt+Tab panel.
/// </summary>
internal static class MaterialHost
{
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("MaterialHostHooked", typeof(bool), typeof(MaterialHost));

    /// <summary>Sets the layer's material (or none). Returns why it can't be shown, or null.</summary>
    public static string? Attach(Border layer, UIElement mouseArea, string? materialName, IReadOnlyDictionary<string, string>? values, Color accent)
    {
        layer.Effect = null;
        if (Materials.Find(materialName) is not { } material) return null;
        var effect = Materials.CreateEffect(material, values, accent, out var error);
        if (effect == null) return error;
        layer.Effect = effect;
        UpdateSize(layer);
        MaterialClock.Add(effect, layer);
        if (material.UsesWallpaper) WallpaperBehind.Track(layer, effect);

        // Size and mouse handlers once per layer (they work with whatever effect it has now).
        if (!(bool)layer.GetValue(HookedProperty))
        {
            layer.SetValue(HookedProperty, true);
            layer.SizeChanged += (_, _) => UpdateSize(layer);
            mouseArea.MouseMove += (_, e) =>
            {
                if (layer.Effect is not MaterialEffect current || layer.ActualWidth <= 0) return;
                var at = e.GetPosition(layer);
                current.SetMouse(new Point(at.X / layer.ActualWidth, at.Y / Math.Max(1, layer.ActualHeight)));
            };
            mouseArea.MouseLeave += (_, _) => (layer.Effect as MaterialEffect)?.SetMouse(null);
        }
        return null;
    }

    private static void UpdateSize(Border layer)
    {
        if (layer.Effect is not MaterialEffect effect) return;
        var dpi = VisualTreeHelper.GetDpi(layer);
        effect.SetSize(layer.ActualWidth * dpi.DpiScaleX, layer.ActualHeight * dpi.DpiScaleY);
    }
}
