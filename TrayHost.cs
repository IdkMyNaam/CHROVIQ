using System.Runtime.InteropServices;
using ManagedShell.WindowsTray;
using ShellNative = ManagedShell.Interop.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Collects the notification-area (tray) icons of all programs, using the open-source ManagedShell
/// library (only its tray part). Shared by every bar with a Tray module: started by the first, stopped
/// when the last one closes.
///
/// How it works while Explorer runs: ManagedShell creates its own window named like the Windows
/// taskbar ("Shell_TrayWnd") and keeps it in front, so programs send their tray icons to us; it
/// also reads the icons Explorer already has. While it runs, Explorer's own tray may not get new icons.
/// When it stops, it tells all programs to add their icons again, so they go back to Explorer.
///
/// Side effect handled here: Windows also sends every app-bar request (reserving screen space for
/// a taskbar) to that window. ManagedShell passes most of them on to Explorer, but the two that
/// reserve space need their data re-packed for Explorer (see <see cref="OnAppBarMessage"/>).
/// </summary>
internal static class TrayHost
{
    private static NotificationArea? _area;
    private static int _users;

    // Kept in a field: ManagedShell calls it from a window procedure, so it must not be garbage collected.
    private static readonly AppBarMessageDelegate AppBarHandler = OnAppBarMessage;

    /// <summary>Starts the tray (if not yet running) and counts one more user. Null if it couldn't start.</summary>
    public static NotificationArea? Acquire()
    {
        if (_area == null)
        {
            try
            {
                var pins = SavedPins is { Length: > 0 } saved ? saved : NotificationArea.DEFAULT_PINNED;
                var area = new NotificationArea(pins, new TrayService(), new ExplorerTrayService());
                area.SetAppBarMessageCallback(AppBarHandler);
                area.Initialize();
                if (area.IsFailed)
                {
                    area.Dispose();
                    return null;
                }
                _area = area;
                Watchdog.Update(); // icons back to Explorer even if this app gets killed
            }
            catch (Exception)
            {
                return null;
            }
        }
        _users++;
        return _area;
    }

    /// <summary>One user fewer; the last one stops the tray.</summary>
    public static void Release()
    {
        if (_users > 0) _users--;
        if (_users == 0) Shutdown();
    }

    /// <summary>
    /// Stops collecting icons: programs are asked to add their icons again (they go back to Explorer),
    /// and Explorer's taskbar is put back on top. Safe to call anytime; call on the UI thread.
    /// </summary>
    public static void Shutdown()
    {
        if (_area == null) return;
        try { _area.Dispose(); }
        catch (Exception) { /* Restore-Taskbar.ps1 does the same as a fallback */ }
        _area = null;
        _users = 0;
        RaiseExplorerTaskbar();
        Watchdog.Update();
    }

    public static bool IsRunning => _area != null;

    /// <summary>Icons kept on the bar from an earlier session (ManagedShell identifiers); set by the main window.</summary>
    public static string[]? SavedPins { get; set; }

    /// <summary>Called with the new list after an icon was pinned or unpinned, so it can be saved.</summary>
    public static Action<string[]>? PinsChanged { get; set; }

    /// <summary>Moves a tray icon between the bar and the ^ flyout.</summary>
    public static void TogglePin(ManagedShell.WindowsTray.NotifyIcon icon)
    {
        if (_area == null) return;
        if (icon.IsPinned) icon.Unpin();
        else icon.Pin();
        SavedPins = _area.PinnedNotifyIcons;
        PinsChanged?.Invoke(_area.PinnedNotifyIcons);
    }

    // ---------- App-bar requests while ManagedShell owns "Shell_TrayWnd" ----------

    private const int ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const uint WM_COPYDATA = 0x004A;

