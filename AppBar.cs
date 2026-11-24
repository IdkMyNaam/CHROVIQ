using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

public enum TaskbarEdge
{
    Top,
    Bottom,
}

/// <summary>Where window buttons appear when there is a bar on each monitor (like the Windows setting).</summary>
public enum TaskbarButtons
{
    /// <summary>Every bar shows every window.</summary>
    AllBars,
    /// <summary>Each bar shows only the windows on its own monitor.</summary>
    OwnMonitor,
}

/// <summary>
/// Registers a window as an "application desktop toolbar" (SHAppBarMessage), so Windows reserves
/// a strip of one monitor for it and maximized windows don't cover it.
/// </summary>
public sealed class AppBar : IDisposable
{
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly TaskbarEdge _edge;
    private readonly double _thicknessDip;
    private readonly string _monitorDevice;
    private bool _registered;

    /// <summary>True when a fullscreen app (game, video) covers this bar's monitor, false when that ends.</summary>
    public event Action<bool>? FullscreenChanged;

    /// <param name="monitorDevice">Device name of the monitor to dock on, like "\\.\DISPLAY1".</param>
    public AppBar(Window window, string monitorDevice, TaskbarEdge edge, double thicknessDip)
    {
        _monitorDevice = monitorDevice;
        _edge = edge;
        _thicknessDip = thicknessDip;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        _callbackMessage = RegisterWindowMessage("WindowsCustomizer.AppBarMessage");
        // Sent to all top-level windows when Explorer (re)starts; Explorer forgets all app bars then.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        Register();
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
        uEdge = _edge == TaskbarEdge.Top ? ABE_TOP : ABE_BOTTOM,
    };

    private void Register()
    {
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
        if (!_registered)
            throw new InvalidOperationException("Windows did not accept the custom taskbar as an app bar.");
        UpdatePosition();
    }

    /// <summary>Asks Windows for a free strip at our edge, reserves it, and moves the window there.</summary>
    public void UpdatePosition()
    {
        if (!_registered) return;

        // The monitor may be gone (unplugged); the owner rebuilds the bars then.
        if (Monitors.Find(_monitorDevice) is not { } found) return;
        var (monitor, info) = found;

        // Win32 works in physical pixels; convert our size from WPF's device-independent units.
        // Use the target monitor's scaling, not the window's: the window may still be on another monitor.
        int thickness = (int)Math.Round(_thicknessDip * Monitors.ScaleOf(monitor));

        var data = NewData();
        data.rc = info.Bounds;
        ApplyThickness(ref data.rc, thickness);

        // Windows may move the rectangle, for example below another bar on the same edge.
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        ApplyThickness(ref data.rc, thickness);
        SHAppBarMessage(ABM_SETPOS, ref data);

        var rc = data.rc;
        SetWindowPos(_hwnd, HWND_TOPMOST, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, SWP_NOACTIVATE);
    }

    /// <summary>True if the foreground window covers this bar's whole monitor (a game or fullscreen video).</summary>
    private bool IsFullscreenOnOurMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || Monitors.DeviceOf(foreground) != _monitorDevice) return false;
        if (Monitors.Find(_monitorDevice) is not { } found) return false;
        var info = found.info;
        if (!GetWindowRect(foreground, out var rc)) return false;

        var m = info.Bounds;
        return rc.Left <= m.Left && rc.Top <= m.Top && rc.Right >= m.Right && rc.Bottom >= m.Bottom;
    }

    private void ApplyThickness(ref RECT rc, int thickness)
    {
        if (_edge == TaskbarEdge.Top) rc.Bottom = rc.Top + thickness;
        else rc.Top = rc.Bottom - thickness;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    UpdatePosition(); // another bar or the Windows taskbar moved
                    break;
                case ABN_FULLSCREENAPP:
                    // Windows doesn't say which monitor this is about, so check our own monitor.
                    FullscreenChanged?.Invoke(lParam != IntPtr.Zero && IsFullscreenOnOurMonitor());
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: register again. Never throw from a window procedure.
            try { Register(); }
            catch (InvalidOperationException) { _registered = false; }
        }
        else if (msg == WM_ACTIVATE && _registered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_ACTIVATE, ref data);
        }
        else if (msg == WM_WINDOWPOSCHANGED && _registered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
        }
        else if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            // Let WPF handle the message first, then re-fit to the new resolution or scaling.
            _source.Dispatcher.InvokeAsync(UpdatePosition, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data); // give the reserved space back
            _registered = false;
        }
        _source.RemoveHook(WndProc);
    }
}
