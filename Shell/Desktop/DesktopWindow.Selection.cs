using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// Selecting icons like on Windows' desktop: click, Ctrl+click, Shift+click, a selection box dragged over the
/// empty desktop, Ctrl+A, arrow keys; and moving the selected icons by dragging them.
/// </summary>
public sealed partial class DesktopWindow
{
    private readonly HashSet<DesktopItem> _selection = new();
    private DesktopItem? _anchor; // where Shift+click ranges start
    private DesktopItem? _focus;  // the icon the arrow keys move from

    private readonly Border _band = new()
    {
        Background = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x3A, 0x8D, 0xDE))),
        BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x3A, 0x8D, 0xDE))),
        BorderThickness = new Thickness(1),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    // The current mouse gesture (from button down to button up).
    private Point? _pressAt;
    private DesktopItem? _pressItem;      // null: pressed on the empty desktop
    private bool _selectOnlyOnUp;         // pressed on an already selected icon: a click (not a drag) selects only it
    private bool _moving, _banding;
    private HashSet<DesktopItem> _bandStart = new();
    private readonly Dictionary<DesktopItem, Point> _moveStart = new();

    private void SetUpSelection()
    {
        Panel.SetZIndex(_band, 10_000);
        _iconArea.Children.Add(_band);

        _iconArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != _iconArea) return; // an icon handles its own clicks
            Activate();
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!ctrl) SelectOnly(null);
            _bandStart = new HashSet<DesktopItem>(_selection);
            BeginGesture(e.GetPosition(_iconArea), null);
            e.Handled = true;
        };
        _iconArea.MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource != _iconArea) return;
            SelectOnly(null);
            ShowDesktopMenu(e.GetPosition(_iconArea));
            e.Handled = true;
        };
        _iconArea.MouseMove += (_, e) => OnGestureMove(e.GetPosition(_iconArea));
        _iconArea.MouseLeftButtonUp += (_, e) => { EndGesture(e.GetPosition(_iconArea)); e.Handled = true; };
        _iconArea.LostMouseCapture += (_, _) => CancelGesture();
    }

    // ---------- Selection ----------

    /// <summary>Selects just this icon (or nothing).</summary>
    private void SelectOnly(DesktopItem? item)
    {
        foreach (var selected in _selection.ToList()) SetSelected(selected, false);
        if (item != null) SetSelected(item, true);
        _anchor = _focus = item;
    }

    private void SetSelected(DesktopItem item, bool selected)
    {
        if (selected) _selection.Add(item); else _selection.Remove(item);
        item.Visual.Background = selected ? SelectedBrush : item.Visual.IsMouseOver ? HoverBrush : Brushes.Transparent;
    }

    /// <summary>The icons in reading order of the desktop: column by column, top to bottom.</summary>
    private List<DesktopItem> InDesktopOrder() =>
        _items.OrderBy(i => Math.Round(Canvas.GetLeft(i.Visual))).ThenBy(i => Canvas.GetTop(i.Visual)).ToList();

    /// <summary>Shift+click: everything from the anchor to this icon, in desktop order (Ctrl keeps what was selected).</summary>
    private void SelectRange(DesktopItem from, DesktopItem to, bool keep)
    {
        var order = InDesktopOrder();
        int a = order.IndexOf(from), b = order.IndexOf(to);
        if (a < 0 || b < 0) { SelectOnly(to); return; }
        if (!keep) foreach (var selected in _selection.ToList()) SetSelected(selected, false);
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) SetSelected(order[i], true);
        _focus = to;
    }

    private void SelectAll()
    {
        foreach (var item in _items) SetSelected(item, true);
        _anchor ??= _items.FirstOrDefault();
    }

    /// <summary>The selected icons, in desktop order.</summary>
    private List<DesktopItem> SelectedItems() => InDesktopOrder().Where(_selection.Contains).ToList();

    // ---------- Clicks on icons ----------

    private void OnItemMouseDown(DesktopItem item, MouseButtonEventArgs e)
    {
        Activate(); // keyboard keys (Enter, Delete, F2, arrows) need the desktop focused
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            if (!_selection.Contains(item)) SelectOnly(item);
            OpenItems(SelectedItems());
            return;
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _selectOnlyOnUp = false;
        if (shift && _anchor != null && _items.Contains(_anchor)) SelectRange(_anchor, item, keep: ctrl);
        else if (ctrl) { SetSelected(item, !_selection.Contains(item)); _anchor = _focus = item; }
        else if (!_selection.Contains(item)) SelectOnly(item);
        else { _selectOnlyOnUp = true; _focus = item; } // maybe the start of moving all selected icons

        // Ctrl+click that just deselected an icon doesn't start a move.
        BeginGesture(e.GetPosition(_iconArea), _selection.Contains(item) ? item : null);
        if (_pressItem == null) _bandStart = new HashSet<DesktopItem>(_selection);
    }

    private void OnItemRightClick(DesktopItem item, MouseButtonEventArgs e)
    {
        if (!_selection.Contains(item)) SelectOnly(item);
        ShowItemMenu(SelectedItems());
        e.Handled = true;
    }

    // ---------- Dragging: selection box or moving icons ----------

    private void BeginGesture(Point at, DesktopItem? item)
    {
        _pressAt = at;
        _pressItem = item;
        _moving = _banding = false;
        _iconArea.CaptureMouse();
    }

    private void OnGestureMove(Point at)
    {
        if (_pressAt is not { } start || Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (!_moving && !_banding)
        {
            if (Math.Abs(at.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(at.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (_pressItem != null)
            {
                if (Layout.AutoArrange) return; // with auto-arrange on, icons can't be placed by hand (like Windows)
                _moving = true;
                _moveStart.Clear();
                foreach (var item in _selection)
                {
                    _moveStart[item] = new Point(Canvas.GetLeft(item.Visual), Canvas.GetTop(item.Visual));
                    Panel.SetZIndex(item.Visual, 1000);
                    item.Visual.Opacity = 0.75;
                }
            }
            else _banding = true;
        }

        if (_moving)
        {
            // Out of the desktop (over File Explorer, an app, the taskbar): Windows' own drag takes over.
            if (MouseOverOtherWindow()) { StartDragOut(); return; }
            var delta = at - start;
            foreach (var (item, from) in _moveStart)
            {
                Canvas.SetLeft(item.Visual, from.X + delta.X);
                Canvas.SetTop(item.Visual, from.Y + delta.Y);
            }
            SetDropTarget(DropTargetAt(at, _moveStart.Keys.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        else UpdateBand(start, at);
    }

    private void UpdateBand(Point from, Point to)
    {
        var box = new Rect(from, to);
        Canvas.SetLeft(_band, box.X);
        Canvas.SetTop(_band, box.Y);
        _band.Width = box.Width;
        _band.Height = box.Height;
        _band.Visibility = Visibility.Visible;

        foreach (var item in _items)
        {
            if (item.Visual.Visibility != Visibility.Visible) continue;
            var bounds = new Rect(Canvas.GetLeft(item.Visual), Canvas.GetTop(item.Visual), item.Visual.Width, item.Visual.Height);
            bool inBox = box.IntersectsWith(bounds);
            bool selected = _bandStart.Contains(item) || inBox;
            if (selected != _selection.Contains(item)) SetSelected(item, selected);
        }
    }

    private void EndGesture(Point at)
    {
        if (_pressAt is not { } start) return;
        if (_moving && _dropTarget is { } target)
        {
            // Dropped onto a folder, the Recycle Bin or a program.
            var files = _moveStart.Keys.Where(i => !i.IsSpecial).Select(i => i.Path).ToList();
            var keys = DragDropKeyStates.None;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) keys |= DragDropKeyStates.ControlKey;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) keys |= DragDropKeyStates.ShiftKey;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) keys |= DragDropKeyStates.AltKey;
            var effect = ChooseEffect(keys, DragDropEffects.All, files, target, ownIcons: true);
            CancelGesture(); // icons back first; the drop then moves/deletes them
            Dispatcher.BeginInvoke(() => PerformDrop(files, target, effect, null, ownIcons: true));
            return;
        }
        if (_moving) DropMoved(at - start);
        else if (!_banding && _selectOnlyOnUp && _pressItem != null) SelectOnly(_pressItem);
        FinishGesture();
    }

    private void CancelGesture()
    {
        if (_moving) Arrange(); // back to where they were
        FinishGesture();
    }

    private void FinishGesture()
    {
        SetDropTarget(null);
        foreach (var item in _moveStart.Keys) { Panel.SetZIndex(item.Visual, 0); item.Visual.Opacity = 1; }
        _moveStart.Clear();
        _band.Visibility = Visibility.Collapsed;
        _pressAt = null;
        _pressItem = null;
        _moving = _banding = false;
        _selectOnlyOnUp = false;
        if (_iconArea.IsMouseCaptured) _iconArea.ReleaseMouseCapture();
    }

    /// <summary>
    /// The moved icons get their new positions: with "align to grid" the nearest free cell (other icons stay
    /// where they are), otherwise exactly where they were dropped.
    /// </summary>
    private void DropMoved(Vector delta)
    {
        var (cellWidth, cellHeight) = Layout.Cell;
        int rows = Math.Max(1, (int)(_iconArea.ActualHeight / cellHeight));
        int columns = Math.Max(1, (int)(_iconArea.ActualWidth / cellWidth));
        (int, int) CellOf(double x, double y) => ((int)Math.Round(x / cellWidth), (int)Math.Round(y / cellHeight));

        var taken = new HashSet<(int, int)>();
        foreach (var other in _items.Where(i => !_moveStart.ContainsKey(i)))
            if (Layout.Positions.TryGetValue(other.Path, out var p)) taken.Add(CellOf(p.X, p.Y));

        RememberPositions("Move", _moveStart.Keys.ToDictionary(i => i.Path,
            i => Layout.Positions.TryGetValue(i.Path, out var p) ? p : null, StringComparer.OrdinalIgnoreCase));

        // In desktop order, so a group keeps its shape as far as the free cells allow.
        foreach (var (item, from) in _moveStart.OrderBy(m => m.Value.X).ThenBy(m => m.Value.Y))
        {
            double x = Math.Clamp(from.X - 2 + delta.X, 0, Math.Max(0, (columns - 1) * cellWidth));
            double y = Math.Clamp(from.Y - 2 + delta.Y, 0, Math.Max(0, (rows - 1) * cellHeight));
            if (Layout.AlignToGrid)
            {
                var cell = CellOf(x, y);
                cell = (Math.Clamp(cell.Item1, 0, columns - 1), Math.Clamp(cell.Item2, 0, rows - 1));
                for (int i = cell.Item1 * rows + cell.Item2; taken.Contains(cell); i++) cell = (i / rows, i % rows);
                taken.Add(cell);
                x = cell.Item1 * cellWidth;
                y = cell.Item2 * cellHeight;
            }
            Layout.Positions[item.Path] = PositionHere(x, y, Layout.Positions.GetValueOrDefault(item.Path));
        }
        DesktopLayout.SaveSoon();
        Arrange();
    }

    // ---------- Keyboard ----------

    /// <summary>Selection keys; true when the key was used.</summary>
    private bool HandleSelectionKey(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.A when ctrl: SelectAll(); return true;
            case Key.Escape: SelectOnly(null); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var next = Neighbor(_focus ?? _anchor, e.Key);
                if (next == null) return true;
                if (shift && _anchor != null) SelectRange(_anchor, next, keep: false);
                else SelectOnly(next);
                _focus = next;
                return true;
        }
        return false;
    }

    /// <summary>The nearest icon in that direction (the first icon if none has the focus yet).</summary>
    private DesktopItem? Neighbor(DesktopItem? from, Key direction)
    {
        if (from == null || !_items.Contains(from)) return InDesktopOrder().FirstOrDefault();
        Point Center(DesktopItem i) => new(Canvas.GetLeft(i.Visual) + i.Visual.Width / 2, Canvas.GetTop(i.Visual) + i.Visual.Height / 2);
        var origin = Center(from);
        DesktopItem? best = null;
        double bestScore = double.MaxValue;
        foreach (var item in _items)
        {
            if (item == from || item.Visual.Visibility != Visibility.Visible) continue;
            var d = Center(item) - origin;
            double along = direction switch { Key.Left => -d.X, Key.Right => d.X, Key.Up => -d.Y, _ => d.Y };
            double across = direction is Key.Left or Key.Right ? Math.Abs(d.Y) : Math.Abs(d.X);
            if (along <= 1) continue;
            double score = along + across * 3; // prefer icons straight in that direction
            if (score < bestScore) { bestScore = score; best = item; }
        }
        return best;
    }
}
