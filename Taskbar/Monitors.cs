using System.Runtime.InteropServices;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>A connected monitor, in physical pixels.</summary>
/// <param name="Device">Device name like \\.\DISPLAY2; identifies the monitor while it stays connected.</param>
internal record MonitorInfo(string Device, RECT Bounds, bool IsPrimary);

/// <summary>Finds monitors and which monitor a window is on.</summary>
internal static class Monitors
{
    private const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>All connected monitors, the primary one first.</summary>
    public static List<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (Describe(monitor) is { } info) result.Add(info);
            return true;
        }, IntPtr.Zero);
        return result.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
    }

    /// <summary>Looks a monitor up again by device name (handles can change when displays change).</summary>
    public static (IntPtr handle, MonitorInfo info)? Find(string device)
    {
        (IntPtr, MonitorInfo)? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (Describe(monitor) is { } info && info.Device == device)
            {
                found = (monitor, info);
                return false; // stop
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The monitor a window is on. For minimized windows this is where they will be restored,
    /// like the Windows taskbar does it (their current position is off-screen).
    /// </summary>
    public static string? DeviceOf(IntPtr hwnd)
    {
        IntPtr monitor;
        if (IsIconic(hwnd))
        {
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref placement);
            var normal = placement.rcNormalPosition;
            monitor = MonitorFromRect(ref normal, MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        }
        return Describe(monitor)?.Device;
    }

    /// <summary>Scale factor of a monitor (1.0 = 100%, 1.5 = 150%).</summary>
    public static double ScaleOf(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;

    /// <summary>Changes whenever a monitor is added, removed, moved or resized.</summary>
    public static string Layout() =>
        string.Join(";", All().Select(m => $"{m.Device}:{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Right},{m.Bounds.Bottom}"));

    private static MonitorInfo? Describe(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoEx(monitor, ref info)) return null;
        return new MonitorInfo(info.szDevice, info.rcMonitor, (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }
}
