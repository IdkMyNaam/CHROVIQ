using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The custom taskbar for one monitor: an app bar on the top or bottom edge, filled with the
/// modules of a <see cref="TaskbarDesign"/> (see AppBarWindow.Modules.cs) and styled by its Look.
/// The Windows taskbar keeps running; this bar only reserves its own strip of screen.
/// </summary>
public partial class AppBarWindow : Window
{
    private readonly TaskbarEdge _edge;
    private readonly string _monitorDevice;
    private readonly bool _onlyOwnMonitorWindows;
    private readonly TaskbarDesign _design;
    private readonly TaskbarLook _look;
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppBar? _appBar;
    private WindowTracker? _tracker;

    /// <summary>Fires once a second, for modules that change over time (clock, battery).</summary>
    private event Action? EverySecond;

    /// <summary>Entries for the Menu module; set by the main window.</summary>
    public Func<IEnumerable<AppMenuItem>>? GetMenuItems { get; set; }

    /// <param name="monitorDevice">The monitor this bar docks on (see <see cref="Monitors"/>).</param>
    /// <param name="onlyOwnMonitorWindows">Show only buttons for windows on this monitor, instead of all windows.</param>
    /// <param name="design">Which modules to show where, and how the bar looks.</param>
    public AppBarWindow(string monitorDevice, TaskbarEdge edge, bool onlyOwnMonitorWindows, TaskbarDesign design)
    {
        _monitorDevice = monitorDevice;
        _edge = edge;
        _onlyOwnMonitorWindows = onlyOwnMonitorWindows;
        _design = design;
        _look = design.Look ?? new TaskbarLook();
        InitializeComponent();
        ApplyLook();

        // Accent line under the active window; follows accent color changes (including from our profiles).
        UpdateAccentBrush();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

        SourceInitialized += (_, _) => SetUp();
        Closed += (_, _) => TearDown();

        _secondTimer.Tick += (_, _) => EverySecond?.Invoke();
        _secondTimer.Start();
    }

    private bool IsFloating => _look.Floating != null;

    /// <summary>Bar height in WPF units, kept within sensible limits.</summary>
    private double BarHeight => Math.Clamp(_look.Height, 20, 200);

    private double DockMargin => IsFloating ? Math.Clamp(_look.Floating!.Margin, 0, 100) : 0;

    /// <summary>Runs once the window has a handle, before it is first shown.</summary>
    private void SetUp()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: not listed in Alt+Tab or in our own window list.
        // No-activate: clicking the bar doesn't take focus away from the app you were using,
        // so "click the active window's button to minimize it" works.
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        // Blur covers the whole window; a floating dock's window spans the screen width, so no blur there (yet).
        if (_look.Blur && !IsFloating) EnableBlur(hwnd);

        // Only track windows when the design actually shows them.
        if (_design.AllModules.Any(m => m.Type == ModuleType.WindowList))
        {
            _tracker = _onlyOwnMonitorWindows
                ? new WindowTracker(this, w => Monitors.DeviceOf(w) == _monitorDevice)
                : new WindowTracker(this);
        }

        Root.Child = BuildLayout();

