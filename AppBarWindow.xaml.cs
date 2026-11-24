using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The custom taskbar: an app bar on the top or bottom edge of the primary monitor with
/// a button per open window, battery level, Quick Settings and a clock.
/// The Windows taskbar keeps running; this bar only reserves its own strip of screen.
/// </summary>
public partial class AppBarWindow : Window
{
    private const double BarHeight = 40;     // in WPF units; scaled for the display automatically
    private const double MaxButtonWidth = 200;

    private readonly TaskbarEdge _edge;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppBar? _appBar;
    private WindowTracker? _tracker;
    private DateTime _lastBatteryCheck;

    public AppBarWindow(TaskbarEdge edge)
    {
        _edge = edge;
        InitializeComponent();

        // Accent line under the active window; follows accent color changes (including from our profiles).
        UpdateAccentBrush();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

        SourceInitialized += (_, _) => SetUp();
        Closed += (_, _) => TearDown();

        _clockTimer.Tick += (_, _) => UpdateClockAndBattery();
        UpdateClockAndBattery();
        _clockTimer.Start();
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

        _appBar = new AppBar(this, _edge, BarHeight);
        _appBar.FullscreenChanged += fullscreen =>
        {
            // Get out of the way of games and fullscreen videos.
            if (fullscreen) Hide();
            else Show();
        };

        _tracker = new WindowTracker(this);
        _tracker.Windows.CollectionChanged += (_, _) => UpdateButtonWidth();
        WindowButtons.ItemsSource = _tracker.Windows;
        UpdateButtonWidth();
    }

    private void TearDown()
    {
        _clockTimer.Stop();
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

    /// <summary>The buttons share the space equally, but never get wider than <see cref="MaxButtonWidth"/>.</summary>
    private void UpdateButtonWidth()
    {
        int count = _tracker?.Windows.Count ?? 0;
        WindowButtons.MaxWidth = Math.Max(1, count) * MaxButtonWidth;
    }

    private void UpdateClockAndBattery()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("t");
        DateText.Text = now.ToString("d");

        // Battery doesn't change quickly; checking every 30 seconds is plenty.
        if (now - _lastBatteryCheck < TimeSpan.FromSeconds(30)) return;
        _lastBatteryCheck = now;

        var power = Forms.SystemInformation.PowerStatus;
        bool hasBattery = !power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)
                          && power.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;
        BatteryText.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (!hasBattery) return;

        int percent = (int)Math.Round(power.BatteryLifePercent * 100);
        bool charging = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        BatteryText.Text = charging ? $"{percent}% ⚡" : $"{percent}%";
        BatteryText.ToolTip = charging ? $"Battery {percent}%, plugged in" : $"Battery {percent}%";
    }

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

    private void QuickSettings_Click(object sender, RoutedEventArgs e) => PressWinShortcut('A');

    private void Clock_Click(object sender, RoutedEventArgs e) => PressWinShortcut('N');
}
