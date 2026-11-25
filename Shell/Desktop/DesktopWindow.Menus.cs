using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsCustomizer;

/// <summary>
/// The desktop's right-click menus, like Windows 11: a short modern menu, and "Show more options" (or
/// Shift+right-click, or Shift+F10) for Windows' full classic menu with every program's entries.
/// </summary>
public sealed partial class DesktopWindow
{
    private static bool ShiftDown => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    /// <summary>Right-click on the empty desktop, at that spot (where new things go).</summary>
    private void ShowDesktopMenu(Point at)
    {
        if (ShiftDown) { ShowClassicDesktopMenu(at, extended: true); return; }

        var menu = ModernMenu.Create();

        var view = ModernMenu.Submenu(menu, ModernMenu.View, "View");
        ModernMenu.Check(view, "Large icons", Layout.IconSize == DesktopIconSize.Large, () => SetIconSize(DesktopIconSize.Large));
        ModernMenu.Check(view, "Medium icons", Layout.IconSize == DesktopIconSize.Medium, () => SetIconSize(DesktopIconSize.Medium));
        ModernMenu.Check(view, "Small icons", Layout.IconSize == DesktopIconSize.Small, () => SetIconSize(DesktopIconSize.Small));
        ModernMenu.Separator(view);
        ModernMenu.Check(view, "Auto arrange icons", Layout.AutoArrange, ToggleAutoArrange);
        ModernMenu.Check(view, "Align icons to grid", Layout.AlignToGrid, ToggleAlignToGrid);
        ModernMenu.Separator(view);
        ModernMenu.Check(view, "Show desktop icons", Layout.ShowIcons, ToggleShowIcons);

        var sort = ModernMenu.Submenu(menu, ModernMenu.Sort, "Sort by");
        ModernMenu.Item(sort, "", "Name", () => SortIcons(DesktopSort.Name));
        ModernMenu.Item(sort, "", "Size", () => SortIcons(DesktopSort.Size));
        ModernMenu.Item(sort, "", "Item type", () => SortIcons(DesktopSort.ItemType));
        ModernMenu.Item(sort, "", "Date modified", () => SortIcons(DesktopSort.DateModified));

        ModernMenu.Item(menu, ModernMenu.Refresh, "Refresh", Refresh);
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.Paste, "Paste", () => Paste(at), "Ctrl+V", enabled: CanPaste());
        if (UndoText is { } undo) ModernMenu.Item(menu, ModernMenu.Undo, undo, UndoLast, "Ctrl+Z");
        ModernMenu.Separator(menu);

