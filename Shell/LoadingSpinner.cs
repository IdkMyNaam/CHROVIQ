using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// A small spinning ring at the bottom middle of the main monitor while CHROVIQ starts as the shell (the desktop is
/// up, the taskbar is still coming). It runs in its own window on its own thread, so it keeps turning while the app
/// itself is busy building the taskbars. Clicks go through it. It goes away by itself after 20 seconds at most.
/// </summary>
internal sealed class LoadingSpinner
{
    private static LoadingSpinner? _current;
    private Dispatcher? _dispatcher;
    private Window? _window;

    /// <summary>Shows the spinner on the monitor with these bounds (physical pixels).</summary>
    public static void Start(RECT monitor)
    {
        if (_current != null) return;
        var spinner = new LoadingSpinner();
        _current = spinner;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                spinner._dispatcher = Dispatcher.CurrentDispatcher;
                spinner._window = Build(monitor);
                spinner._window.Show();
                // Gone by itself if nobody stops it (the app hung or ended before its taskbar came).
                var limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                limit.Tick += (_, _) => { limit.Stop(); spinner.FadeOut(); };
                limit.Start();
            }
            catch (Exception ex)
            {
                ShellMode.Log($"Loading spinner: {ex.Message}");
                ready.Set();
                return;
            }
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Loading spinner" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(1)); // shown before the app goes on (it's quick)
    }

    /// <summary>Fades the spinner out (the taskbar is up).</summary>
    public static void Stop()
    {
        var spinner = _current;
        _current = null;
        try { spinner?._dispatcher?.BeginInvoke(spinner.FadeOut); }
        catch (Exception) { /* its thread is gone already */ }
    }

    private void FadeOut()
    {
        if (_window == null) { _dispatcher?.InvokeShutdown(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            _window.Close();
            _dispatcher?.InvokeShutdown();
        };
        _window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static Window Build(RECT monitor)
    {
        const double size = 40;
        // Three quarters of a ring, round ends, on a faint full ring; a soft shadow keeps it visible on light wallpapers.
        var track = new System.Windows.Shapes.Ellipse
        {
            Width = 32, Height = 32, Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 3,
        };
        var rotate = new RotateTransform();
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 16,1.5 A 14.5,14.5 0 1 1 1.5,16"),
            Stroke = Brushes.White, StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 32, Height = 32, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotate,
        };
        var ring = new System.Windows.Controls.Grid
        {
            Width = size, Height = size,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 },
        };
        ring.Children.Add(track);
        ring.Children.Add(arc);
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize,
            Width = size, Height = size, Content = ring, Title = "Starting CHROVIQ",
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            // Not in Alt+Tab, never takes the focus, clicks go through.
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | 0x20 /* WS_EX_TRANSPARENT */));
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            int pixels = (int)Math.Round(size * scale);
            int x = (monitor.Left + monitor.Right) / 2 - pixels / 2;
            int y = monitor.Bottom - (int)Math.Round(150 * scale);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, pixels, pixels, SWP_NOACTIVATE);
        };
        return window;
    }
}
