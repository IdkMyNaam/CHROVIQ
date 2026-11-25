using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>Colors for the snap overlays: the Windows accent.</summary>
internal static class SnapColors
{
    public static Color Accent => SystemParameters.WindowGlassBrush is SolidColorBrush b
        ? Color.FromRgb(b.Color.R, b.Color.G, b.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);

    public static Brush Fill(byte alpha) { var c = Accent; var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); brush.Freeze(); return brush; }
}

/// <summary>
/// A see-through, click-through window over one monitor that draws zones: all zones of a layout (Shift-drag,
/// the one under the mouse stronger), or just the zone a window will go to (from the picker).
/// </summary>
internal sealed class ZonesOverlay : Window
{
    private readonly Canvas _canvas = new();
    private IntPtr _hwnd;
    private RECT _monitor;
    private double _scale = 1;

    public ZonesOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            // Click-through and never focused: the window being dragged keeps the mouse.
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | 0x20 /* WS_EX_TRANSPARENT */));
        };
    }

    /// <summary>Shows these zones (physical pixels) on the monitor; <paramref name="highlight"/> is drawn stronger (-1: none).</summary>
    public void ShowZones(RECT monitor, double scale, IReadOnlyList<RECT> zones, int highlight)
    {
        if (!IsVisible || !monitor.Equals(_monitor))
        {
            _monitor = monitor;
            _scale = scale;
            if (!IsVisible) Show();
            SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, SWP_NOACTIVATE);
        }
        _canvas.Children.Clear();
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            bool on = i == highlight;
            var box = new Border
            {
                Width = (z.Right - z.Left) / _scale, Height = (z.Bottom - z.Top) / _scale,
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(on ? 3 : 2),
                Background = SnapColors.Fill(on ? (byte)0x70 : (byte)0x28), BorderBrush = SnapColors.Fill(on ? (byte)0xFF : (byte)0x90),
            };
            Canvas.SetLeft(box, (z.Left - _monitor.Left) / _scale);
            Canvas.SetTop(box, (z.Top - _monitor.Top) / _scale);
            _canvas.Children.Add(box);
        }
    }
}

/// <summary>
/// The layout picker at the top of a monitor: every layout as a small picture of its zones. While dragging a
/// window it's read from the mouse position (the drag keeps the mouse); after Win+Alt+Z it's clicked normally.
/// </summary>
internal sealed class LayoutPicker : Window
{
    private readonly List<(Border Zone, SnapLayout Layout, int Index)> _zones = new();
    private readonly WrapPanel _layouts = new() { Orientation = Orientation.Horizontal };
    private IntPtr _hwnd;

    /// <summary>Clicked (Win+Alt+Z mode): the layout and zone index.</summary>
    public event Action<SnapLayout, int>? Picked;

    public LayoutPicker(IEnumerable<SnapLayout> layouts, bool clickable)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = clickable;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        foreach (var layout in layouts) _layouts.Children.Add(Preview(layout, clickable));
        _layouts.MaxWidth = 4 * 136;
        var frame = new Border
        {
            Child = _layouts, Padding = new Thickness(8), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x24, 0x24, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        Content = frame;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | (clickable ? 0 : WS_EX_NOACTIVATE)));
        };
        if (clickable)
        {
            Deactivated += (_, _) => Close();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }
    }

    /// <summary>One layout: its zones as small rounded boxes, 120 wide in the monitor's proportions.</summary>
    private FrameworkElement Preview(SnapLayout layout, bool clickable)
    {
        const double width = 120, height = 72;
        var canvas = new Canvas { Width = width, Height = height };
        var zones = layout.Zones();
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var box = new Border
            {
                Width = Math.Max(4, z.Width * width - 4), Height = Math.Max(4, z.Height * height - 4), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            };
            Canvas.SetLeft(box, z.Left * width + 2);
            Canvas.SetTop(box, z.Top * height + 2);
            canvas.Children.Add(box);
            _zones.Add((box, layout, i));
            if (clickable)
            {
                int index = i;
                box.Cursor = Cursors.Hand;
                box.MouseEnter += (_, _) => Highlight(box);
                box.MouseLeave += (_, _) => Highlight(null);
                box.MouseLeftButtonUp += (_, _) => { Picked?.Invoke(layout, index); Close(); };
            }
        }
        var label = new TextBlock { Text = layout.Name, Foreground = Brushes.White, FontSize = 11, Opacity = 0.8, Margin = new Thickness(2, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = width };
        var stack = new StackPanel { Margin = new Thickness(4) };
        stack.Children.Add(canvas);
        stack.Children.Add(label);
        return stack;
    }

    private void Highlight(Border? on)
    {
        foreach (var (zone, _, _) in _zones)
            zone.Background = zone == on ? SnapColors.Fill(0xFF) : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>Shows it at the top center of the monitor (physical pixels).</summary>
    public void ShowOn(RECT monitor, double scale)
    {
        Show();
        UpdateLayout();
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int x = monitor.Left + (monitor.Right - monitor.Left - w) / 2, y = monitor.Top + (int)(8 * scale);
        SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, w, h, SWP_NOACTIVATE);
        if (ShowActivated) Activate();
    }

    /// <summary>The picker's place on screen (physical pixels), to know whether the mouse is still over it.</summary>
    public RECT ScreenRect => GetWindowRect(_hwnd, out var r) ? r : default;

    /// <summary>The zone under this screen point while dragging (highlighted), or null.</summary>
    public (SnapLayout Layout, int Index)? ZoneAt(POINT screen)
    {
        if (!IsVisible) return null;
        var point = PointFromScreen(new Point(screen.X, screen.Y));
        foreach (var (zone, layout, index) in _zones)
        {
            var topLeft = zone.TranslatePoint(new Point(0, 0), this);
            if (new Rect(topLeft, zone.RenderSize).Contains(point))
            {
                Highlight(zone);
                return (layout, index);
            }
        }
        Highlight(null);
        return null;
    }
}
