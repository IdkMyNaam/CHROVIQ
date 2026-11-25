using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    /// <summary>True while one of the bar's menus is open or the search box is in use (auto-hide waits for it).</summary>
    public bool IsMenuOpen => _openMenus > 0 || _searchActive;

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
        // With a material, keep the (see-through) background: the material is drawn in it, over the blur.
        Resources["BarBackground"] = new SolidColorBrush(active && MaterialLayer.Effect == null ? Color.FromArgb(1, 0, 0, 0) : BackgroundColor);
    }

    /// <summary>Why the design's material can't be shown (it doesn't compile), or null.</summary>
    public string? MaterialError { get; private set; }

    /// <summary>The design's material on the background layer (or none); animated ones join the material clock.</summary>
    private void ApplyMaterial() =>
        MaterialError = MaterialHost.Attach(MaterialLayer, Surface, _look.Material, _look.MaterialValues,
                                            ParseColor(_look.Accent, SystemParameters.WindowGlassColor));

    /// <summary>The window list's buttons (windows or app groups), built from the tracked windows.</summary>
    private TaskItemList? _items;

    /// <summary>Programs shown by dock-style pinned apps; their windows get no second button.</summary>
    private readonly HashSet<string> _pinnedPrograms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the contents once the view is in a window (the window list needs the window).</summary>
    private void Build()
    {
        if (_built) return;
        _built = true;

        // Dock-style pinned apps need to know which programs are running, like the window list does.
        foreach (var module in _design.AllModules.Where(m => m.Type == ModuleType.PinnedApps && m.ShowRunning != false))
            foreach (var app in module.Apps ?? new List<string>())
                if (PinnedTargets.ProgramOf(app) is { } program)
                    _pinnedPrograms.Add(program);

        bool needsWindows = _design.AllModules.Any(m => m.Type == ModuleType.WindowList) || _pinnedPrograms.Count > 0;
        if (needsWindows && Window.GetWindow(this) is { } host)
        {
            _tracker = new WindowTracker(host, WindowFilter);
            _items = new TaskItemList(_tracker.Windows, _look.GroupWindows,
                w => w.ProcessPath == null || !_pinnedPrograms.Contains(w.ProcessPath));
            _items.Items.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null) foreach (TaskItem item in e.NewItems) Decorate(item);
            };
            foreach (var item in _items.Items) Decorate(item);
        }

        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Root.Child = BuildLayout();
        if (EverySecond != null) _secondTimer.Start(); // only when a module needs it (volume, CPU/memory)
    }

    /// <summary>A timer for one module, stopped when the bar is rebuilt or closed.</summary>
    private DispatcherTimer ModuleTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        _disposables.Add(new StopOnDispose(timer));
        timer.Start();
        return timer;
    }

    private sealed class StopOnDispose(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private void TearDown()
    {
        _secondTimer.Stop();
        _thumbnailTimer.Stop();
        _thumbnail?.Close();
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _items?.Detach();
        _tracker?.Dispose();
        _tracker = null;
        foreach (var disposable in _disposables) disposable.Dispose();
        _disposables.Clear();
    }

    // ---------- Look ----------

    private Color BackgroundColor => ParseColor(_look.Background, Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C));

    /// <summary>Color of tinted icons (Look.IconTint, or the text color).</summary>
    private Color TintColor { get; set; } = Colors.White;

    /// <summary>Applies the design's colors, font, sizes and corners.</summary>
    private void ApplyLook()
    {
        var foreground = ParseColor(_look.Foreground, Colors.White);

        Resources["BarBackground"] = new SolidColorBrush(BackgroundColor);
        Resources["BarForeground"] = new SolidColorBrush(foreground);

        // Hover/active highlights are the text color, faintly: works on dark and light bars.
        Resources["BarHover"] = new SolidColorBrush(Color.FromArgb(0x26, foreground.R, foreground.G, foreground.B));
        Resources["BarActive"] = new SolidColorBrush(Color.FromArgb(0x33, foreground.R, foreground.G, foreground.B));
        Resources["ButtonCornerRadius"] = new CornerRadius(Math.Max(0, _look.ButtonCornerRadius));

        // Instant or fading hover highlight for every button on the bar.
        Resources["BarButton"] = Resources[_look.HoverFade ? "BarButtonFade" : "BarButtonInstant"];

        // Tinted icons: a one-color copy over a hidden full-color icon (shown on hover).
        TintColor = ParseColor(_look.IconTint, foreground);
        Resources["IconTint"] = new SolidColorBrush(TintColor);
        Resources["TintIconVisibility"] = _look.TintIcons ? Visibility.Visible : Visibility.Collapsed;
        Resources["ColorIconOpacity"] = _look.TintIcons ? 0.0 : 1.0;

        Surface.CornerRadius = new CornerRadius(Math.Max(0, _look.CornerRadius));
        MaterialLayer.CornerRadius = Surface.CornerRadius;
        ApplyMaterial();
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

        // Buttons without glow use the accent for their line: update them.
        if (_items != null)
            foreach (var item in _items.Items) Decorate(item);
    }

    // ---------- Window list ----------

    /// <summary>
    /// Sets a button's colors: with glow, from its app icon's main color (when the icon has one);
    /// otherwise the design's hover and active colors and the accent line.
    /// </summary>
    private void Decorate(TaskItem item)
    {
        if (_look.IconGlow && IconColors.MainColor(item.Icon) is { } c)
        {
            item.HoverBrush = Frozen(Color.FromArgb(0x40, c.R, c.G, c.B));
            item.ActiveBrush = Frozen(Color.FromArgb(0x55, c.R, c.G, c.B));
            item.LineBrush = Frozen(c);
        }
        else
        {
            item.HoverBrush = (Brush)Resources["BarHover"];
            item.ActiveBrush = (Brush)Resources["BarActive"];
            item.LineBrush = (Brush)Resources["AccentBrush"];
        }

        if (_look.TintIcons)
            item.TintedIcon = IconColors.Tinted(item.Icon, TintColor);

        // Icons can arrive a moment after the window opens: decorate again then.
        if ((_look.IconGlow || _look.TintIcons) && !_decorated.Contains(item))
        {
            _decorated.Add(item);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TaskItem.Icon)) Decorate(item); };
        }
    }

    private readonly HashSet<TaskItem> _decorated = new();

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The windows behind a button: one window, an app group, or a running pinned app.</summary>
    private static List<TaskWindow> WindowsOf(object? item) => item switch
    {
        TaskWindow w => new List<TaskWindow> { w },
        TaskGroup g => g.Windows.ToList(),
        _ => new List<TaskWindow>(),
    };

    private void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button)
            ActivateWindows(WindowsOf(button.DataContext), button);
    }

    /// <summary>
    /// One window: bring it to the front, or minimize it when it already is in front.
    /// Several windows (an app group): a menu to pick one.
    /// </summary>
    private void ActivateWindows(List<TaskWindow> windows, FrameworkElement anchor)
    {
        CloseThumbnails();
        if (windows.Count == 1)
        {
            WindowTracker.ActivateOrMinimize(windows[0].Handle);
            _tracker?.Refresh(); // show the new active window right away
            return;
        }
        if (windows.Count == 0) return;

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = Edge == TaskbarEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
        };
        foreach (var window in windows)
        {
            var item = new MenuItem
            {
                Header = window.Title,
                Icon = new Image { Source = window.Icon, Width = 16, Height = 16 },
                FontWeight = window.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var handle = window.Handle;
            item.Click += (_, _) => { WindowTracker.ActivateOrMinimize(handle); _tracker?.Refresh(); };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => _openMenus++;
        menu.Closed += (_, _) => _openMenus = Math.Max(0, _openMenus - 1);
        menu.IsOpen = true;
    }

    /// <summary>Middle-click closes the window (when the design allows it). Not for app groups: that would close several.</summary>
    private void TaskButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_look.MiddleClickCloses) return;
        if ((sender as FrameworkElement)?.DataContext is { } item && WindowsOf(item) is { Count: 1 } windows)
        {
            CloseThumbnails();
            WindowTracker.Close(windows[0].Handle);
            e.Handled = true;
        }
    }

    /// <summary>Right-click "Close": closes the window, or every window of an app group (each may still ask to save).</summary>
    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        foreach (var window in WindowsOf((sender as FrameworkElement)?.DataContext))
            WindowTracker.Close(window.Handle);
    }

    // ---------- Thumbnails ----------

    private readonly DispatcherTimer _thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _thumbnailCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FrameworkElement? _hoverButton;
    private ThumbnailPopup? _thumbnail;
    private bool _thumbnailTimersReady;

    private void TaskButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_look.Thumbnails || sender is not FrameworkElement button) return;
        PrepareThumbnailTimers();
        _hoverButton = button;
        _thumbnailCloseTimer.Stop();

        // Already showing a preview: switch right away; otherwise wait a moment (don't flash while passing over).
        if (_thumbnail != null) ShowThumbnails();
        else { _thumbnailTimer.Stop(); _thumbnailTimer.Start(); }
    }

    private void TaskButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender != _hoverButton) return;
        _hoverButton = null;
        _thumbnailTimer.Stop();
        _thumbnailCloseTimer.Stop();
        _thumbnailCloseTimer.Start(); // time to move the mouse onto the preview
    }

    private void PrepareThumbnailTimers()
    {
        if (_thumbnailTimersReady) return;
        _thumbnailTimersReady = true;
        _thumbnailTimer.Tick += (_, _) => { _thumbnailTimer.Stop(); ShowThumbnails(); };
        _thumbnailCloseTimer.Tick += (_, _) =>
        {
            _thumbnailCloseTimer.Stop();
            if (_hoverButton == null && _thumbnail?.IsMouseOver != true) CloseThumbnails();
            else if (_hoverButton == null) _thumbnailCloseTimer.Start(); // still on the preview: check again
        };
    }

    private void ShowThumbnails()
    {
        if (_hoverButton is not { IsMouseOver: true } button || IsMenuOpen) return;
        var windows = WindowsOf(button.DataContext);
        if (windows.Count == 0) return;

        _thumbnail ??= CreateThumbnailPopup();
        _thumbnail.ShowFor(windows, button, Edge);
    }

    private ThumbnailPopup CreateThumbnailPopup()
    {
        var popup = new ThumbnailPopup();
        popup.WindowClicked += handle =>
        {
            CloseThumbnails();
            WindowTracker.ActivateOrMinimize(handle);
            _tracker?.Refresh();
        };
        popup.MouseLeave += (_, _) => { _thumbnailCloseTimer.Stop(); _thumbnailCloseTimer.Start(); };
        return popup;
    }

    private void CloseThumbnails()
    {
        _thumbnailTimer.Stop();
        _thumbnail?.Close();
        _thumbnail = null;
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
