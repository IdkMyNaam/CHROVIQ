using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// "Snap settings...": how snapping starts (drag to the top, Shift-drag, hotkeys), the gap, which layout each
/// monitor uses, and an editor for your own layouts (columns with a width, each split into rows).
/// </summary>
internal sealed class SnapSettingsWindow : SettingsForm
{
    private readonly SnapSettings _settings;
    private readonly Action _save;
    private readonly StackPanel _editor = new();
    private SnapLayout? _editing;

    public SnapSettingsWindow(SnapSettings settings, Action save, Action tryIt)
        : base("Snap settings", save, tryIt)
    {
        _settings = settings;
        _save = save;

        Header("How");
        Check("Drag a window to the top of a screen to pick a layout", settings.PickerOnDragToTop, v => settings.PickerOnDragToTop = v);
        Check("Hold Shift while dragging to see the zones", settings.ZonesOnShiftDrag, v => settings.ZonesOnShiftDrag = v);
        Check("Hotkeys: Win+Alt+Left/Right and Win+Alt+Z", settings.Hotkeys, v => settings.Hotkeys = v);
        Hint("Win+Alt+Left/Right moves the active window to the previous/next zone of its screen's layout (past the last one, " +
             "on to the next screen). Win+Alt+Z shows the picker for it. \"Try it\" shows the picker for this window.");
        Slider("Space between zones (pixels)", settings.Gap, 0, 40, v => settings.Gap = Math.Round(v));
        Hint("Windows has its own layout bar when you drag to the top. With both on you'd see two: turn Windows' off in " +
             "Settings > System > Multitasking > Snap windows (\"Show snap layouts when I drag a window to the top of my screen\").");

        Header("Monitors");
        var names = settings.AllLayouts().Select(l => l.Name).ToArray();
        int number = 1;
        foreach (var monitor in Monitors.All())
        {
            string device = monitor.Device;
            int current = Array.FindIndex(names, n => n.Equals(settings.LayoutFor(device).Name, StringComparison.CurrentCultureIgnoreCase));
            Combo($"Monitor {number++}{(monitor.IsPrimary ? " (main)" : "")}", names, Math.Max(0, current), i => settings.MonitorLayouts[device] = names[i]);
        }
        Hint("The layout used when you hold Shift while dragging, and by the hotkeys. The picker always shows every layout. " +
             "Reopen this window after adding a layout to choose it here.");

        Header("Your layouts");
        Add(_editor);
        FillEditor();
    }

    // ---------- Layout editor ----------