        var create = ModernMenu.Submenu(menu, ModernMenu.New, "New");
        ModernMenu.Item(create, ModernMenu.Folder, "Folder", () => NewFolder(at));
        ModernMenu.Item(create, ModernMenu.Link, "Shortcut", () => NewShortcut(at));
        ModernMenu.Separator(create);
        foreach (var type in ShellNew.Types())
            ModernMenu.Item(create, ModernMenu.File, type.Name, () => NewFile(type, at));

        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.Display, "Display settings", () => OpenSettings("ms-settings:display"));
        ModernMenu.Item(menu, ModernMenu.Personalize, "Personalize", () => OpenSettings("ms-settings:personalization"));
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.Terminal, "Open in Terminal", OpenTerminal);
        if (OpenCustomizer != null) ModernMenu.Item(menu, ModernMenu.Settings, "Windows Customizer", OpenCustomizer);
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.More, "Show more options", () => ShowClassicDesktopMenu(at, extended: false), "Shift+F10");

        menu.IsOpen = true;
    }

    /// <summary>Right-click on icons: the selected ones.</summary>
    private void ShowItemMenu(IReadOnlyList<DesktopItem> items)
    {
        if (items.Count == 0) return;
        if (ShiftDown) { ShowClassicItemMenu(items, extended: true); return; }

        bool one = items.Count == 1;
        var first = items[0];
        bool files = items.Any(i => !i.IsSpecial); // Windows' own icons (Recycle Bin, This PC) are no files
        var menu = ModernMenu.Create();
        ModernMenu.Item(menu, ModernMenu.Open, one ? "Open" : $"Open ({items.Count})", () => OpenItems(items), "Enter");
        if (one && IsShortcut(first.Path))
            ModernMenu.Item(menu, ModernMenu.Location, "Open file location", () => ShellLaunch.ShowInFolder(first.Path));
        if (one && first.IsRecycleBin)
            ModernMenu.Item(menu, ModernMenu.Delete, "Empty Recycle Bin", EmptyRecycleBin);
        if (files) ModernMenu.Item(menu, ModernMenu.Copy, "Copy as path", () => CopyAsPath(items));
        if (one) ModernMenu.Item(menu, ModernMenu.Properties, "Properties", () => Properties(first.Path), "Alt+Enter");
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.More, "Show more options", () => ShowClassicItemMenu(items, extended: false), "Shift+F10");

        if (files)
            ModernMenu.ButtonRow(menu,
                (ModernMenu.Cut, "Cut (Ctrl+X)", () => CopyItems(items, cut: true), true),
                (ModernMenu.Copy, "Copy (Ctrl+C)", () => CopyItems(items, cut: false), true),
                (ModernMenu.Rename, "Rename (F2)", () => Rename(first.Path), one && !first.IsSpecial),
                (ModernMenu.Delete, "Delete (Delete)", () => DeleteItems(items), true));
        menu.IsOpen = true;
    }

    // ---------- Windows' classic menus ----------

    private void ShowClassicDesktopMenu(Point at, bool extended)
    {
        // Our View / Sort by / Refresh at the top, like Explorer's desktop menu; then everything Windows has.
        var extras = new List<ShellContextMenu.Extra>
        {
            new("View", null, Submenu: new ShellContextMenu.Extra[]
            {
                new("Large icons", () => SetIconSize(DesktopIconSize.Large), Layout.IconSize == DesktopIconSize.Large),
                new("Medium icons", () => SetIconSize(DesktopIconSize.Medium), Layout.IconSize == DesktopIconSize.Medium),
                new("Small icons", () => SetIconSize(DesktopIconSize.Small), Layout.IconSize == DesktopIconSize.Small),
                ShellContextMenu.Extra.Line,
                new("Auto arrange icons", ToggleAutoArrange, Layout.AutoArrange),
                new("Align icons to grid", ToggleAlignToGrid, Layout.AlignToGrid),
                ShellContextMenu.Extra.Line,
                new("Show desktop icons", ToggleShowIcons, Layout.ShowIcons),
            }),
            new("Sort by", null, Submenu: new ShellContextMenu.Extra[]
            {
                new("Name", () => SortIcons(DesktopSort.Name)),
                new("Size", () => SortIcons(DesktopSort.Size)),
                new("Item type", () => SortIcons(DesktopSort.ItemType)),
                new("Date modified", () => SortIcons(DesktopSort.DateModified)),
            }),
            new("Refresh", Refresh),
        };
        // After the WPF menu has closed (this can be called from it).
        Dispatcher.BeginInvoke(() =>
        {
            var before = SnapshotDesktop();
            if (!ShellContextMenu.ShowForDesktop(Handle, UserDesktop, extended, extras))
                MessageBox.Show(this, "Windows couldn't make its menu here.", "Desktop");
            PlaceNewItems(before, at); // something from "New" or "Paste" goes where you right-clicked
        });
    }

    private void ShowClassicItemMenu(IReadOnlyList<DesktopItem> items, bool extended)
    {
        // Windows' menu works on items of one folder: the user's Desktop or the shared one (whichever was right-clicked first).
        string? folder = System.IO.Path.GetDirectoryName(items[0].Path);
        var paths = items.Select(i => i.Path)
                         .Where(p => string.Equals(System.IO.Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)).ToList();
        Dispatcher.BeginInvoke(() =>
        {
            if (!ShellContextMenu.ShowForItems(Handle, paths, extended, Rename))
                MessageBox.Show(this, "Windows couldn't make its menu for this.", "Desktop");
        });
    }

    // ---------- View toggles ----------

    private void ToggleAutoArrange() { Layout.AutoArrange = !Layout.AutoArrange; DesktopLayout.NotifyChanged(); }
    private void ToggleAlignToGrid() { Layout.AlignToGrid = !Layout.AlignToGrid; DesktopLayout.NotifyChanged(); }
    private void ToggleShowIcons() { Layout.ShowIcons = !Layout.ShowIcons; DesktopLayout.NotifyChanged(); }
}
