using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// Pinning and ordering on the bar itself: pin a window's program (right-click), unpin a pinned app, drag pinned apps
/// and window buttons to reorder them, and drop programs, shortcuts, files or folders on the bar to pin them.
/// Pins live in the design (saved through <see cref="SaveDesign"/>); the window order in the settings.
/// </summary>
public partial class TaskbarView
{
    /// <summary>Saves a changed design (pins added, removed or moved); set by the main window.</summary>
    public static Action<TaskbarDesign>? SaveDesign { get; set; }

    /// <summary>
    /// The order you dragged window buttons into, by program (the same for every bar). Programs not in it come
    /// after, in the order their windows opened.
    /// </summary>
    public static List<string> WindowOrder { get; set; } = new();

    /// <summary>Called after <see cref="WindowOrder"/> changed, to save it; set by the main window.</summary>
    public static Action? WindowOrderChanged { get; set; }

    // ---------- Pins ----------

    /// <summary>The design's first pinned-apps module (one is added after the menu/start button when there's none).</summary>
    private TaskbarModule PinModule()
    {
        var pins = _design.AllModules.FirstOrDefault(m => m.Type == ModuleType.PinnedApps);
        if (pins != null) return pins;
        pins = TaskbarModule.Create(ModuleType.PinnedApps);
        var zone = _design.Left.Count > 0 || _design.Center.Count == 0 ? _design.Left : _design.Center;
        zone.Insert(zone.FindIndex(m => m.Type is ModuleType.Menu or ModuleType.Start) + 1, pins);
        return pins;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Environment.ExpandEnvironmentVariables(a.Trim()), Environment.ExpandEnvironmentVariables(b.Trim()), StringComparison.OrdinalIgnoreCase);

    private bool IsPinned(string path) =>
        _design.AllModules.Where(m => m.Type == ModuleType.PinnedApps).Any(m => m.Apps?.Any(a => SamePath(a, path)) == true);

