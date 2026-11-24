using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The custom taskbar for one monitor: an app bar on the top or bottom edge, filled with the
/// modules of a <see cref="TaskbarDesign"/> (see AppBarWindow.Modules.cs).
/// The Windows taskbar keeps running; this bar only reserves its own strip of screen.
/// </summary>
public partial class AppBarWindow : Window
{
    private const double BarHeight = 40; // in WPF units; scaled for the display automatically

    private readonly TaskbarEdge _edge;
    private readonly string _monitorDevice;
    private readonly bool _onlyOwnMonitorWindows;
    private readonly TaskbarDesign _design;
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppBar? _appBar;
    private WindowTracker? _tracker;

    /// <summary>Fires once a second, for modules that change over time (clock, battery).</summary>
    private event Action? EverySecond;

    /// <summary>Entries for the Menu module; set by the main window.</summary>
    public Func<IEnumerable<AppMenuItem>>? GetMenuItems { get; set; }

    /// <param name="monitorDevice">The monitor this bar docks on (see <see cref="Monitors"/>).</param>
    /// <param name="onlyOwnMonitorWindows">Show only buttons for windows on this monitor, instead of all windows.</param>
    /// <param name="design">Which modules to show where.</param>
    public AppBarWindow(string monitorDevice, TaskbarEdge edge, bool onlyOwnMonitorWindows, TaskbarDesign design)
    {
        _monitorDevice = monitorDevice;
        _edge = edge;
        _onlyOwnMonitorWindows = onlyOwnMonitorWindows;
        _design = design;
        InitializeComponent();

        // Accent line under the active window; follows accent color changes (including from our profiles).
        UpdateAccentBrush();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

        SourceInitialized += (_, _) => SetUp();
        Closed += (_, _) => TearDown();

        _secondTimer.Tick += (_, _) => EverySecond?.Invoke();
        _secondTimer.Start();
    }

    /// <summary>Runs once the window has a handle, before it is first shown.</summary>
    private void SetUp()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: not listed in Alt+Tab or in our own window list.
        // No-activate: clicking the bar doesn't take focus away from the app you were using,
        // so "click the active window's button to minimize it" works.
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        _appBar = new AppBar(this, _monitorDevice, _edge, BarHeight);
        _appBar.FullscreenChanged += fullscreen =>
        {
            // Get out of the way of games and fullscreen videos on this monitor.
            if (fullscreen) Hide();
            else Show();
        };

        // Only track windows when the design actually shows them.
        if (_design.AllModules.Any(m => m.Type == ModuleType.WindowList))
        {
            _tracker = _onlyOwnMonitorWindows
                ? new WindowTracker(this, w => Monitors.DeviceOf(w) == _monitorDevice)
                : new WindowTracker(this);
        }

        Root.Child = BuildLayout();
    }

    private void TearDown()
    {
        _secondTimer.Stop();
        _tracker?.Dispose();
        _appBar?.Dispose(); // gives the reserved screen space back
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WindowGlassBrush))
            UpdateAccentBrush();
    }

    private void UpdateAccentBrush()
    {
        // WindowGlassBrush is the window border/title bar color Windows derives from the accent.
        var accent = SystemParameters.WindowGlassBrush is System.Windows.Media.SolidColorBrush b
            ? System.Windows.Media.Color.FromRgb(b.Color.R, b.Color.G, b.Color.B) // drop its transparency
            : System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
        Resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(accent);
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
