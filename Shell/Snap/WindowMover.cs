using System.Runtime.InteropServices;
using System.Windows;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>Puts a window exactly into a zone of a monitor's work area (physical pixels), the way Windows' snap does.</summary>
internal static class WindowMover
{
    /// <summary>A monitor's work area in physical pixels, and its device name.</summary>
    public static (RECT Work, string? Device) WorkAreaOf(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var ex = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        string? device = GetMonitorInfoEx(monitor, ref ex) ? ex.szDevice : null;
        return (info.rcWork, device);
    }

    /// <summary>The zone (0-1 parts) on that work area, in physical pixels, with the gap around it.</summary>
    public static RECT ZoneRect(RECT work, Rect zone, double gap, double scale)
    {
        int g = (int)Math.Round(gap * scale);
        int w = work.Right - work.Left, h = work.Bottom - work.Top;
        int left = work.Left + (int)Math.Round(zone.Left * w), top = work.Top + (int)Math.Round(zone.Top * h);
        int right = work.Left + (int)Math.Round(zone.Right * w), bottom = work.Top + (int)Math.Round(zone.Bottom * h);
        // A full gap at the screen edges, half a gap on each side between zones (so the space between is one gap).
        left += zone.Left <= 0.001 ? g : g / 2;
        top += zone.Top <= 0.001 ? g : g / 2;
        right -= zone.Right >= 0.999 ? g : g / 2;
        bottom -= zone.Bottom >= 0.999 ? g : g / 2;
        return new RECT { Left = left, Top = top, Right = Math.Max(left + 50, right), Bottom = Math.Max(top + 50, bottom) };
    }

    /// <summary>
    /// Moves the window so what you see of it fills <paramref name="target"/>. Windows have invisible borders
    /// (for resizing) around what's drawn; those are added so the visible edges line up with the zone.
    /// </summary>
    public static void MoveTo(IntPtr hwnd, RECT target)
    {
        if (hwnd == IntPtr.Zero) return;
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref placement) && placement.showCmd != 1 /* SW_SHOWNORMAL */)
            ShowWindow(hwnd, SW_RESTORE); // maximized (Windows' own snap to the top) or minimized: normal first

        // The invisible border: the difference between the window's rectangle and what DWM draws of it.
        int left = 0, top = 0, right = 0, bottom = 0;
        if (GetWindowRect(hwnd, out var outer) && DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visible, Marshal.SizeOf<RECT>()) == 0)
        {
            left = visible.Left - outer.Left;
            top = visible.Top - outer.Top;
            right = outer.Right - visible.Right;
            bottom = outer.Bottom - visible.Bottom;
        }
        SetWindowPos(hwnd, IntPtr.Zero, target.Left - left, target.Top - top,
            target.Right - target.Left + left + right, target.Bottom - target.Top + top + bottom, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>The window a hotkey or drag is about: a normal top-level window (not the desktop, a bar, or ours).</summary>
    public static bool IsMovable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        return (style & WS_THICKFRAME) != 0; // resizable windows only (dialogs keep their size)
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004;

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
