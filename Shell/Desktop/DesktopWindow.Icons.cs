using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>The desktop's icons: reading the Desktop folders, the grid, sorting, and the View options.</summary>
public sealed partial class DesktopWindow
{
    /// <summary>One icon on the desktop.</summary>
    private sealed class DesktopItem
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required Border Visual { get; init; }
        public required Image Picture { get; init; }
        public required TextBlock Label { get; init; }
        public bool IsFolder { get; init; }

        /// <summary>One of Windows' own icons (Recycle Bin, This PC, ...): no file, can't be renamed, deleted or copied.</summary>
        public bool IsSpecial => SpecialIcons.IsSpecial(Path);
        public bool IsRecycleBin => Path == SpecialIcons.RecycleBin;
    }

    private static readonly string UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string SharedDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SelectedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x9F, 0xFF)));

    private readonly Canvas _iconArea = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly List<DesktopItem> _items = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int _shownIconPixels;

    private static DesktopLayout Layout => DesktopLayout.Current;

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    private void SetUpIcons()
    {
        SetUpSelection(); // clicks, selection box, moving icons: DesktopWindow.Selection.cs
        SetUpDragDrop();  // onto folders / Recycle Bin / programs, in and out: DesktopWindow.DragDrop.cs
        _iconArea.SizeChanged += (_, _) => Arrange();
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
    }

    private void WatchDesktopFolders()
    {
        foreach (var folder in new[] { UserDesktop, SharedDesktop }.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder) { EnableRaisingEvents = true, IncludeSubdirectories = false };
            void Changed(object? s, EventArgs e) => Dispatcher.InvokeAsync(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += Changed;
            _watchers.Add(watcher);
        }
    }

    /// <summary>A View option changed (here or on another desktop window).</summary>
    private void OnLayoutChanged()
    {
        if (!_showIcons) return;
        ShowOrHideIcons();
        if (_shownIconPixels != Layout.IconPixels)
        {
            foreach (var item in _items) SizeIcon(item);
            _shownIconPixels = Layout.IconPixels;
        }
        Arrange();
    }

    // ---------- Reading the Desktop folders ----------

    /// <summary>
    /// Reads both Desktop folders again (the user's first; the shared one adds what isn't there yet).
    /// Icons that are still there are kept as they are; only new ones are made.
    /// </summary>
    public void Refresh()
    {
        if (!_showIcons) return;
        _shownIconPixels = Layout.IconPixels;

        var found = new List<(string Path, string Name, bool IsFolder)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in SpecialIcons.Shown()) found.Add((id, name, false)); // Recycle Bin, This PC, ...
        foreach (var folder in new[] { UserDesktop, SharedDesktop }.Where(Directory.Exists))
        {
            List<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder).ToList(); }
            catch (Exception) { continue; }

            foreach (var path in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); } catch (Exception) { continue; }
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue; // desktop.ini etc.
                var name = IsShortcut(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
                if (!names.Add(name)) continue;
                found.Add((path, name, (attributes & FileAttributes.Directory) != 0));
            }
        }

        var stillThere = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _items.Where(i => !stillThere.Contains(i.Path)).ToList())
        {
            _iconArea.Children.Remove(gone.Visual);
            _items.Remove(gone);
            _selection.Remove(gone);
        }

        var known = _items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, name, isFolder) in found.Where(f => !known.Contains(f.Path)))
        {
            var item = CreateItem(path, name, isFolder);
            _items.Add(item);
            _iconArea.Children.Add(item.Visual);
        }

        // Forget positions of icons that are gone (keeps desktop.json small).
        foreach (var path in Layout.Positions.Keys.Where(p => !stillThere.Contains(p)).ToList())
            Layout.Positions.Remove(path);

        // The Recycle Bin's picture shows whether it's empty: look again.
        foreach (var bin in _items.Where(i => i.IsRecycleBin)) LoadPicture(bin);

        ShowOrHideIcons();
        Arrange();
        ShellMode.Log($"Desktop icons listed: {_items.Count}");
    }

    private void LoadPicture(DesktopItem item) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => item.Picture.Source = ShellIcons.Get(item.Path, Math.Max(Layout.IconPixels, 48), fresh: true));

    /// <summary>
    /// "Show desktop icons": hides the icons, not the area they're on, so a right-click on the desktop
    /// still opens the menu (to turn them back on).
    /// </summary>
    private void ShowOrHideIcons()
    {
        var visibility = Layout.ShowIcons ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _items) item.Visual.Visibility = visibility;
        if (!Layout.ShowIcons) SelectOnly(null);
    }

    private static bool IsShortcut(string path) =>
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    private DesktopItem CreateItem(string path, string name, bool isFolder)
    {
        var picture = new Image { Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var label = new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            FontSize = 12,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.9 }, // readable on any wallpaper
        };
        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(label);

        var visual = new Border
        {
            Padding = new Thickness(2), CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent, Child = stack, ToolTip = name,
        };
        var item = new DesktopItem { Path = path, Name = name, Visual = visual, Picture = picture, Label = label, IsFolder = isFolder };
        SizeIcon(item);

        visual.MouseEnter += (_, _) => { if (!_selection.Contains(item)) visual.Background = HoverBrush; };
        visual.MouseLeave += (_, _) => { if (!_selection.Contains(item)) visual.Background = Brushes.Transparent; };
        visual.MouseLeftButtonDown += (_, e) => OnItemMouseDown(item, e);
        visual.MouseRightButtonUp += (_, e) => OnItemRightClick(item, e);
        return item;
    }

    /// <summary>Sizes an icon for the current icon size; the picture loads in the background.</summary>
    private void SizeIcon(DesktopItem item)
    {
        var (cellWidth, cellHeight) = Layout.Cell;
        int pixels = Layout.IconPixels;
        item.Visual.Width = cellWidth - 4;
        item.Visual.Height = cellHeight - 4;
        item.Picture.Width = item.Picture.Height = pixels;
        item.Label.FontSize = Layout.IconSize == DesktopIconSize.Small ? 11 : 12;
        item.Label.MaxHeight = item.Label.FontSize * 2.8; // two lines
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => item.Picture.Source = ShellIcons.Get(item.Path, Math.Max(pixels, 48)));
    }

    // ---------- The grid ----------

    /// <summary>
    /// Puts every icon in its place, like Windows: with auto-arrange, packed in columns in the sort order;
    /// otherwise at its saved position (snapped to the grid if "align to grid" is on), and new icons in
    /// the first free grid cell.
    /// </summary>
    private void Arrange()
    {
        if (_items.Count == 0 || _iconArea.ActualHeight <= 0) return;
        var (cellWidth, cellHeight) = Layout.Cell;
        int rows = Math.Max(1, (int)(_iconArea.ActualHeight / cellHeight));
        int columns = Math.Max(1, (int)(_iconArea.ActualWidth / cellWidth));
        var taken = new HashSet<(int Column, int Row)>();
        bool changed = false;

        (int, int) NextFree((int Column, int Row) from)
        {
            for (int i = from.Column * rows + from.Row; ; i++)
            {
                var cell = (i / rows, i % rows);
                if (!taken.Contains(cell)) return cell;
            }
        }

        void Place(DesktopItem item, double x, double y)
        {
            Canvas.SetLeft(item.Visual, x + 2);
            Canvas.SetTop(item.Visual, y + 2);
        }

        if (Layout.AutoArrange)
        {
            int i = 0;
            foreach (var item in Sorted(_items, Layout.SortBy))
            {
                Place(item, i / rows * cellWidth, i % rows * cellHeight);
                i++;
            }
            return;
        }

        // Icons with a position first (in the sort order, so a crowded cell goes to the first one).
        var placed = new List<DesktopItem>();
        foreach (var item in Sorted(_items, Layout.SortBy))
        {
            if (!Layout.Positions.TryGetValue(item.Path, out var position)) continue;
            double x = Math.Clamp(position.X, 0, Math.Max(0, (columns - 1) * cellWidth));
            double y = Math.Clamp(position.Y, 0, Math.Max(0, (rows - 1) * cellHeight));
            var cell = ((int)Math.Round(x / cellWidth), Math.Min(rows - 1, (int)Math.Round(y / cellHeight)));
            if (Layout.AlignToGrid)
            {
                if (taken.Contains(cell)) cell = NextFree(cell);
                x = cell.Item1 * cellWidth;
                y = cell.Item2 * cellHeight;
            }
            taken.Add(cell);
            if (position.X != x || position.Y != y) { Layout.Positions[item.Path] = new IconPosition(x, y); changed = true; }
            Place(item, x, y);
            placed.Add(item);
        }

        // New icons: the first free cell.
        foreach (var item in Sorted(_items.Except(placed), Layout.SortBy))
        {
            var cell = NextFree((0, 0));
            taken.Add(cell);
            double x = cell.Item1 * cellWidth, y = cell.Item2 * cellHeight;
            Layout.Positions[item.Path] = new IconPosition(x, y);
            changed = true;
            Place(item, x, y);
        }

        if (changed) DesktopLayout.SaveSoon();
    }

    /// <summary>The order for "Sort by": folders first, like Windows (except for Item type, which groups by type anyway).</summary>
    private static IEnumerable<DesktopItem> Sorted(IEnumerable<DesktopItem> items, DesktopSort sort)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        static FileSystemInfo Info(DesktopItem i) => i.IsFolder ? new DirectoryInfo(i.Path) : new FileInfo(i.Path);
        static long Size(DesktopItem i) { try { return i.IsFolder ? 0 : new FileInfo(i.Path).Length; } catch (Exception) { return 0; } }
        static DateTime Modified(DesktopItem i) { try { return Info(i).LastWriteTime; } catch (Exception) { return DateTime.MinValue; } }
        static string Type(DesktopItem i) => i.IsFolder ? "" : Path.GetExtension(i.Path).ToLowerInvariant();

        var folderFirst = items.OrderBy(i => i.IsSpecial ? 0 : i.IsFolder ? 1 : 2); // Windows' own icons first, like Explorer
        return sort switch
        {
            DesktopSort.Size => folderFirst.ThenBy(Size).ThenBy(i => i.Name, byName),
            DesktopSort.ItemType => items.OrderBy(i => i.IsSpecial ? 0 : 1).ThenBy(Type).ThenBy(i => i.Name, byName),
            DesktopSort.DateModified => folderFirst.ThenByDescending(Modified).ThenBy(i => i.Name, byName),
            _ => folderFirst.ThenBy(i => i.Name, byName),
        };
    }

    // ---------- View and Sort by ----------

    private void SetIconSize(DesktopIconSize size)
    {
        var (oldWidth, oldHeight) = Layout.Cell;
        Layout.IconSize = size;
        var (newWidth, newHeight) = Layout.Cell;
        // Keep icons in the same grid cell at the new size.
        foreach (var (path, position) in Layout.Positions.ToList())
            Layout.Positions[path] = new IconPosition(Math.Round(position.X / oldWidth) * newWidth, Math.Round(position.Y / oldHeight) * newHeight);
        DesktopLayout.NotifyChanged();
    }

    /// <summary>"Sort by": arranges all icons in that order once (like Windows), and remembers the choice.</summary>
    private void SortIcons(DesktopSort sort)
    {
        Layout.SortBy = sort;
        Layout.Positions.Clear(); // every icon gets the next free cell, in the new order
        DesktopLayout.NotifyChanged();
    }

}