        // Reserve the bar's height (plus the gap around a floating dock), or nothing for a dock over windows.
        double reserve = IsFloating && !_look.Floating!.ReserveSpace ? 0 : BarHeight + 2 * DockMargin;
        _appBar = new AppBar(this, _monitorDevice, _edge, reserve, PlaceWindow);
        _appBar.FullscreenChanged += fullscreen =>
        {
            // Get out of the way of games and fullscreen videos on this monitor.
            if (fullscreen) Hide();
            else Show();
        };
    }

    private void TearDown()
    {
        _secondTimer.Stop();
        _tracker?.Dispose();
        _appBar?.Dispose(); // gives the reserved screen space back
    }

    // ---------- Position ----------

    /// <summary>
    /// Called by <see cref="AppBar"/> with the reserved strip (physical pixels).
    /// Full-width bar: fills the strip. Floating dock: the window spans the strip minus the margins,
    /// and the dock itself is drawn centered inside it, as wide as its contents. The rest of the window
    /// is fully transparent, which Windows treats as click-through, so it never blocks the windows behind.
    /// </summary>
    private void PlaceWindow(RECT strip, double scale)
    {
        int height = (int)Math.Round(BarHeight * scale);
        int margin = (int)Math.Round(DockMargin * scale);
        int left = strip.Left + margin;
        int right = strip.Right - margin;

        // Top edge: measure from the top of the strip; bottom edge: from its bottom.
        int top = _edge == TaskbarEdge.Top ? strip.Top + margin : strip.Bottom - margin - height;
        AppBar.MoveWindow(this, new RECT { Left = left, Top = top, Right = right, Bottom = top + height });
    }

    // ---------- Look ----------

    /// <summary>Applies the design's colors, font, sizes and corners.</summary>
    private void ApplyLook()
    {
        var background = ParseColor(_look.Background, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C));
        var foreground = ParseColor(_look.Foreground, Colors.White);

        // With blur on, Windows paints the tinted blur; the bar itself stays see-through.
        // Alpha 1 instead of 0: fully transparent pixels would let clicks fall through the empty parts of the bar.
        bool blurs = _look.Blur && !IsFloating;
        Resources["BarBackground"] = new SolidColorBrush(blurs ? Color.FromArgb(1, 0, 0, 0) : background);
        Resources["BarForeground"] = new SolidColorBrush(foreground);

        // Hover/pressed/active highlights are the text color, faintly: works on dark and light bars.
        Resources["BarHover"] = new SolidColorBrush(Color.FromArgb(0x26, foreground.R, foreground.G, foreground.B));
        Resources["BarPressed"] = new SolidColorBrush(Color.FromArgb(0x14, foreground.R, foreground.G, foreground.B));
        Resources["BarActive"] = new SolidColorBrush(Color.FromArgb(0x33, foreground.R, foreground.G, foreground.B));
        Resources["ButtonCornerRadius"] = new CornerRadius(Math.Max(0, _look.ButtonCornerRadius));

        Surface.CornerRadius = new CornerRadius(Math.Max(0, _look.CornerRadius));
        // Floating dock: only as wide as its contents, centered in the (transparent) window.
        Surface.HorizontalAlignment = IsFloating ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_look.FontFamily) ? "Segoe UI" : _look.FontFamily); }
        catch (ArgumentException) { /* bad font name: keep Segoe UI */ }
        FontSize = Math.Clamp(_look.FontSize, 6, 48);
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" (also color names like "Black"); a bad value uses <paramref name="fallback"/>.</summary>
    private static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(text.Trim()); }
        catch (FormatException) { return fallback; }
    }

    /// <summary>
    /// Blurs what is behind the bar, tinted with the Background color.
    /// Undocumented Windows feature: if it fails, the bar simply shows its normal background.
    /// </summary>
    private void EnableBlur(IntPtr hwnd)
    {
        var tint = ParseColor(_look.Background, Color.FromArgb(0x80, 0x1C, 0x1C, 0x1C));
        var policy = new ACCENT_POLICY
        {
            AccentState = AccentState.AcrylicBlurBehind,
            AccentFlags = 2,
            // 0xAABBGGRR; acrylic needs some transparency to show the blur.
            GradientColor = ((uint)Math.Min(tint.A, (byte)0xCC) << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R,
        };

        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = pointer, SizeOfData = size };
            if (!SetWindowCompositionAttribute(hwnd, ref data))
                Resources["BarBackground"] = new SolidColorBrush(tint); // blur refused: draw the color instead
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        // Windows 11: round the blurred area's corners too (ignored on Windows 10).
        if (_look.CornerRadius > 0)
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WindowGlassBrush))
            UpdateAccentBrush();
    }

    private void UpdateAccentBrush()
    {
        // The design's own accent wins; otherwise WindowGlassBrush, the title bar color Windows derives from the accent.
        var windowsAccent = SystemParameters.WindowGlassBrush is SolidColorBrush b
            ? Color.FromRgb(b.Color.R, b.Color.G, b.Color.B) // drop its transparency
            : Color.FromRgb(0x00, 0x78, 0xD4);
        Resources["AccentBrush"] = new SolidColorBrush(ParseColor(_look.Accent, windowsAccent));
    }

    // ---------- Window list ----------

    private void WindowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskWindow window)
        {
            WindowTracker.ActivateOrMinimize(window.Handle);
            _tracker?.Refresh(); // show the new active window right away
        }
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskWindow window)
            WindowTracker.Close(window.Handle);
    }

    // ---------- App menu ----------

    /// <summary>Opens the app menu next to <paramref name="anchor"/>, on the screen side of the bar.</summary>
    private void ShowAppMenu(UIElement anchor)
    {
        if (GetMenuItems == null) return;

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = _edge == TaskbarEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
        };
        foreach (var entry in GetMenuItems())
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem
            {
                Header = entry.Text,
                IsChecked = entry.IsChecked,
                IsEnabled = entry.IsEnabled,
                InputGestureText = entry.Shortcut ?? "",
            };
            var onClick = entry.OnClick;
            item.Click += (_, _) => onClick?.Invoke();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
