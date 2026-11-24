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
internal sealed class AppBar : IDisposable
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
    private readonly double _reserveDip;
    private readonly string _monitorDevice;
    private readonly PlaceWindow _place;
    private bool _registered;

    /// <summary>
    /// Moves the bar's window. <paramref name="strip"/> is the reserved strip at the edge
    /// (zero high when nothing is reserved), in physical pixels; <paramref name="scale"/> is the monitor's scaling.
    /// </summary>
    public delegate void PlaceWindow(RECT strip, double scale);

    /// <summary>True when a fullscreen app (game, video) covers this bar's monitor, false when that ends.</summary>
    public event Action<bool>? FullscreenChanged;

    /// <param name="monitorDevice">Device name of the monitor to dock on, like "\\.\DISPLAY1".</param>
    /// <param name="reserveDip">How much screen to reserve at the edge (WPF units); 0 reserves nothing.</param>
    /// <param name="place">Positions the window inside (or at) the reserved strip.</param>
    public AppBar(Window window, string monitorDevice, TaskbarEdge edge, double reserveDip, PlaceWindow place)
    {
        _monitorDevice = monitorDevice;
        _edge = edge;
        _reserveDip = reserveDip;
        _place = place;
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

    /// <summary>Asks Windows for a free strip at our edge, reserves it, and lets the owner place the window.</summary>
    public void UpdatePosition()
    {
        if (!_registered) return;

        // The monitor may be gone (unplugged); the owner rebuilds the bars then.
        if (Monitors.Find(_monitorDevice) is not { } found) return;
        var (monitor, info) = found;

        // Win32 works in physical pixels; convert our size from WPF's device-independent units.
        // Use the target monitor's scaling, not the window's: the window may still be on another monitor.
        double scale = Monitors.ScaleOf(monitor);
        int thickness = (int)Math.Round(_reserveDip * scale);

        var data = NewData();
        data.rc = info.Bounds;
        ApplyThickness(ref data.rc, thickness);

        // Windows may move the rectangle, for example below another bar on the same edge.
        // With nothing reserved (thickness 0) we still stay registered, to hear about fullscreen apps.
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        ApplyThickness(ref data.rc, thickness);
        SHAppBarMessage(ABM_SETPOS, ref data);

        _place(data.rc, scale);
    }

    /// <summary>Moves the window to a rectangle in physical pixels, staying on top.</summary>
    public static void MoveWindow(Window window, RECT rc) =>
        SetWindowPos(new WindowInteropHelper(window).Handle, HWND_TOPMOST,
            rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, SWP_NOACTIVATE);

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
            // "The taskbar was (re)created": after an Explorer restart, or when the Tray module starts/stops
            // (ManagedShell announces it so programs re-add their tray icons). Unregister first: if we are
            // still registered (no Explorer restart), registering again would be refused.
            var data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registered = false;
            // Never throw from a window procedure.
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