    /// <summary>
    /// ABM_QUERYPOS / ABM_SETPOS (asking for and reserving screen space) carry their data in a shared
    /// memory block of the program that asked. Forwarded as-is, Explorer can't use that block. So, like
    /// ManagedShell's own AppBarManager: copy the data into a new block owned by Explorer, send that,
    /// and copy Explorer's answer (the rectangle it granted) back to the program that asked.
    /// All other app-bar messages are left to ManagedShell, which forwards them to Explorer unchanged.
    /// </summary>
    private static IntPtr OnAppBarMessage(ShellNative.APPBARMSGDATAV3 message, ref bool handled)
    {
        if (message.dwMessage != ABM_QUERYPOS && message.dwMessage != ABM_SETPOS) return IntPtr.Zero;

        var explorer = WindowsTaskbar.ExplorerTaskbarWindow();
        if (explorer == IntPtr.Zero) return IntPtr.Zero;
        NativeMethods.GetWindowThreadProcessId(explorer, out uint explorerPid);

        int dataSize = Marshal.SizeOf<ShellNative.APPBARDATAV2>();
        IntPtr callerData = SHLockShared((IntPtr)message.hSharedMemory, (uint)message.dwSourceProcessId);
        if (callerData == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            IntPtr explorerBlock = SHAllocShared(IntPtr.Zero, (uint)dataSize, explorerPid);
            if (explorerBlock == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                // 1. The caller's request into Explorer's block.
                CopyShared(callerData, explorerBlock, explorerPid, dataSize, toBlock: true);

                // 2. Send the request to Explorer, pointing at its own block.
                message.hSharedMemory = (long)explorerBlock;
                message.dwSourceProcessId = (int)explorerPid;
                IntPtr result = SendCopyData(explorer, message);

                // 3. Explorer's answer back to the caller.
                CopyShared(callerData, explorerBlock, explorerPid, dataSize, toBlock: false);
                handled = true;
                return result;
            }
            finally
            {
                SHFreeShared(explorerBlock, explorerPid);
            }
        }
        finally
        {
            SHUnlockShared(callerData);
        }
    }

    /// <summary>Copies the app-bar data between the caller's mapped block and Explorer's block (either direction).</summary>
    private static void CopyShared(IntPtr callerData, IntPtr explorerBlock, uint explorerPid, int size, bool toBlock)
    {
        IntPtr explorerData = SHLockShared(explorerBlock, explorerPid);
        if (explorerData == IntPtr.Zero) return;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(toBlock ? callerData : explorerData, bytes, 0, size);
            Marshal.Copy(bytes, 0, toBlock ? explorerData : callerData, size);
        }
        finally
        {
            SHUnlockShared(explorerData);
        }
    }

    /// <summary>Sends an app-bar message to Explorer the way Windows does: WM_COPYDATA with dwData 0.</summary>
    private static IntPtr SendCopyData(IntPtr explorer, ShellNative.APPBARMSGDATAV3 message)
    {
        int size = Marshal.SizeOf(message);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(message, buffer, false);
            var data = new COPYDATASTRUCT { dwData = IntPtr.Zero, cbData = size, lpData = buffer };
            SendMessageTimeout(explorer, WM_COPYDATA, (IntPtr)message.abd.hWnd, ref data,
                0x0002 /* SMTO_ABORTIFHUNG */, 3000, out var result);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT data,
        uint flags, uint timeoutMs, out IntPtr result);

    // Shared memory between processes (shlwapi), the same functions Windows uses for app-bar messages.
    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHAllocShared(IntPtr data, uint size, uint processId);

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHLockShared(IntPtr shared, uint processId);

    [DllImport("shlwapi.dll")]
    private static extern bool SHUnlockShared(IntPtr data);

    [DllImport("shlwapi.dll")]
    private static extern bool SHFreeShared(IntPtr shared, uint processId);

    // ---------- Explorer's taskbar ----------

    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    /// <summary>ManagedShell pushes Explorer's taskbar to the back while it runs; bring it to the front again.</summary>
    private static void RaiseExplorerTaskbar()
    {
        var explorerTaskbar = WindowsTaskbar.ExplorerTaskbarWindow();
        if (explorerTaskbar != IntPtr.Zero)
            NativeMethods.SetWindowPos(explorerTaskbar, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
