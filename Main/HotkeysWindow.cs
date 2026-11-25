using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// "Keyboard shortcuts": every shortcut of Windows Customizer in one list. Click a box and press the keys;
/// Backspace or Delete turns it off; "Default" puts the original back. Used at once (and saved).
/// </summary>
internal sealed class HotkeysWindow : SettingsForm
{
    private readonly Hotkeys _hotkeys;
    private readonly Action _changed;
    private readonly List<Action> _refresh = new();

    /// <param name="changed">Save and register the shortcuts again.</param>
    public HotkeysWindow(Hotkeys hotkeys, Action changed)
        : base("Keyboard shortcuts", () => { }, () => { })
    {
        _hotkeys = hotkeys;
        _changed = changed;
        foreach (var group in Hotkeys.All.GroupBy(i => i.Group))
        {
            Header(group.Key);
            foreach (var info in group) Add(Row(info));
        }
        Header("Fixed");
        Hint("The Windows key (start menu or search) and Alt+Tab are set on the Start menu page; Windows' own shortcuts " +
             "(Win+E, Win+L, ...) can't be taken. Shortcuts need Ctrl, Alt or Win, so a plain key keeps working in other programs.");
        Closed += (_, _) => _changed(); // in case a box still had the keys released
    }

    private UIElement Row(Hotkeys.Info info)
    {
        var box = new TextBox
        {
            Width = 170, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Click, then press the keys (Backspace: none)",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, info.Name);
        var status = FluentLook.Secondary(new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        void Show()
        {
            var current = _hotkeys.Get(info.Action);
            box.Text = current.IsNone ? "(none)" : current.ToString();
            bool taken = _hotkeys.Taken.Contains(info.Action);
            status.Text = taken ? "Another program already uses this; pick other keys." : current == info.Default ? "" : $"Default: {(info.Default.IsNone ? "none" : info.Default.ToString())}";
            status.Foreground = taken ? Brushes.IndianRed : status.Foreground;
            if (!taken) FluentLook.Secondary(status);
        }
        _refresh.Add(Show);

        // While typing a shortcut, the registered ones are released (so pressing one doesn't run it).
        box.GotKeyboardFocus += (_, _) => { _hotkeys.Release(); box.Text = "Press the keys..."; };
        box.LostKeyboardFocus += (_, _) => { _changed(); foreach (var r in _refresh) r(); };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Tab or Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key is Key.Back or Key.Delete) { Set(Shortcut.None); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var shortcut = new Shortcut(Keyboard.Modifiers, key);
            if (!shortcut.IsUsable) { box.Text = "Add Ctrl, Alt or Win"; return; }
            // The same keys elsewhere: that one is turned off (one action per shortcut).
            foreach (var other in Hotkeys.All.Where(o => o.Action != info.Action && _hotkeys.Get(o.Action) == shortcut))
                _hotkeys.Set(other.Action, Shortcut.None);
            Set(shortcut);
        };
        void Set(Shortcut shortcut)
        {
            _hotkeys.Set(info.Action, shortcut);
            box.Text = shortcut.IsNone ? "(none)" : shortcut.ToString();
            Keyboard.ClearFocus(); // registers them again (LostKeyboardFocus)
        }

        var reset = new Wpf.Ui.Controls.Button { Content = "Default", Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += (_, _) => { _hotkeys.Set(info.Action, info.Default); _changed(); foreach (var r in _refresh) r(); };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(box);
        right.Children.Add(reset);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = info.Name, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(status);
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);
        Show();
        return new Wpf.Ui.Controls.Card { Content = row, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(14, 10, 14, 10) };
    }
}