    /// <summary>Pins programs, shortcuts, files or folders (skipping ones already pinned) and saves the design.</summary>
    private void Pin(IEnumerable<string> paths)
    {
        var module = PinModule();
        module.Apps ??= new List<string>();
        bool changed = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || IsPinned(path)) continue;
            module.Apps.Add(path);
            changed = true;
        }
        if (changed) SaveDesign?.Invoke(_design);
    }

    private void Unpin(string path)
    {
        foreach (var module in _design.AllModules.Where(m => m.Type == ModuleType.PinnedApps))
            module.Apps?.RemoveAll(a => SamePath(a, path));
        SaveDesign?.Invoke(_design);
    }

    // ---------- Window buttons: right-click menu ----------

    /// <summary>Fills in "Pin to taskbar" / "Unpin from taskbar" for the window (or app group) the menu belongs to.</summary>
    private void WindowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var windows = WindowsOf(menu.DataContext);
        var pin = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "pin");
        if (pin == null) return;
        var program = windows.Select(w => w.ProcessPath).FirstOrDefault(p => p != null);
        bool storeApp = program == null || program.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        pin.IsEnabled = !storeApp;
        pin.Header = storeApp ? "Pin to taskbar (pin Store apps from the start menu)" : IsPinned(program!) ? "Unpin from taskbar" : "Pin to taskbar";
    }

    private void PinWindow_Click(object sender, RoutedEventArgs e)
    {
        var program = WindowsOf((sender as FrameworkElement)?.DataContext).Select(w => w.ProcessPath).FirstOrDefault(p => p != null);
        if (program == null) return;
        if (IsPinned(program)) Unpin(program);
        else Pin(new[] { program });
    }

    /// <summary>Right-click "Force close": ends the program of the window (or of each window in an app group).</summary>
    private void ForceCloseWindow_Click(object sender, RoutedEventArgs e) => ForceCloseAll(WindowsOf((sender as FrameworkElement)?.DataContext));

    private void ForceCloseAll(List<TaskWindow> windows)
    {
        CloseThumbnails();
        // One window per program is enough: the whole program ends.
        foreach (var window in windows.GroupBy(w => w.ProcessPath ?? w.Handle.ToString()).Select(g => g.First()))
            if (ForceClose.End(window.Handle) is { } problem)
                MessageBox.Show(problem, "Force close", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Dragging to reorder ----------
    //
    // Inside the bar itself (not Windows drag and drop): the button follows the mouse, the others slide aside to make
    // room, and letting go puts it there. Escape cancels.

    /// <summary>Which pinned app each pinned button is (kept apart from Tag, which the button style uses for its hover color).</summary>
    private readonly Dictionary<FrameworkElement, PinTag> _pinOf = new();

    /// <summary>A pinned button's module and the entry it shows.</summary>
    private sealed record PinTag(TaskbarModule Module, string Path);

    private sealed class Reorder
    {
        public required FrameworkElement Button;     // what was pressed
        public required FrameworkElement Item;       // the button's slot in the row (the button, or its container)
        public required Panel Row;
        public required List<FrameworkElement> Items; // the row's items, in order
        public required int From;
        public required double PressX;               // where the mouse went down, in the row
        public bool Started;
        public int To;
    }

    private Reorder? _reorder;
    private bool _swallowClick;

    /// <summary>True while a button is being dragged (thumbnails and hover previews wait).</summary>
    private bool IsReordering => _reorder?.Started == true;

    /// <summary>The element whose parent is the row panel: the button itself, or (window list) its item container.</summary>
    private static (FrameworkElement Item, Panel Row)? SlotOf(FrameworkElement button)
    {
        DependencyObject node = button;
        while (VisualTreeHelper.GetParent(node) is { } parent)
        {
            if (parent is Panel panel && node is FrameworkElement item) return (item, panel);
            node = parent;
        }
        return null;
    }

    private void DragSource_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement button || SlotOf(button) is not { } slot) return;
        var items = slot.Row.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
        int from = items.IndexOf(slot.Item);
        if (from < 0 || items.Count < 2) { _reorder = null; return; }
        _reorder = new Reorder
        {
            Button = button, Item = slot.Item, Row = slot.Row, Items = items, From = from, To = from,
            PressX = e.GetPosition(slot.Row).X,
        };
    }

    private void DragSource_PreviewMove(object sender, MouseEventArgs e)
    {
        var r = _reorder;
        if (r == null || sender != r.Button) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndReorder(apply: false); return; }
        double x = e.GetPosition(r.Row).X, dx = x - r.PressX;
        if (!r.Started)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
            r.Started = true;
            CloseThumbnails();
            _openMenus++; // an auto-hiding bar stays in while dragging
            r.Button.CaptureMouse();
            Panel.SetZIndex(r.Item, 100);
            r.Item.Opacity = 0.85;
            foreach (var item in r.Items) item.RenderTransform = new TranslateTransform();
        }

        // The dragged button follows the mouse (kept within the row).
        var slot = LayoutInformation.GetLayoutSlot(r.Item);
        double min = LayoutInformation.GetLayoutSlot(r.Items[0]).Left - slot.Left;
        double max = LayoutInformation.GetLayoutSlot(r.Items[^1]).Right - slot.Right;
        ((TranslateTransform)r.Item.RenderTransform).X = Math.Clamp(dx, min, max);

        // Where it would go: past the middle of a neighbour, that neighbour moves over.
        double center = slot.Left + slot.Width / 2 + Math.Clamp(dx, min, max);
        int to = 0;
        for (int i = 0; i < r.Items.Count; i++)
        {
            if (i == r.From) continue;
            var other = LayoutInformation.GetLayoutSlot(r.Items[i]);
            if (center > other.Left + other.Width / 2) to = i < r.From ? i + 1 : i;
        }
        to = Math.Clamp(to, 0, r.Items.Count - 1);
        if (to == r.To) return;
        r.To = to;

        // The others slide aside, smoothly, by the dragged button's width.
        for (int i = 0; i < r.Items.Count; i++)
        {
            if (i == r.From) continue;
            double shift = (r.From < to && i > r.From && i <= to) ? -slot.Width
                         : (to < r.From && i >= to && i < r.From) ? slot.Width : 0;
            var move = (TranslateTransform)r.Items[i].RenderTransform;
            move.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(shift, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
        }
    }

    private void DragSource_PreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (_reorder?.Started == true)
        {
            EndReorder(apply: true);
            e.Handled = true; // a drag isn't a click
        }
        else _reorder = null;
    }

    private void DragSource_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsReordering) { EndReorder(apply: false); e.Handled = true; }
    }

    private void DragSource_LostCapture(object sender, MouseEventArgs e)
    {
        if (IsReordering) EndReorder(apply: false);
    }

    /// <summary>Puts everything back in place, and (when let go normally) applies the new order.</summary>
    private void EndReorder(bool apply)
    {
        var r = _reorder;
        _reorder = null;
        if (r == null || !r.Started) return;
        _openMenus = Math.Max(0, _openMenus - 1);
        if (r.Button.IsMouseCaptured) r.Button.ReleaseMouseCapture();
        foreach (var item in r.Items)
        {
            item.RenderTransform = Transform.Identity;
            Panel.SetZIndex(item, 0);
        }
        r.Item.Opacity = 1;
        if (!apply || r.To == r.From) return;

        var order = r.Items.ToList();
        order.RemoveAt(r.From);
        order.Insert(r.To, r.Item);

        if (_pinOf.TryGetValue(r.Button, out var dragged))
        {
            // Pinned apps: the module's list in the new order (entries not shown, like broken paths, stay at the end).
            var shown = order.Select(i => _pinOf.TryGetValue(i, out var tag) ? tag.Path : null).OfType<string>().ToList();
            var apps = dragged.Module.Apps ??= new List<string>();
            var rest = apps.Where(a => !shown.Contains(a)).ToList();
            apps.Clear();
            apps.AddRange(shown.Concat(rest));
            // Moved right away in the row, so it stays where it was dropped while the saved design reloads.
            if (r.Row.Children.Count == order.Count)
            {
                r.Row.Children.Clear();
                foreach (var item in order) r.Row.Children.Add(item);
            }
            SaveDesign?.Invoke(_design);
        }
        else if (_items != null)
        {
            // Window buttons: remembered by program.
            var keys = order.Select(i => i.DataContext is TaskItem t ? TaskItemList.KeyOf(t) : null).OfType<string>().ToList();
            WindowOrder = keys.Concat(WindowOrder.Where(k => !keys.Contains(k))).Take(300).ToList();
            _items.Rebuild();
            WindowOrderChanged?.Invoke();
        }
    }

    /// <summary>Makes a pinned button draggable within its row.</summary>
    private void AttachReorder(FrameworkElement button)
    {
        button.PreviewMouseLeftButtonDown += DragSource_PreviewDown;
        button.PreviewMouseMove += DragSource_PreviewMove;
        button.PreviewMouseLeftButtonUp += DragSource_PreviewUp;
        button.LostMouseCapture += DragSource_LostCapture;
        button.PreviewKeyDown += DragSource_KeyDown;
    }

    // ---------- Dropping files on the bar ----------

    /// <summary>Programs, shortcuts, files and folders dragged from the desktop or File Explorer can be dropped to pin them.</summary>
    private void SetUpDropToPin()
    {
        Surface.AllowDrop = true;
        Surface.DragOver += (_, e) =>
        {
            if (e.Handled) return;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Surface.Drop += (_, e) =>
        {
            if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            Pin(paths.Where(p => File.Exists(p) || Directory.Exists(p)));
            e.Handled = true;
        };
    }
}
