using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// One desktop widget in its own small window, kept just above the desktop: above Explorer's desktop (and a
/// Wallpaper Engine wallpaper), or above Windows Customizer's own desktop in shell mode, but under every other
/// window. Drag it to move it, the corner to resize it (unless locked); right-click for its settings.
/// </summary>
internal sealed class WidgetWindow : Window
{
    public DesktopWidget Widget { get; }
    private readonly WidgetContent _content;
    private readonly Action _changed;
    private readonly Action<WidgetWindow> _openSettings, _remove;
    private IntPtr _hwnd;

    /// <param name="changed">The widget's place, size or text changed (save it).</param>
    public WidgetWindow(DesktopWidget widget, Action changed, Action<WidgetWindow> openSettings, Action<WidgetWindow> remove)
    {
        Widget = widget;
        _changed = changed;
        _openSettings = openSettings;
        _remove = remove;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Title = "Windows Customizer widget";
        Left = widget.Left;
        Top = widget.Top;
        Width = Math.Clamp(widget.Width, 80, 2000);
        Height = Math.Clamp(widget.Height, 50, 2000);

        var foreground = TaskbarView.ParseColor(widget.Foreground, Colors.White);
        var windowsAccent = SystemParameters.WindowGlassBrush is SolidColorBrush glass
            ? Color.FromRgb(glass.Color.R, glass.Color.G, glass.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);
        var accent = TaskbarView.ParseColor(widget.Accent, windowsAccent);
        Foreground = new SolidColorBrush(foreground);
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(widget.FontFamily) ? "Segoe UI" : widget.FontFamily); }
        catch (ArgumentException) { /* keep Segoe UI */ }
        FontSize = Math.Clamp(widget.FontSize, 8, 72);

        _content = WidgetContent.Create(widget, foreground, accent, changed);

        // The background (and its material) on its own layer behind the content, in the same shape.
        var corner = new CornerRadius(Math.Clamp(widget.CornerRadius, 0, 100));
        var materialLayer = new Border { CornerRadius = corner, Background = new SolidColorBrush(TaskbarView.ParseColor(widget.Background, Color.FromArgb(0x99, 0x20, 0x20, 0x20))) };
        var frame = new Border { CornerRadius = corner, Padding = new Thickness(14), Child = _content.View };
        var layers = new Grid();
        layers.Children.Add(materialLayer);
        layers.Children.Add(frame);
        layers.Children.Add(ResizeCorner());
        Content = layers;
        if (MaterialHost.Attach(materialLayer, layers, widget.Material, widget.MaterialValues, accent) is { } error)
            AppTrace.Log($"Widget {widget}: material: {error}");

        layers.ContextMenu = Menu();
        MouseLeftButtonDown += (_, e) =>
        {
            if (Widget.Locked || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { return; }
            Widget.Left = Left;
            Widget.Top = Top;
            _changed();
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW)); // not in Alt+Tab or the taskbar
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        };
        Loaded += (_, _) => GoBehindEverything();
        Closed += (_, _) => _content.Dispose();
    }

    /// <summary>Settings, lock, remove.</summary>
    private ContextMenu Menu()
    {
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "Widget settings..." };
        settings.Click += (_, _) => _openSettings(this);
        var locked = new MenuItem { Header = "Lock position and size", IsCheckable = true, IsChecked = Widget.Locked };
        locked.Click += (_, _) => { Widget.Locked = locked.IsChecked; _changed(); UpdateResizeCorner(); };
        var remove = new MenuItem { Header = "Remove widget" };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show($"Remove the {Widget} widget?", "Windows Customizer", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                _remove(this);
        };
        menu.Items.Add(settings);
        menu.Items.Add(locked);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    // ---------- Resizing ----------

    private Thumb? _grip;

    /// <summary>A small corner at the bottom right to drag; shows while the mouse is on the widget (not when locked).</summary>
    private Thumb ResizeCorner()
    {
        _grip = new Thumb
        {
            Width = 14, Height = 14, Cursor = Cursors.SizeNWSE, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 3),
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
                  <Grid Background="Transparent">
                    <Path Data="M 12,2 L 2,12 M 12,6 L 6,12 M 12,10 L 10,12" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" />
                  </Grid>
                </ControlTemplate>
                """),
            Foreground = Foreground,
        };
        _grip.DragDelta += (_, e) =>
        {
            Width = Math.Max(80, Width + e.HorizontalChange);
            Height = Math.Max(50, Height + e.VerticalChange);
        };
        _grip.DragCompleted += (_, _) => { Widget.Width = Width; Widget.Height = Height; _changed(); };
        MouseEnter += (_, _) => { if (!Widget.Locked) _grip.Opacity = 0.7; };
        MouseLeave += (_, _) => _grip.Opacity = 0;
        UpdateResizeCorner();
        return _grip;
    }

    private void UpdateResizeCorner()
    {
        if (_grip == null) return;
        _grip.IsEnabled = !Widget.Locked;
        if (Widget.Locked) _grip.Opacity = 0;
    }

    // ---------- Staying on the desktop ----------

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
    private const uint GW_HWNDPREV = 3;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    /// <summary>The window the widgets sit on: Explorer's desktop (the one with the icons), or ours in shell mode.</summary>
    private static IntPtr DesktopHost()
    {
        if (DesktopWindow.BackHandles.Count > 0) return DesktopWindow.BackHandles[0];
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            found = hwnd; // Progman, or a WorkerW (with Wallpaper Engine or after Win+Tab)
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Where to go in the window order: right above the desktop (null: stay where it is).</summary>
    private IntPtr? PlaceAfter()
    {
        var desktop = DesktopHost();
        if (desktop == IntPtr.Zero) return HWND_BOTTOM;
        var above = GetWindow(desktop, GW_HWNDPREV); // the window just above the desktop
        if (above == _hwnd) return null;             // already there
        if (above == IntPtr.Zero) return HWND_BOTTOM;
        return above; // "insert after it" = just under it = just above the desktop
    }

    private void GoBehindEverything()
    {
        if (_hwnd == IntPtr.Zero || PlaceAfter() is not { } after) return;
        SetWindowPos(_hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Whatever moves the widget in the window order (clicking it, typing in a note), it stays on the desktop.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING) return IntPtr.Zero;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SWP_NOZORDER) != 0) return IntPtr.Zero;
        if (PlaceAfter() is { } after) pos.hwndInsertAfter = after;
        else pos.flags |= SWP_NOZORDER;
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }
}
