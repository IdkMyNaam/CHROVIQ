using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// "Force close": ends the program a window belongs to right away, together with the processes it started
/// (a browser's helpers, say), like Task Manager's "End task" but for the whole program. Unsaved work is lost.
///
/// Safety rails: File Explorer windows are only asked to close (ending explorer.exe would take the desktop and the
/// Windows taskbar down), CHROVIQ never ends itself, and Store apps end their own process, not the shared frame
/// host that all Store app windows live in.
/// </summary>
internal static class ForceClose
{
    /// <summary>Ends the window's program. Returns null when it worked, or why it didn't.</summary>
    public static string? End(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "That window is already gone.";

        // A Store app's window is a frame from ApplicationFrameHost; the app itself is the process of the
        // "CoreWindow" inside it.
        if (ProcessName(pid) is "applicationframehost" && StoreAppProcess(hwnd) is uint app && app != 0) pid = app;

        string name = ProcessName(pid) ?? "";
        if (pid == (uint)Environment.ProcessId) return "CHROVIQ doesn't force-close itself; use Exit in its menu.";
        if (name is "explorer" or "applicationframehost" or "dwm" or "csrss" or "winlogon" or "sihost" or "shellexperiencehost"
            or "startmenuexperiencehost" or "searchhost" or "textinputhost" or "lockapp")
        {
            // Part of Windows itself: only ask the window to close.
            WindowTracker.Close(hwnd);
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill(entireProcessTree: true);
            AppTrace.Log($"Force close: ended {name} ({pid}) and the processes it started");
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"\"{name}\" runs as administrator, so it can only be force-closed from Task Manager running as administrator.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // it ended by itself meanwhile
        }
    }

    private static string? ProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant(); }
        catch (Exception) { return null; }
    }

    private static uint? StoreAppProcess(IntPtr frame)
    {
        uint found = 0;
        EnumChildWindows(frame, (child, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != "Windows.UI.Core.CoreWindow") return true;
            GetWindowThreadProcessId(child, out found);
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr data);
}
