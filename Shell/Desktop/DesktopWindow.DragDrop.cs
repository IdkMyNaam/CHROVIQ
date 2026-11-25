using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// Drag and drop like Windows' desktop: icons onto a folder (move into it), onto the Recycle Bin (delete), onto
/// a program (open the files with it); out of the desktop into File Explorer or other apps; and files from
/// File Explorer onto the desktop. Ctrl = copy, Shift = move, Alt = shortcut, otherwise Windows' rule (same drive:
/// move, other drive: copy).
/// </summary>
public sealed partial class DesktopWindow
{
    private const string OwnDragFormat = "WindowsCustomizer.DesktopDrag";
    private static readonly Brush DropTargetBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x60, 0x9F, 0xFF)));
    private DesktopItem? _dropTarget;

    private void SetUpDragDrop()
    {
        _iconArea.AllowDrop = true;
        _iconArea.DragEnter += OnDragOver;
        _iconArea.DragOver += OnDragOver;
        _iconArea.DragLeave += (_, _) => SetDropTarget(null);
        _iconArea.Drop += OnDrop;
    }

    // ---------- Drop targets ----------

    private static bool IsProgram(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".bat" or ".cmd") return true;
        return ext == ".lnk" && ShortcutTarget(path) is { } target && Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string?> ShortcutTargets = new(StringComparer.OrdinalIgnoreCase);

    private static string? ShortcutTarget(string shortcut)
    {
        if (ShortcutTargets.TryGetValue(shortcut, out var known)) return known;
        string? target = null;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is { } type)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                try { target = (string)shell.CreateShortcut(shortcut).TargetPath; }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
        }
        catch (Exception) { /* not a readable shortcut */ }
        return ShortcutTargets[shortcut] = string.IsNullOrWhiteSpace(target) ? null : target;
    }

    private static bool CanDropOn(DesktopItem item) => item.IsFolder || item.IsRecycleBin || (!item.IsSpecial && IsProgram(item.Path));

    /// <summary>The icon under this spot that things can be dropped on (not one of the dragged ones).</summary>
    private DesktopItem? DropTargetAt(Point at, ICollection<string> dragged)
    {
        foreach (var item in _items)
        {
            if (item.Visual.Visibility != Visibility.Visible || dragged.Contains(item.Path)) continue;
            var bounds = new Rect(Canvas.GetLeft(item.Visual), Canvas.GetTop(item.Visual), item.Visual.Width, item.Visual.Height);
            if (bounds.Contains(at)) return CanDropOn(item) ? item : null;
        }
        return null;
    }

    private void SetDropTarget(DesktopItem? item)
    {
        if (_dropTarget == item) return;
        if (_dropTarget != null) SetSelected(_dropTarget, _selection.Contains(_dropTarget)); // back to its normal look
        _dropTarget = item;
        if (item != null) item.Visual.Background = DropTargetBrush;
    }

    /// <summary>Copy, move or link: the keys first, then Windows' rule (same drive moves, another drive copies).</summary>
    private static DragDropEffects ChooseEffect(DragDropKeyStates keys, DragDropEffects allowed, IReadOnlyList<string> files,
                                                DesktopItem? target, bool ownIcons)
    {
        DragDropEffects wanted;
        if (target?.IsRecycleBin == true) wanted = DragDropEffects.Move;
        else if (target != null && !target.IsFolder) wanted = DragDropEffects.Copy; // a program: open the files with it
        else if (keys.HasFlag(DragDropKeyStates.AltKey) ||
                 (keys.HasFlag(DragDropKeyStates.ControlKey) && keys.HasFlag(DragDropKeyStates.ShiftKey))) wanted = DragDropEffects.Link;
        else if (keys.HasFlag(DragDropKeyStates.ControlKey)) wanted = DragDropEffects.Copy;
        else if (keys.HasFlag(DragDropKeyStates.ShiftKey)) wanted = DragDropEffects.Move;
        else if (ownIcons && target == null) wanted = DragDropEffects.Move; // own icons back onto the desktop: just a new spot
        else
        {
            string destination = target?.Path ?? UserDesktop;
            bool sameDrive = files.All(f => string.Equals(Path.GetPathRoot(f), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase));
            wanted = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if ((allowed & wanted) != 0) return wanted;
        foreach (var fallback in new[] { DragDropEffects.Copy, DragDropEffects.Move, DragDropEffects.Link })
            if ((allowed & fallback) != 0) return fallback;
        return DragDropEffects.None;
    }

    /// <summary>Does what a drop means: delete, open with, move/copy/link into a folder or onto the desktop.</summary>
    private void PerformDrop(IReadOnlyList<string> files, DesktopItem? target, DragDropEffects effect, Point? at, bool ownIcons)
    {
        SetDropTarget(null);
        if (files.Count == 0 || effect == DragDropEffects.None) return;

        if (target?.IsRecycleBin == true) { DeletePaths(files); return; }
        if (target != null && !target.IsFolder)
        {
            try { ShellLaunch.Start(target.Path, string.Join(" ", files.Select(f => $"\"{f}\""))); }
            catch (Exception ex) { MessageBox.Show(this, $"Could not open the files with it:\n{ex.Message}", "Desktop"); }
            return;
        }

        string folder = target?.Path ?? UserDesktop;
        if (ownIcons && target == null && effect == DragDropEffects.Move && at is { } spot)
        {
            // Our own icons dropped back on the desktop (after leaving the window): a new spot, nothing to move.
            var paths = files.ToList();
            RememberPositions("Move", paths.ToDictionary(p => p, p => Layout.Positions.TryGetValue(p, out var pos) ? pos : null, StringComparer.OrdinalIgnoreCase));
            AssignCellsFrom(spot, paths);
            DesktopLayout.SaveSoon();
            Arrange();
            return;
        }
        if (effect == DragDropEffects.Link) { CreateShortcuts(files, folder, target == null ? at : null); return; }
        TransferInto(files, folder, effect == DragDropEffects.Move ? FO_MOVE : FO_COPY, target == null ? at : null,
                     effect == DragDropEffects.Move ? "Move" : "Copy");
    }

    /// <summary>"Create shortcuts here": "Name - Shortcut.lnk" for each file.</summary>
    private void CreateShortcuts(IReadOnlyList<string> files, string folder, Point? at)
    {
        var before = SnapshotDesktop();
        var created = new List<string>();
        if (Type.GetTypeFromProgID("WScript.Shell") is not { } type) return;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var file in files)
            {
                string link = ShellNew.FreeName(folder, Path.GetFileNameWithoutExtension(file) + " - Shortcut", ".lnk");
                dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = file;
                shortcut.WorkingDirectory = Path.GetDirectoryName(file) ?? "";
                shortcut.Save();
                created.Add(link);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not create the shortcut:\n{ex.Message}", "Desktop"); }
        finally { Marshal.FinalReleaseComObject(shell); }
        if (created.Count > 0) RememberCreated("New shortcut", created);
        PlaceNewItems(before, at);
    }

    /// <summary>The grid cell under the spot (or the next free ones after it) for each path; other icons stay put.</summary>
    private void AssignCellsFrom(Point spot, IReadOnlyList<string> paths)
    {
        var (cellWidth, cellHeight) = Layout.Cell;
        int rows = Math.Max(1, (int)(_iconArea.ActualHeight / cellHeight));
        var placing = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = Layout.Positions.Where(p => !placing.Contains(p.Key))
                          .Select(p => ((int)Math.Round(p.Value.X / cellWidth), (int)Math.Round(p.Value.Y / cellHeight))).ToHashSet();
        int index = (int)(Math.Max(0, spot.X) / cellWidth) * rows + Math.Min(rows - 1, (int)(Math.Max(0, spot.Y) / cellHeight));
        foreach (var path in paths)
        {
            while (taken.Contains((index / rows, index % rows))) index++;
            taken.Add((index / rows, index % rows));
            Layout.Positions[path] = new IconPosition(index / rows * cellWidth, index % rows * cellHeight);
        }
    }

    // ---------- Dragging in from other programs (and our own icons coming back) ----------

    private static string[]? DroppedFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedFiles(e.Data) is not { Length: > 0 } files) { e.Effects = DragDropEffects.None; return; }
        bool own = e.Data.GetDataPresent(OwnDragFormat);
        var target = DropTargetAt(e.GetPosition(_iconArea), new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
        e.Effects = ChooseEffect(e.KeyStates, e.AllowedEffects, files, target, own);
        SetDropTarget(target);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedFiles(e.Data) is not { Length: > 0 } files) return;
        bool own = e.Data.GetDataPresent(OwnDragFormat);
        var at = e.GetPosition(_iconArea);
        var target = DropTargetAt(at, new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
        var effect = ChooseEffect(e.KeyStates, e.AllowedEffects, files, target, own);
        e.Effects = effect;
        // After the drag has finished (Windows' file operations show dialogs, which must not run inside it).
        Dispatcher.BeginInvoke(() => PerformDrop(files, target, effect, at, own));
    }

    // ---------- Dragging our icons out ----------

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>Is the mouse over another window now (File Explorer, an app, the taskbar)?</summary>
    private bool MouseOverOtherWindow()
    {
        if (!GetCursorPos(out var at)) return false;
        var window = WindowFromPoint(at);
        return window != IntPtr.Zero && GetAncestor(window, 2 /* GA_ROOT */) != Handle;
    }

    /// <summary>The moved icons left the desktop: from here on it's a normal Windows drag (to Explorer, apps, back).</summary>
    private void StartDragOut()
    {
        var files = _moveStart.Keys.Where(i => !i.IsSpecial).Select(i => i.Path).ToArray();
        CancelGesture(); // icons back to their places; Windows takes over the drag
        if (files.Length == 0) return;

        var data = new DataObject();
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(files);
        data.SetFileDropList(list);
        data.SetData(OwnDragFormat, true);
        DragDrop.DoDragDrop(_iconArea, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        Refresh(); // moved away, or deleted by the target
    }
}
