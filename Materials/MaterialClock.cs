using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Advances animated materials at a frame-rate limit (default 30 per second). Pauses while a fullscreen
/// app or game is in front (the GPU is theirs then) and while no animated material is on screen; runs
/// not at all when no material is animated.
/// </summary>
internal static class MaterialClock
{
    private static readonly List<(WeakReference<MaterialEffect> Effect, WeakReference<FrameworkElement> Surface)> Effects = new();
    private static readonly Stopwatch Elapsed = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private static int _fps = 30;

    /// <summary>Frames per second for animated materials (5–60).</summary>
    public static int FramesPerSecond
    {
        get => _fps;
        set
        {
            _fps = Math.Clamp(value, 5, 60);
            if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(1.0 / _fps);
        }
    }

    /// <summary>Animates this effect while its surface is on screen.</summary>
    public static void Add(MaterialEffect effect, FrameworkElement surface)
    {
        if (!effect.Animated) return;
        Effects.Add((new WeakReference<MaterialEffect>(effect), new WeakReference<FrameworkElement>(surface)));
        effect.Seconds = Elapsed.Elapsed.TotalSeconds;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / _fps) };
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
    }

    private static void Tick()
    {
        if (ForegroundIsFullscreen()) return; // a game or a video: leave the GPU to it
        double now = Elapsed.Elapsed.TotalSeconds;
        for (int i = Effects.Count - 1; i >= 0; i--)
        {
            if (!Effects[i].Effect.TryGetTarget(out var effect) || !Effects[i].Surface.TryGetTarget(out var surface))
            {
                Effects.RemoveAt(i); // the bar was rebuilt or closed
                continue;
            }
            // Only redraw what's actually shown, and only while it still has this effect.
            if (surface.IsVisible && ReferenceEquals(surface.Effect, effect)) effect.Seconds = now;
            else if (!ReferenceEquals(surface.Effect, effect)) Effects.RemoveAt(i);
        }
        if (Effects.Count == 0) _timer?.Stop();
    }

    /// <summary>True when the window in front covers its whole monitor (and isn't the desktop).</summary>
    private static bool ForegroundIsFullscreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        var center = new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        if (!GetMonitorInfo(MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var m = info.rcMonitor;
        if (rect.Left > m.Left || rect.Top > m.Top || rect.Right < m.Right || rect.Bottom < m.Bottom) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid != (uint)Environment.ProcessId; // our own desktop (as the shell) is fullscreen too, but not a game
    }
}
