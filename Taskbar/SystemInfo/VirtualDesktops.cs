using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Windows' virtual desktops for the taskbar's Desktops module. Windows has no public API to list or switch them,
/// but it keeps the list, their names and the current one in the registry (read here and watched for changes),
/// and switching is done with the same shortcuts you'd press (Ctrl+Win+Left/Right, Ctrl+Win+D, Ctrl+Win+F4).
/// Needs Explorer: as the shell there are no virtual desktops.
/// </summary>
internal static class VirtualDesktops
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    public sealed record Desktop(Guid Id, string Name);

    /// <summary>The desktops in order and the index of the current one (an empty list when unknown).</summary>
    public static (List<Desktop> Desktops, int Current) Read()
    {
        var list = new List<Desktop>();
        int current = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length < 16) return (list, 0);
            var now = key.GetValue("CurrentVirtualDesktop") is byte[] c && c.Length == 16 ? new Guid(c) : Guid.Empty;
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var id = new Guid(ids.AsSpan(i, 16));
                using var desktop = key.OpenSubKey($@"Desktops\{id:B}");
                string name = desktop?.GetValue("Name") as string is { Length: > 0 } n ? n : $"Desktop {list.Count + 1}";
                if (id == now) current = list.Count;
                list.Add(new Desktop(id, name));
            }
        }
        catch (Exception ex) { AppTrace.Log($"Virtual desktops: {ex.Message}"); }
        return (list, current);
    }

    // ---------- Watching for changes ----------

    private static event Action? ChangedInternal;
    private static Thread? _watcher;
    private static readonly object Lock = new();

    /// <summary>Raised (on a background thread) when desktops are added, removed, renamed or switched.</summary>
    public static event Action Changed
    {
        add
        {
            lock (Lock)
            {
                ChangedInternal += value;
                if (_watcher == null)
                {
                    _watcher = new Thread(Watch) { IsBackground = true, Name = "Virtual desktop watcher" };
                    _watcher.Start();
                }
            }
        }
        remove { lock (Lock) ChangedInternal -= value; }
    }

    /// <summary>Waits for Windows to say the key changed (no polling), and tells the listeners.</summary>
    private static void Watch()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key == null) return;
            using var changed = new AutoResetEvent(false);
            while (true)
            {
                int result = RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), true,
                    REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle.DangerousGetHandle(), true);
                if (result != 0) return;
                changed.WaitOne();
                Thread.Sleep(100); // several values change in a row: once after they're done
                ChangedInternal?.Invoke();
            }
        }
        catch (Exception ex) { AppTrace.Log($"Virtual desktops watcher: {ex.Message}"); }
        finally { lock (Lock) _watcher = null; }
    }

    private const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(IntPtr key, bool watchSubtree, uint filter, IntPtr eventHandle, bool asynchronous);

    // ---------- Switching ----------

    /// <summary>Switches to desktop <paramref name="index"/> by pressing Ctrl+Win+Left/Right as often as needed.</summary>
    public static async Task SwitchToAsync(int index)
    {
        var (desktops, current) = Read();
        if (desktops.Count == 0 || index < 0 || index >= desktops.Count || index == current) return;
        ushort arrow = index > current ? VK_RIGHT : VK_LEFT;
        for (int i = 0; i < Math.Abs(index - current); i++)
        {
            Press(VK_CONTROL, VK_LWIN, arrow);
            await Task.Delay(80); // let Windows take each step
        }
    }

    public static void Add() => Press(VK_CONTROL, VK_LWIN, (ushort)'D');
    public static void CloseCurrent() => Press(VK_CONTROL, VK_LWIN, VK_F4);

    private const ushort VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_F4 = 0x73;
    private const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_EXTENDEDKEY = 0x1;

    /// <summary>Presses the keys down in order and lets them go in reverse order.</summary>
    private static void Press(params ushort[] keys)
    {
        foreach (var k in keys) keybd_event((byte)k, 0, IsExtended(k) ? KEYEVENTF_EXTENDEDKEY : 0, UIntPtr.Zero);
        foreach (var k in keys.Reverse()) keybd_event((byte)k, 0, KEYEVENTF_KEYUP | (IsExtended(k) ? KEYEVENTF_EXTENDEDKEY : 0), UIntPtr.Zero);
    }

    private static bool IsExtended(ushort key) => key is VK_LEFT or VK_RIGHT or VK_LWIN;

    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
