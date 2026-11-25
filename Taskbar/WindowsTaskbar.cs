using System.Runtime.InteropServices;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Hides and restores the real Windows taskbar (on every monitor).
/// Hiding = switch it to auto-hide (so it no longer reserves screen space) and hide its windows.
/// A watchdog hides it again whenever Explorer shows it (Explorer restart, Start menu, mouse at the edge).
/// If anything goes wrong, Restore-Taskbar.ps1 in the project folder brings it back without the app.
/// </summary>
public static class WindowsTaskbar
{
    private const uint ABM_GETSTATE = 4;
    private const uint ABM_SETSTATE = 10;
    private const int ABS_AUTOHIDE = 1;
    private const int ABS_ALWAYSONTOP = 2; // on its own: "not auto-hide"
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? title);

    private static DispatcherTimer? _watchdog;
    private static bool? _originalAutoHide;

    /// <summary>True while the app keeps the Windows taskbar hidden.</summary>
    public static bool IsHiding { get; private set; }

    /// <summary>The auto-hide setting from before hiding; put back by <see cref="Restore"/>.</summary>
    public static bool? OriginalAutoHide => _originalAutoHide;

    /// <summary>Whether the user has taskbar auto-hide turned on.</summary>
    public static bool IsAutoHide()
    {
        var data = NewData(PrimaryTaskbar());
        return ((long)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// Hides the taskbar and keeps it hidden. Call on the UI thread.
    /// </summary>
    /// <param name="knownOriginalAutoHide">
    /// The auto-hide setting saved from an earlier session. Needed after a crash: by then auto-hide
    /// is already on because of us, so reading it now would give the wrong answer.
    /// </param>
    public static void Hide(bool? knownOriginalAutoHide)
    {
        if (PrimaryTaskbar() == IntPtr.Zero)
            throw new InvalidOperationException("The Windows taskbar was not found (is Explorer running?).");

        if (!IsHiding)
            _originalAutoHide = knownOriginalAutoHide ?? IsAutoHide();

        IsHiding = true;
        EnforceHidden();

        // Hide it again whenever Explorer shows it: right away through a window event, plus a check every
        // 3 seconds as a safety net (also catches an Explorer restart, which needs the event hooked again).
        if (_watchdog == null)
        {
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _watchdog.Tick += (_, _) => { HookExplorerShow(); EnforceHidden(); };
        }
        HookExplorerShow();
        _watchdog.Start();
        Watchdog.Update(); // restores the taskbar even if this app gets killed
    }

    private static WinEventProc? _showCallback; // kept in a field so the GC doesn't collect it
    private static IntPtr _showHook;
    private static uint _hookedExplorerPid;

    /// <summary>Listens for Explorer showing a window; (re)hooks when Explorer's process changed.</summary>
    private static void HookExplorerShow()
    {
        var taskbar = PrimaryTaskbar();
        if (taskbar == IntPtr.Zero) return;
        GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid == _hookedExplorerPid && _showHook != IntPtr.Zero) return;

        UnhookExplorerShow();
        _showCallback ??= (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject != OBJID_WINDOW || !IsHiding) return;
            if (AllTaskbars().Contains(hwnd)) EnforceHidden();
        };
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _showCallback, pid, 0, WINEVENT_OUTOFCONTEXT);
        _hookedExplorerPid = pid;
    }

    private static void UnhookExplorerShow()
    {
        if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
        _showHook = IntPtr.Zero;
        _hookedExplorerPid = 0;
    }

    /// <summary>Shows the taskbar again with the user's original auto-hide setting. Safe to call anytime.</summary>
    public static void Restore()
    {
        _watchdog?.Stop();
        UnhookExplorerShow();
        RestoreNative();
        Watchdog.Update(); // stops the helper when nothing else is taken over
    }

    /// <summary>
    /// Only the Win32 part of <see cref="Restore"/>, so it can run from any thread (crash handlers).
    /// </summary>
    public static void RestoreNative()
    {
        if (!IsHiding) return;
        IsHiding = false;

        try
        {
            SetAutoHide(_originalAutoHide ?? false);
            foreach (var taskbar in AllTaskbars())
                ShowWindow(taskbar, SW_SHOW);
        }
        catch (Exception)
        {
            // Never throw while restoring (this also runs while the app is crashing).
            // Restore-Taskbar.ps1 is the fallback.
        }
    }

    private static void EnforceHidden()
    {
        if (!IsHiding) return;

        var primary = PrimaryTaskbar();
        if (primary == IntPtr.Zero) return; // Explorer restarting; try again next tick

        if (!IsAutoHide()) SetAutoHide(true); // frees the screen space the taskbar reserves
        foreach (var taskbar in AllTaskbars())
            if (IsWindowVisible(taskbar))
                ShowWindow(taskbar, SW_HIDE);
    }

    private static void SetAutoHide(bool on)
    {
        var data = NewData(PrimaryTaskbar());
        data.lParam = new IntPtr(on ? ABS_AUTOHIDE : ABS_ALWAYSONTOP);
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    private static IntPtr PrimaryTaskbar() => ExplorerTaskbarWindow();

    /// <summary>
    /// Explorer's main taskbar window. While the Tray module runs there is a second window with the same
    /// name ("Shell_TrayWnd") in our own process (ManagedShell's); that one is skipped.
    /// </summary>
    public static IntPtr ExplorerTaskbarWindow() => FindExplorerWindows("Shell_TrayWnd").FirstOrDefault();

    /// <summary>The main taskbar plus the ones on other monitors.</summary>
    private static List<IntPtr> AllTaskbars()
    {
        var result = new List<IntPtr>();
        var primary = PrimaryTaskbar();
        if (primary != IntPtr.Zero) result.Add(primary);
        result.AddRange(FindExplorerWindows("Shell_SecondaryTrayWnd"));
        return result;
    }

    /// <summary>All top-level windows of a class that don't belong to this app.</summary>
    private static List<IntPtr> FindExplorerWindows(string className)
    {
        var result = new List<IntPtr>();
        IntPtr window = IntPtr.Zero;
        while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != (uint)Environment.ProcessId) result.Add(window);
        }
        return result;
    }

    private static APPBARDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
    };
}
