using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace WindowsCustomizer;

/// <summary>What the desktop's icons and menus do: open, rename, delete, new, cut/copy/paste, properties, terminal.</summary>
public sealed partial class DesktopWindow
{
    private static void Open(string path)
    {
        try { ShellLaunch.Start(path); }
        catch (Exception ex) { MessageBox.Show($"Could not open it:\n{ex.Message}", "Desktop", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void OpenItems(IReadOnlyList<DesktopItem> items)
    {
        if (items.Count > 15 && MessageBox.Show($"Open all {items.Count} items?", "Desktop",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var item in items) Open(item.Path);
    }

    // ---------- File operations (Windows' own: progress, confirmations, Recycle Bin, Ctrl+Z in Explorer) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Runs a Windows file operation on a list of paths; false if it failed (not if the user cancelled).</summary>
    private bool FileOperation(uint function, IEnumerable<string> paths, string? target, ushort flags)
    {
        var operation = new SHFILEOPSTRUCT
        {
            hwnd = Handle,
            wFunc = function,
            pFrom = string.Join("\0", paths) + "\0\0", // a list ends with two zeros
            pTo = target == null ? null : target + "\0\0",
            fFlags = flags,
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 && !operation.fAnyOperationsAborted)
        {
            MessageBox.Show(this, $"Windows could not finish this (error {result}).", "Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    /// <summary>To the Recycle Bin (Shift+Delete: permanently), all at once with one Windows confirmation, like Explorer.</summary>
    private void DeleteItems(IReadOnlyList<DesktopItem> items, bool permanently = false)
    {
        if (items.Count == 0) return;
        FileOperation(FO_DELETE, items.Select(i => i.Path), null, permanently ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING));
        Refresh();
    }

    // ---------- Cut / Copy / Paste (the Windows clipboard, so it works with File Explorer too) ----------

    private readonly HashSet<string> _cut = new(StringComparer.OrdinalIgnoreCase);

    private void CopyItems(IReadOnlyList<DesktopItem> items, bool cut)
    {
        if (items.Count == 0) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(items.Select(i => i.Path).ToArray());
        var data = new DataObject();
        data.SetFileDropList(files);
        // How Explorer tells "cut" (move) from "copy": 2 = move, 5 = copy + link.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, copy: true); }
        catch (Exception ex) { MessageBox.Show(this, $"The clipboard is busy: {ex.Message}", "Desktop"); return; }

        _cut.Clear();
        if (cut) foreach (var item in items) _cut.Add(item.Path);
        foreach (var item in _items) item.Visual.Opacity = _cut.Contains(item.Path) ? 0.5 : 1; // cut icons look faded, like Explorer
    }

    private static bool CanPaste()
    {
        try { return Clipboard.ContainsFileDropList(); } catch (Exception) { return false; }
    }

    /// <summary>Pastes files from the clipboard onto the desktop (moves them if they were cut), at the given spot.</summary>
    private void Paste(Point? at = null)
    {
        System.Collections.Specialized.StringCollection files;
        bool move;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            files = Clipboard.GetFileDropList();
            move = Clipboard.GetData("Preferred DropEffect") is MemoryStream effect && effect.Length >= 4 &&
                   (BitConverter.ToInt32(effect.ToArray(), 0) & 2) != 0;
        }
        catch (Exception) { return; }
        var paths = files.Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count == 0) return;

        bool sameFolder = paths.All(p => string.Equals(Path.GetDirectoryName(p), UserDesktop, StringComparison.OrdinalIgnoreCase));
        if (move && sameFolder) return; // cut and pasted in the same place: nothing to do
        var before = SnapshotDesktop();
        ushort flags = (ushort)(FOF_ALLOWUNDO | (sameFolder ? FOF_RENAMEONCOLLISION : 0)); // "X - Copy" when copying in place
        if (!FileOperation(move ? FO_MOVE : FO_COPY, paths, UserDesktop, flags)) return;

        if (move)
        {
            _cut.Clear();
            try { Clipboard.Clear(); } catch (Exception) { }
        }
        PlaceNewItems(before, at);
    }

    /// <summary>What's on the desktop now, to find what a paste or "New" added.</summary>
    private HashSet<string> SnapshotDesktop() =>
        Directory.Exists(UserDesktop)
            ? Directory.EnumerateFileSystemEntries(UserDesktop).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts newly added icons at the spot (if any; the next free cells after it) and selects them.</summary>
    private void PlaceNewItems(HashSet<string> before, Point? at)
    {
        var added = SnapshotDesktop().Where(p => !before.Contains(p)).ToList();
        if (at is { } spot && !Layout.AutoArrange && added.Count > 0)
        {
            // The cell under the spot, or the next free ones after it; icons already there stay put.
            var (cellWidth, cellHeight) = Layout.Cell;
            int rows = Math.Max(1, (int)(_iconArea.ActualHeight / cellHeight));
            var taken = Layout.Positions.Where(p => !added.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                              .Select(p => ((int)Math.Round(p.Value.X / cellWidth), (int)Math.Round(p.Value.Y / cellHeight))).ToHashSet();
            int index = (int)(spot.X / cellWidth) * rows + Math.Min(rows - 1, (int)(spot.Y / cellHeight));
            foreach (var path in added)
            {
                while (taken.Contains((index / rows, index % rows))) index++;
                taken.Add((index / rows, index % rows));
                Layout.Positions[path] = new IconPosition(index / rows * cellWidth, index % rows * cellHeight);
            }
        }
        Refresh();
        SelectOnly(null);
        foreach (var item in _items.Where(i => added.Contains(i.Path, StringComparer.OrdinalIgnoreCase))) SetSelected(item, true);
    }

    private static void CopyAsPath(IReadOnlyList<DesktopItem> items)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => $"\"{i.Path}\""))); }
        catch (Exception) { /* clipboard busy */ }
    }

