using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsCustomizer;

/// <summary>
/// Windows' own right-click menu (the classic one, behind "Show more options"): for files and folders, or for
/// the desktop background. It includes everything other programs add (Open with Code, 7-Zip, Send to, ...).
/// Built with the shell's IContextMenu, shown with TrackPopupMenuEx; the owner window must pass its menu
/// messages to <see cref="HandleMenuMessage"/> so submenus like "Send to" and "Open with" fill in.
/// </summary>
internal static class ShellContextMenu
{
    /// <summary>Our own entries added to the classic menu (command ids above the shell's).</summary>
    public sealed record Extra(string Text, Action? Action, bool IsChecked = false, IReadOnlyList<Extra>? Submenu = null)
    {
        public static readonly Extra Line = new("-", null);
    }

    private const int FirstShellId = 1, LastShellId = 0x7FFF, FirstOwnId = 0x8000;
    private static IContextMenu2? _open2;
    private static IContextMenu3? _open3;

    /// <summary>Shows the menu for these files/folders (all must be in one folder). Returns false if Windows couldn't make it.</summary>
    /// <param name="onRename">Called for the "Rename" entry (renaming is up to the desktop).</param>
    public static bool ShowForItems(IntPtr owner, IReadOnlyList<string> paths, bool extended, Action<string> onRename)
    {
        if (paths.Count == 0) return false;
        var pidls = new List<IntPtr>();
        IShellFolder? parent = null;
        try
        {
            var children = new List<IntPtr>();
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0) continue;
                pidls.Add(pidl);
                var iid = typeof(IShellFolder).GUID;
                if (SHBindToParent(pidl, ref iid, out var folderObject, out var child) != 0) continue;
                var folder = (IShellFolder)folderObject;
                if (parent == null) parent = folder;
                else Marshal.ReleaseComObject(folder); // same Desktop folder; the first one is used
                children.Add(child); // points into pidl; freed with it
            }
            if (parent == null || children.Count == 0) return false;

            var menuIid = typeof(IContextMenu).GUID;
            if (parent.GetUIObjectOf(owner, (uint)children.Count, children.ToArray(), ref menuIid, IntPtr.Zero, out var menuObject) != 0)
                return false;
            uint flags = CMF_NORMAL | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
            Show(owner, (IContextMenu)menuObject, flags, Path.GetDirectoryName(paths[0]), null, verb =>
            {
                if (verb == "rename") { onRename(paths[0]); return true; }
                return false;
            });
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
            if (parent != null) Marshal.ReleaseComObject(parent);
        }
    }

    /// <summary>Shows Windows' menu for the desktop background (New, Paste, Display settings, Personalize, other apps' entries).</summary>
    public static bool ShowForDesktop(IntPtr owner, string desktopFolder, bool extended, IReadOnlyList<Extra> extras)
    {
        IShellFolder? folder = null;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHGetDesktopFolder(out var desktop) != 0) return false;
            // The user's Desktop folder (not the namespace root), so New/Paste act there.
            if (SHParseDisplayName(desktopFolder, IntPtr.Zero, out pidl, 0, out _) != 0) { Marshal.ReleaseComObject(desktop); return false; }
            var folderIid = typeof(IShellFolder).GUID;
            int hr = desktop.BindToObject(pidl, IntPtr.Zero, ref folderIid, out var folderObject);
            Marshal.ReleaseComObject(desktop);
            if (hr != 0) return false;
            folder = (IShellFolder)folderObject;

            var menuIid = typeof(IContextMenu).GUID;
            if (folder.CreateViewObject(owner, ref menuIid, out var menuObject) != 0) return false;
            Show(owner, (IContextMenu)menuObject, CMF_NORMAL | (extended ? CMF_EXTENDEDVERBS : 0), desktopFolder, extras, _ => false);
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
    }

    /// <param name="handleVerb">Gets the chosen entry's verb first; true = handled here, don't let Windows run it.</param>
    private static void Show(IntPtr owner, IContextMenu menu, uint flags, string? directory, IReadOnlyList<Extra>? extras, Func<string, bool> handleVerb)
    {
        IntPtr hmenu = CreatePopupMenu();
        var actions = new Dictionary<int, Action>();
        try
        {
            uint position = 0;
            if (extras != null)
            {
                int nextId = FirstOwnId;
                AddExtras(hmenu, extras, ref nextId, actions);
                AppendMenu(hmenu, MF_SEPARATOR, UIntPtr.Zero, null); // between ours and Windows' entries
                position = (uint)GetMenuItemCount(hmenu);
            }
            menu.QueryContextMenu(hmenu, position, FirstShellId, LastShellId, flags);
            _open2 = menu as IContextMenu2;
            _open3 = menu as IContextMenu3;

            GetCursorPos(out var at);
            SetForegroundWindow(owner); // otherwise the menu doesn't close when clicking elsewhere
            int id = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.X, at.Y, owner, IntPtr.Zero);
            _open2 = null;
            _open3 = null;
            if (id == 0) return;
            if (actions.TryGetValue(id, out var action)) { action(); return; }
            if (id < FirstShellId || id > LastShellId) return;

            int offset = id - FirstShellId;
            var verb = new StringBuilder(256);
            if (menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, verb, verb.Capacity) == 0 && handleVerb(verb.ToString().ToLowerInvariant()))
                return;

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = owner,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectory = directory,
                lpDirectoryW = directory,
                nShow = SW_SHOWNORMAL,
                ptInvoke = at,
            };
            if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) info.fMask |= CMIC_MASK_CONTROL_DOWN;
            if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) info.fMask |= CMIC_MASK_SHIFT_DOWN;
            menu.InvokeCommand(ref info);
        }
        finally
        {
            DestroyMenu(hmenu);
            Marshal.ReleaseComObject(menu);
        }
    }

    private static void AddExtras(IntPtr hmenu, IReadOnlyList<Extra> extras, ref int nextId, Dictionary<int, Action> actions)
    {
        foreach (var extra in extras)
        {
            if (extra == Extra.Line) { AppendMenu(hmenu, MF_SEPARATOR, UIntPtr.Zero, null); continue; }
            if (extra.Submenu != null)
            {
                IntPtr sub = CreatePopupMenu();
                AddExtras(sub, extra.Submenu, ref nextId, actions);
                AppendMenu(hmenu, MF_POPUP, (UIntPtr)(ulong)sub, extra.Text);
                continue;
            }
            int id = nextId++;
            if (extra.Action != null) actions[id] = extra.Action;
            AppendMenu(hmenu, MF_STRING | (extra.IsChecked ? MF_CHECKED : 0), (UIntPtr)id, extra.Text);
        }
    }

    /// <summary>
    /// Call from the owner window's message hook: while a classic menu is open, its submenus ("Send to",
    /// "Open with", "New") are filled and drawn by the shell through these messages.
    /// </summary>
    public static bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return false;
        try
        {
            if (_open3 != null) return _open3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
            if (_open2 != null) return _open2.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
        }
        catch (Exception) { /* a broken shell extension: ignore its message */ }
        return false;
    }

    // ---------- Win32 / COM ----------

    private const uint CMF_NORMAL = 0, CMF_EXTENDEDVERBS = 0x100, CMF_CANRENAME = 0x10;
    private const uint GCS_VERBW = 4;
    private const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_SHIFT_DOWN = 0x10000000,
        CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const int SW_SHOWNORMAL = 1;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_POPUP = 0x10, MF_CHECKED = 0x8;
    private const int WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder folder);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object folder, out IntPtr pidlLast);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hmenu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hmenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hmenu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}