    private void FillEditor()
    {
        SafeUi.Clear(_editor);
        var own = _settings.Layouts;

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var pick = new ComboBox { Width = 220, ItemsSource = own.Select(l => l.Name).ToList(), Margin = new Thickness(0, 0, 8, 0) };
        if (_editing != null) pick.SelectedIndex = own.IndexOf(_editing);
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedIndex < 0) return;
            _editing = own[pick.SelectedIndex];
            Dispatcher.BeginInvoke(FillEditor); // after the dropdown has closed (see the designer's crash)
        };
        bar.Children.Add(pick);
        var add = new Wpf.Ui.Controls.Button { Content = "New layout", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Add24), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            var copy = (_editing ?? SnapLayout.BuiltIns[2]).Copy(FreeName());
            own.Add(copy);
            _editing = copy;
            _save();
            FillEditor();
        };
        bar.Children.Add(add);
        if (_editing != null)
        {
            var delete = new Wpf.Ui.Controls.Button { Content = "Delete", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Delete24) };
            delete.Click += (_, _) =>
            {
                own.Remove(_editing);
                foreach (var key in _settings.MonitorLayouts.Where(kv => kv.Value == _editing.Name).Select(kv => kv.Key).ToList())
                    _settings.MonitorLayouts.Remove(key);
                _editing = null;
                _save();
                FillEditor();
            };
            bar.Children.Add(delete);
        }
        _editor.Children.Add(bar);

        if (_editing == null)
        {
            _editor.Children.Add(FluentLook.Secondary(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = own.Count == 0 ? "No layouts of your own yet. \"New layout\" starts from \"Main and side\"; change its columns below."
                                      : "Choose one of your layouts to change it, or make a new one.",
            }));
            return;
        }

        var layout = _editing;
        var preview = new Canvas { Width = 320, Height = 180, Margin = new Thickness(0, 4, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        void DrawPreview()
        {
            preview.Children.Clear();
            preview.Children.Add(new Border { Width = 320, Height = 180, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) });
            int n = 1;
            foreach (var z in layout.Zones())
            {
                var box = new Border
                {
                    Width = z.Width * 320 - 6, Height = z.Height * 180 - 6, CornerRadius = new CornerRadius(5),
                    Background = SnapColors.Fill(0x70), BorderBrush = SnapColors.Fill(0xFF), BorderThickness = new Thickness(1.5),
                    Child = new TextBlock { Text = (n++).ToString(), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                Canvas.SetLeft(box, z.Left * 320 + 3);
                Canvas.SetTop(box, z.Top * 180 + 3);
                preview.Children.Add(box);
            }
        }

        var name = new TextBox { Text = layout.Name, Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        name.LostFocus += (_, _) =>
        {
            string newName = name.Text.Trim();
            if (newName.Length == 0 || newName == layout.Name) return;
            if (_settings.AllLayouts().Any(l => l != layout && l.Name.Equals(newName, StringComparison.CurrentCultureIgnoreCase))) { name.Text = layout.Name; return; }
            foreach (var key in _settings.MonitorLayouts.Where(kv => kv.Value == layout.Name).Select(kv => kv.Key).ToList())
                _settings.MonitorLayouts[key] = newName;
            layout.Name = newName;
            _save();
        };
        _editor.Children.Add(name);
        _editor.Children.Add(preview);
        DrawPreview();

        // One row per column: its width and how many zones it's split into.
        for (int i = 0; i < layout.Columns.Count; i++)
        {
            var column = layout.Columns[i];
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(Label($"Column {i + 1}"));
            var width = new Slider { Minimum = 1, Maximum = 6, Value = column.Width, Width = 140, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            width.ValueChanged += (_, _) => { column.Width = width.Value; _save(); DrawPreview(); };
            row.Children.Add(Label("width"));
            row.Children.Add(width);
            row.Children.Add(Label("  zones"));
            var rows = new ComboBox { ItemsSource = new[] { 1, 2, 3, 4 }, SelectedItem = Math.Clamp(column.Rows, 1, 4), Width = 70 };
            rows.SelectionChanged += (_, _) => { if (rows.SelectedItem is int r) { column.Rows = r; _save(); DrawPreview(); } };
            row.Children.Add(rows);
            if (layout.Columns.Count > 1)
            {
                var remove = new Wpf.Ui.Controls.Button { Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Delete24), ToolTip = "Remove this column", Margin = new Thickness(8, 0, 0, 0) };
                remove.Click += (_, _) => { layout.Columns.Remove(column); _save(); Dispatcher.BeginInvoke(FillEditor); };
                row.Children.Add(remove);
            }
            _editor.Children.Add(row);
        }
        var addColumn = new Wpf.Ui.Controls.Button { Content = "Add a column", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Add24), Margin = new Thickness(0, 4, 0, 0) };
        addColumn.IsEnabled = layout.Columns.Count < 6;
        addColumn.Click += (_, _) => { layout.Columns.Add(new SnapColumn()); _save(); FillEditor(); };
        _editor.Children.Add(addColumn);
        _editor.Children.Add(FluentLook.Secondary(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "Widths are shares: 1, 2, 1 makes 25 %, 50 %, 25 %. Each column can be split into up to 4 zones from top to bottom.",
        }));
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MinWidth = 20 };

    private string FreeName()
    {
        for (int n = 1; ; n++)
        {
            string name = $"My layout {n}";
            if (!_settings.AllLayouts().Any(l => l.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) return name;
        }
    }
}