    // ---------- Rename / New ----------

    private void Rename(string path)
    {
        bool shortcut = IsShortcut(path);
        string extension = shortcut ? Path.GetExtension(path) : "";
        string current = shortcut ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        if (InputDialog.Ask(this, "Rename", "New name:", current) is not { } newName || newName == current) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "A name can't contain any of these: \\ / : * ? \" < > |", "Rename");
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(path)!, newName + extension);
        try
        {
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            // The renamed icon stays where it was.
            if (Layout.Positions.Remove(path, out var position)) Layout.Positions[target] = position;
            Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not rename it:\n{ex.Message}", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Makes something new on the desktop at the spot, selects it and asks for its name (like Explorer).</summary>
    private void CreateNew(Func<string> create, Point? at)
    {
        var before = SnapshotDesktop();
        string path;
        try { path = create(); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not create it:\n{ex.Message}", "Desktop"); return; }
        PlaceNewItems(before, at);
        Rename(path);
    }

    private void NewFolder(Point? at) => CreateNew(() =>
    {
        string path = ShellNew.FreeName(UserDesktop, "New folder", "");
        Directory.CreateDirectory(path);
        return path;
    }, at);

    private void NewFile(ShellNew.FileType type, Point? at) => CreateNew(() => ShellNew.Create(type, UserDesktop), at);

    /// <summary>Windows' "Create Shortcut" wizard, for a new shortcut on the desktop.</summary>
    private void NewShortcut(Point? at)
    {
        var before = SnapshotDesktop();
        string path = ShellNew.FreeName(UserDesktop, "New shortcut", ".lnk");
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>()); // the wizard fills it in (and removes it if cancelled)
            var wizard = Process.Start(new ProcessStartInfo("rundll32.exe", $"appwiz.cpl,NewLinkHere {path}") { UseShellExecute = false });
            Task.Run(() => wizard?.WaitForExit()).ContinueWith(_ => PlaceNewItems(before, at), TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not start the shortcut wizard:\n{ex.Message}", "Desktop"); }
    }

    // ---------- Other ----------

    /// <summary>Windows Terminal in the Desktop folder (PowerShell if Terminal isn't installed).</summary>
    private static void OpenTerminal()
    {
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        try
        {
            if (File.Exists(wt))
                Process.Start(new ProcessStartInfo(wt, $"-d \"{UserDesktop}\"") { UseShellExecute = false, WorkingDirectory = UserDesktop })?.Dispose();
            else
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { UseShellExecute = true, WorkingDirectory = UserDesktop })?.Dispose();
        }
        catch (Exception ex) { MessageBox.Show($"Could not open a terminal:\n{ex.Message}", "Desktop"); }
    }

    private static void OpenSettings(string page)
    {
        try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { /* Settings not available */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    /// <summary>Windows' own Properties window for a file or folder.</summary>
    private void Properties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd = Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
        };
        ShellExecuteEx(ref info);
    }
}
