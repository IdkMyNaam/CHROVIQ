using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.FileIO;

namespace WindowsCustomizer;

/// <summary>What the desktop's icons and menus do: open, rename, delete, new folder, properties.</summary>
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

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// To the Recycle Bin (Shift+Delete: permanently), all at once with one Windows confirmation, like Explorer.
    /// </summary>
    private void DeleteItems(IReadOnlyList<DesktopItem> items, bool permanently = false)
    {
        if (items.Count == 0) return;
        var operation = new SHFILEOPSTRUCT
        {
            hwnd = Handle,
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", items.Select(i => i.Path)) + "\0\0", // a list ends with two zeros
            fFlags = permanently ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING),
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 && !operation.fAnyOperationsAborted)
            MessageBox.Show(this, $"Could not delete everything (error {result}).", "Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

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

    private void NewFolder()
    {
        string path = Path.Combine(UserDesktop, "New folder");
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(UserDesktop, $"New folder ({n})");
        try
        {
            Directory.CreateDirectory(path);
            Refresh();
            Rename(path);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not create the folder:\n{ex.Message}", "Desktop"); }
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

    // ---------- Menus (stage 3 replaces these with Windows 11-style menus and the full Windows menu) ----------

    private ContextMenu ItemMenu(IReadOnlyList<DesktopItem> items)
    {
        var menu = new ContextMenu();
        if (items.Count == 0) return menu;
        string path = items[0].Path;
        bool one = items.Count == 1;
        Add(menu, one ? "Open" : $"Open ({items.Count})", () => OpenItems(items));
        if (one) Add(menu, "Open file location", () => ShellLaunch.ShowInFolder(path));
        menu.Items.Add(new Separator());
        if (one) Add(menu, "Rename", () => Rename(path));
        Add(menu, one ? "Delete" : $"Delete ({items.Count})", () => DeleteItems(items));
        if (one)
        {
            menu.Items.Add(new Separator());
            Add(menu, "Properties", () => Properties(path));
        }
        return menu;
    }

    private ContextMenu DesktopMenu()
    {
        var menu = new ContextMenu();

        var view = new MenuItem { Header = "View" };
        Check(view, "Large icons", Layout.IconSize == DesktopIconSize.Large, () => SetIconSize(DesktopIconSize.Large));
        Check(view, "Medium icons", Layout.IconSize == DesktopIconSize.Medium, () => SetIconSize(DesktopIconSize.Medium));
        Check(view, "Small icons", Layout.IconSize == DesktopIconSize.Small, () => SetIconSize(DesktopIconSize.Small));
        view.Items.Add(new Separator());
        Check(view, "Auto arrange icons", Layout.AutoArrange, () => { Layout.AutoArrange = !Layout.AutoArrange; DesktopLayout.NotifyChanged(); });
        Check(view, "Align icons to grid", Layout.AlignToGrid, () => { Layout.AlignToGrid = !Layout.AlignToGrid; DesktopLayout.NotifyChanged(); });
        view.Items.Add(new Separator());
        Check(view, "Show desktop icons", Layout.ShowIcons, () => { Layout.ShowIcons = !Layout.ShowIcons; DesktopLayout.NotifyChanged(); });
        menu.Items.Add(view);

        var sort = new MenuItem { Header = "Sort by" };
        Check(sort, "Name", false, () => SortIcons(DesktopSort.Name));
        Check(sort, "Size", false, () => SortIcons(DesktopSort.Size));
        Check(sort, "Item type", false, () => SortIcons(DesktopSort.ItemType));
        Check(sort, "Date modified", false, () => SortIcons(DesktopSort.DateModified));
        menu.Items.Add(sort);

        Add(menu, "Refresh", Refresh);
        menu.Items.Add(new Separator());
        Add(menu, "New folder", NewFolder);
        Add(menu, "Open desktop folder", () => ShellLaunch.OpenFolder(UserDesktop));
        menu.Items.Add(new Separator());
        Add(menu, "Display settings", () => Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true })?.Dispose());
        Add(menu, "Personalize", () => Process.Start(new ProcessStartInfo("ms-settings:personalization") { UseShellExecute = true })?.Dispose());
        if (OpenCustomizer != null) Add(menu, "Windows Customizer", OpenCustomizer);
        return menu;
    }

    private static void Add(ItemsControl menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void Check(ItemsControl menu, string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
