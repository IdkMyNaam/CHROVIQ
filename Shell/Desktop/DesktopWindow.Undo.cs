using System.IO;
using System.Windows;

namespace WindowsCustomizer;

/// <summary>
/// Ctrl+Z on the desktop: every change made here remembers how to take it back (moving icons, renaming,
/// new items, pasting or dropping files, moving into a folder, deleting to the Recycle Bin).
/// </summary>
public sealed partial class DesktopWindow
{
    private sealed record UndoStep(string What, Action Undo);

    private const int MaxUndo = 30;
    private readonly LinkedList<UndoStep> _undo = new();

    private void Remember(string what, Action undo)
    {
        _undo.AddLast(new UndoStep(what, undo));
        if (_undo.Count > MaxUndo) _undo.RemoveFirst();
    }

    /// <summary>"Undo Move" etc., for the menu; null when there is nothing to undo.</summary>
    private string? UndoText => _undo.Last is { } last ? $"Undo {last.Value.What}" : null;

    private void UndoLast()
    {
        if (_undo.Last is not { } last) return;
        _undo.RemoveLast();
        try { last.Value.Undo(); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not undo: {ex.Message}", "Desktop", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Refresh();
    }

    // ---------- What the steps do ----------

    private const ushort FOF_NOCONFIRMATION = 0x10;

    /// <summary>Icons moved on the desktop: put them back where they were.</summary>
    private void RememberPositions(string what, Dictionary<string, IconPosition?> before) => Remember(what, () =>
    {
        foreach (var (path, position) in before)
        {
            if (position == null) Layout.Positions.Remove(path);
            else Layout.Positions[path] = position;
        }
        DesktopLayout.SaveSoon();
        Arrange();
    });

    /// <summary>Things that were made or copied here: to the Recycle Bin (no question, like Explorer's undo).</summary>
    private void RememberCreated(string what, IReadOnlyList<string> created) => Remember(what, () =>
    {
        var still = created.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (still.Count > 0) FileOperation(FO_DELETE, still, null, (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION));
    });

    /// <summary>Things that were moved: each back to the folder it came from (moves = (from, to) full paths).</summary>
    private void RememberMoved(string what, IReadOnlyList<(string From, string To)> moves) => Remember(what, () =>
    {
        foreach (var group in moves.Where(m => File.Exists(m.To) || Directory.Exists(m.To))
                                   .GroupBy(m => Path.GetDirectoryName(m.From)!, StringComparer.OrdinalIgnoreCase))
            FileOperation(FO_MOVE, group.Select(m => m.To), group.Key, FOF_ALLOWUNDO);
    });

    private void RememberRename(string from, string to) => Remember("Rename", () =>
    {
        if (Directory.Exists(to)) Directory.Move(to, from);
        else File.Move(to, from);
        if (Layout.Positions.Remove(to, out var position)) Layout.Positions[from] = position;
    });

    /// <summary>Deleted to the Recycle Bin: restore them from there.</summary>
    private void RememberDeleted(IReadOnlyList<string> deleted) => Remember("Delete", () =>
    {
        int missing = deleted.Count(path => !SpecialIcons.Restore(path));
        if (missing > 0)
            MessageBox.Show(this, $"{missing} item(s) are no longer in the Recycle Bin.", "Undo Delete", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    /// <summary>The (from, to) pairs of a move into a folder, by name.</summary>
    private static List<(string From, string To)> MovedInto(IEnumerable<string> sources, string folder) =>
        sources.Select(s => (s, Path.Combine(folder, Path.GetFileName(s)))).ToList();
}
