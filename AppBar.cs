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

/// <summary>
/// Registers a window as an "application desktop toolbar" (SHAppBarMessage), so Windows reserves
/// a strip of the primary monitor for it and maximized windows don't cover it.
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
    private bool _registered;

    /// <summary>True when a fullscreen app (game, video) starts, false when it ends.</summary>
    public event Action<bool>? FullscreenChanged;

    public AppBar(Window window, TaskbarEdge edge, double thicknessDip)
    {
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

        // Win32 works in physical pixels; convert our size from WPF's device-independent units.
        uint dpi = GetDpiForWindow(_hwnd);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int thickness = (int)Math.Round(_thicknessDip * scale);

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY), ref monitorInfo);

        var data = NewData();
        data.rc = monitorInfo.rcMonitor;
        ApplyThickness(ref data.rc, thickness);

        // Windows may move the rectangle, for example below another bar on the same edge.
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        ApplyThickness(ref data.rc, thickness);
        SHAppBarMessage(ABM_SETPOS, ref data);

        var rc = data.rc;
        SetWindowPos(_hwnd, HWND_TOPMOST, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, SWP_NOACTIVATE);
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
                    FullscreenChanged?.Invoke(lParam != IntPtr.Zero);
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
