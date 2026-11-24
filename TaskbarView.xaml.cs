using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>
/// Draws one taskbar from a <see cref="TaskbarDesign"/>: its look, zones and modules.
/// The real bars (<see cref="AppBarWindow"/>) host one each; the taskbar designer uses one as its preview.
/// </summary>
public partial class TaskbarView : UserControl
{
    private readonly TaskbarDesign _design;
    private readonly TaskbarLook _look;
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WindowTracker? _tracker;
    private bool _built;

    /// <summary>Fires once a second, for modules that change over time (clock, battery).</summary>
    private event Action? EverySecond;

    /// <summary>Entries for the Menu module.</summary>
    public Func<IEnumerable<AppMenuItem>>? GetMenuItems { get; set; }

    /// <summary>Optional: which windows the window list shows (for example only those on this monitor).</summary>
    public Func<IntPtr, bool>? WindowFilter { get; set; }

    /// <summary>The screen edge the bar is on; menus open toward the middle of the screen.</summary>
    public TaskbarEdge Edge { get; set; } = TaskbarEdge.Top;

    public TaskbarView(TaskbarDesign design)
    {
        _design = design;
        _look = design.Look ?? new TaskbarLook();
        InitializeComponent();
        ApplyLook();
        UpdateAccentBrush();

        Loaded += (_, _) => Build();
        Unloaded += (_, _) => TearDown();
        _secondTimer.Tick += (_, _) => EverySecond?.Invoke();

        // Right-click menus of window buttons (they bubble up to here).
        ContextMenuOpening += (_, _) => _openMenus++;
        ContextMenuClosing += (_, _) => _openMenus = Math.Max(0, _openMenus - 1);
    }

    private int _openMenus;

    /// <summary>True while one of the bar's menus is open (auto-hide waits for it).</summary>
    public bool IsMenuOpen => _openMenus > 0;

    public bool IsFloating => _look.Floating != null;

    /// <summary>Bar height in WPF units, kept within sensible limits.</summary>
    public double BarHeight => Math.Clamp(_look.Height, 20, 200);

    /// <summary>Distance of a floating dock from the screen edge, in WPF units.</summary>
    public double DockMargin => IsFloating ? Math.Clamp(_look.Floating!.Margin, 0, 100) : 0;

    /// <summary>
    /// The host (AppBarWindow) calls this when Windows draws a blur behind the bar.
    /// The bar's own background then stays almost invisible so the blur shows through.
    /// </summary>
    public void SetBlurActive(bool active)
    {
        // Alpha 1 instead of 0: fully transparent pixels would let clicks fall through the empty parts of the bar.
        Resources["BarBackground"] = new SolidColorBrush(active ? Color.FromArgb(1, 0, 0, 0) : BackgroundColor);
    }

    /// <summary>Builds the contents once the view is in a window (the window list needs the window).</summary>
    private void Build()
    {
        if (_built) return;
        _built = true;

        if (_design.AllModules.Any(m => m.Type == ModuleType.WindowList) && Window.GetWindow(this) is { } host)
            _tracker = new WindowTracker(host, WindowFilter);

        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Root.Child = BuildLayout();
        _secondTimer.Start();
    }

    private void TearDown()
    {
        _secondTimer.Stop();
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _tracker?.Dispose();
        _tracker = null;
    }

    // ---------- Look ----------

    private Color BackgroundColor => ParseColor(_look.Background, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C));

    /// <summary>Applies the design's colors, font, sizes and corners.</summary>
    private void ApplyLook()
    {
        var foreground = ParseColor(_look.Foreground, Colors.White);

        Resources["BarBackground"] = new SolidColorBrush(BackgroundColor);
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
    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(text.Trim()); }
        catch (FormatException) { return fallback; }
    }

    /// <summary>True if <paramref name="text"/> is empty or a color <see cref="ParseColor"/> understands.</summary>
    public static bool IsValidColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        try { ColorConverter.ConvertFromString(text.Trim()); return true; }
        catch (FormatException) { return false; }
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
            Placement = Edge == TaskbarEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
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
        menu.Opened += (_, _) => _openMenus++;
        menu.Closed += (_, _) => _openMenus = Math.Max(0, _openMenus - 1);
        menu.IsOpen = true;
    }
}
