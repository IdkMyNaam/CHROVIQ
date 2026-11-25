using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Snap layouts: while you drag a window, drag it to the top of a screen for the layout picker, or hold Shift to see
/// the zones of that screen's layout; let go over a zone and the window fills it. Win+Alt+Left/Right moves the
/// active window to the previous/next zone (on to the next screen at the end); Win+Alt+Z opens the picker for it.
/// </summary>
internal sealed class SnapEngine : IDisposable
{
    private readonly SnapSettings _settings;
    private readonly Window _hotkeyWindow;
    private readonly WinEventProc _moveSizeCallback; // kept in a field so the GC doesn't collect it
    private IntPtr _hook;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };

    private IntPtr _dragged;
    private ZonesOverlay? _overlay;
    private LayoutPicker? _picker;
    private RECT? _target; // where the dragged window goes when it's let go


    public SnapEngine(SnapSettings settings, Window hotkeyWindow)
    {
        _settings = settings;
        _hotkeyWindow = hotkeyWindow;
        _moveSizeCallback = OnMoveSize;
        _poll.Tick += (_, _) => WhileDragging();
    }

    /// <summary>Starts or stops to match the settings.</summary>
    public void Update()
    {
        Stop();
        if (!_settings.Enabled) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _moveSizeCallback, 0, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // The hotkeys (Win+Alt+Left/Right/Z by default) are registered with all the others (Hotkeys.cs).
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        EndDrag();
    }

    public void Dispose() => Stop();

    // ---------- Dragging ----------

    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;

    private void OnMoveSize(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW) return;
        if (evt == EVENT_SYSTEM_MOVESIZESTART)
        {
            if (!WindowMover.IsMovable(hwnd)) return;
            _dragged = hwnd;
            _target = null;
            _poll.Start();
        }
        else if (evt == EVENT_SYSTEM_MOVESIZEEND && hwnd == _dragged)
        {
            var target = _target;
            EndDrag();
            // A moment later, so Windows' own snap (maximize at the top) is done first; then it's undone by ours.
            if (target is { } rect)
                _poll.Dispatcher.BeginInvoke(() => WindowMover.MoveTo(hwnd, rect), DispatcherPriority.Background);
        }
    }

    private void EndDrag()
    {
        _poll.Stop();
        _dragged = IntPtr.Zero;
        _target = null;
        _overlay?.Hide();
        _picker?.Close();
        _picker = null;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static bool ShiftDown => (GetAsyncKeyState(0x10) & 0x8000) != 0;

    /// <summary>Every 30 ms during a drag: which overlay to show, and where the window would go.</summary>
    private void WhileDragging()
    {
        if (_dragged == IntPtr.Zero || !GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var (work, device) = WindowMover.WorkAreaOf(monitor);
        double scale = Monitors.ScaleOf(monitor);

        // Shift held: the zones of this screen's layout, the one under the mouse is where it goes.
        if (_settings.ZonesOnShiftDrag && ShiftDown)
        {
            ClosePicker();
            var zones = _settings.LayoutFor(device).Zones().Select(z => WindowMover.ZoneRect(work, z, _settings.Gap, scale)).ToList();
            int under = zones.FindIndex(z => cursor.X >= z.Left && cursor.X < z.Right && cursor.Y >= z.Top && cursor.Y < z.Bottom);
            (_overlay ??= new ZonesOverlay()).ShowZones(info.rcMonitor, scale, zones, under);
            _target = under >= 0 ? zones[under] : null;
            return;
        }

        // At the top of the screen (or still over the picker): the picker; the zone under the mouse shows full size.
        if (_settings.PickerOnDragToTop)
        {
            bool atTop = cursor.Y <= info.rcMonitor.Top + 12 * scale;
            if (_picker == null && atTop)
            {
                _picker = new LayoutPicker(_settings.AllLayouts(), clickable: false);
                _picker.ShowOn(info.rcMonitor, scale);
            }
            if (_picker != null)
            {
                var area = _picker.ScreenRect;
                int slack = (int)(40 * scale);
                bool near = cursor.X >= area.Left - slack && cursor.X < area.Right + slack && cursor.Y < area.Bottom + slack;
                if (!near && !atTop) { ClosePicker(); _overlay?.Hide(); _target = null; return; }
                if (_picker.ZoneAt(cursor) is { } pick)
                {
                    var rect = WindowMover.ZoneRect(work, pick.Layout.Zones()[pick.Index], _settings.Gap, scale);
                    (_overlay ??= new ZonesOverlay()).ShowZones(info.rcMonitor, scale, new[] { rect }, 0);
                    _target = rect;
                }
                else { _overlay?.Hide(); _target = null; }
                return;
            }
        }
        _overlay?.Hide();
        _target = null;
    }

    private void ClosePicker()
    {
        _picker?.Close();
        _picker = null;
    }

    // ---------- Hotkeys ----------

    /// <summary>Win+Alt+Left/Right: the active window to the previous/next zone; past the last one, on to the next screen.</summary>
    public void Step(int direction)
    {
        var hwnd = GetForegroundWindow();
        if (!WindowMover.IsMovable(hwnd)) return;
        var screens = Screens();
        var here = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        int s = Math.Max(0, screens.FindIndex(m => m.Handle == here));
        var zones = ZonesOn(screens[s]);
        GetWindowRect(hwnd, out var now);
        int cx = (now.Left + now.Right) / 2, cy = (now.Top + now.Bottom) / 2;
        int current = zones.FindIndex(z => Math.Abs(z.Left - now.Left) < 24 && Math.Abs(z.Right - now.Right) < 24
                                           && Math.Abs(z.Top - now.Top) < 24 && Math.Abs(z.Bottom - now.Bottom) < 24);
        if (current < 0)
        {
            // Not in a zone yet: into the zone it's mostly in.
            int inside = zones.FindIndex(z => cx >= z.Left && cx < z.Right && cy >= z.Top && cy < z.Bottom);
            WindowMover.MoveTo(hwnd, zones[Math.Max(0, inside)]);
            return;
        }
        int next = current + direction;
        if (next >= 0 && next < zones.Count) { WindowMover.MoveTo(hwnd, zones[next]); return; }
        // Past the end: the first (or last) zone of the next (or previous) screen.
        int other = s + direction;
        if (other < 0 || other >= screens.Count) return;
        var otherZones = ZonesOn(screens[other]);
        WindowMover.MoveTo(hwnd, direction > 0 ? otherZones[0] : otherZones[^1]);
    }

    /// <summary>Win+Alt+Z: the picker on the active window's screen; click a zone to put the window there.</summary>
    public void PickForActiveWindow()
    {
        var hwnd = GetForegroundWindow();
        if (!WindowMover.IsMovable(hwnd)) return;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var (work, _) = WindowMover.WorkAreaOf(monitor);
        double scale = Monitors.ScaleOf(monitor);
        var picker = new LayoutPicker(_settings.AllLayouts(), clickable: true);
        picker.Picked += (layout, index) =>
        {
            WindowMover.MoveTo(hwnd, WindowMover.ZoneRect(work, layout.Zones()[index], _settings.Gap, scale));
            SetForegroundWindow(hwnd);
        };
        picker.ShowOn(info.rcMonitor, scale);
    }

    private sealed record Screen(IntPtr Handle, RECT Work, string? Device, double Scale);

    /// <summary>The monitors, left to right.</summary>
    private static List<Screen> Screens()
    {
        var list = new List<Screen>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var (work, device) = WindowMover.WorkAreaOf(monitor);
            list.Add(new Screen(monitor, work, device, Monitors.ScaleOf(monitor)));
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(m => m.Work.Left).ThenBy(m => m.Work.Top).ToList();
    }

    private List<RECT> ZonesOn(Screen screen) =>
        _settings.LayoutFor(screen.Device).Zones().Select(z => WindowMover.ZoneRect(screen.Work, z, _settings.Gap, screen.Scale)).ToList();
}
