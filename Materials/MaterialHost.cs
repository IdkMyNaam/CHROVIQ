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
    private static readonly DependencyProperty Direct3DProperty =
        DependencyProperty.RegisterAttached("MaterialHostDirect3D", typeof(D3DMaterialSurface), typeof(MaterialHost));

    public static string? Attach(Border layer, UIElement mouseArea, string? materialName, IReadOnlyDictionary<string, string>? values, Color accent)
    {
        layer.Effect = null;
        (layer.GetValue(Direct3DProperty) as D3DMaterialSurface)?.Dispose(); // a Direct3D material it had before
        layer.ClearValue(Direct3DProperty);
        if (Materials.Find(materialName) is not { } material) return null;
        if (material.IsDirect3D) return AttachDirect3D(layer, mouseArea, material, values, accent);
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

    /// <summary>A Direct3D material: drawn by <see cref="D3DMaterialSurface"/> as the layer's background.</summary>
    private static string? AttachDirect3D(Border layer, UIElement mouseArea, Material material, IReadOnlyDictionary<string, string>? values, Color accent)
    {
        var passes = D3DMaterialShaders.Compile(material, out var error);
        if (passes == null) return error;
        if (D3DGraphics.Shared == null) return "Direct3D isn't available here: " + D3DGraphics.Problem;
        var surface = new D3DMaterialSurface(layer, material, passes, values, accent);
        layer.SetValue(Direct3DProperty, surface);
        mouseArea.MouseMove += (_, e) =>
        {
            if (!ReferenceEquals(layer.GetValue(Direct3DProperty), surface) || layer.ActualWidth <= 0) return;
            var at = e.GetPosition(layer);
            surface.SetMouse(new Point(at.X / layer.ActualWidth, at.Y / Math.Max(1, layer.ActualHeight)));
        };
        mouseArea.MouseLeave += (_, _) => { if (ReferenceEquals(layer.GetValue(Direct3DProperty), surface)) surface.SetMouse(null); };
        // Its GPU resources go with its window.
        void HookWindow()
        {
            if (Window.GetWindow(layer) is { } window) window.Closed += (_, _) => surface.Dispose();
        }
        if (layer.IsLoaded) HookWindow();
        else layer.Loaded += (_, _) => HookWindow();
        return null;
    }

    private static void UpdateSize(Border layer)
    {
        if (layer.Effect is not MaterialEffect effect) return;
        var dpi = VisualTreeHelper.GetDpi(layer);
        effect.SetSize(layer.ActualWidth * dpi.DpiScaleX, layer.ActualHeight * dpi.DpiScaleY);
    }
}
