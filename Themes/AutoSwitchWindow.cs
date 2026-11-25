using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// "Rules..." on the Profiles page: the list of automatic profile switches. Each row: when (a time, or while an
/// app runs), the time or app, and the profile. Saves after every change.
/// </summary>
internal sealed class AutoSwitchWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<AutoSwitchRule> _rules;
    private readonly IReadOnlyList<string> _profiles;
    private readonly Action _save;
    private readonly StackPanel _rows = new();

    public AutoSwitchWindow(List<AutoSwitchRule> rules, IReadOnlyList<string> profiles, Action save)
    {
        _rules = rules;
        _profiles = profiles;
        _save = save;
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var intro = FluentLook.Secondary(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Time rules switch at that time each day and stay until the next one (for example School at 08:00, Night at 21:00). " +
                   "App rules switch while that program runs and go back afterwards; they win over time rules, the top one first. " +
                   "A profile you pick yourself stays until the next rule kicks in.",
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var addTime = new Wpf.Ui.Controls.Button { Content = "Add a time rule", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Clock24), Margin = new Thickness(0, 0, 8, 0) };
        addTime.Click += (_, _) => Add(new AutoSwitchRule { When = AutoSwitchWhen.Time, At = "21:00", Profile = _profiles.FirstOrDefault() ?? "" });
        var addApp = new Wpf.Ui.Controls.Button { Content = "Add an app rule", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Games24), Margin = new Thickness(0, 0, 8, 0) };
        addApp.Click += (_, _) => Add(new AutoSwitchRule { When = AutoSwitchWhen.AppRunning, App = "", Profile = _profiles.FirstOrDefault() ?? "" });
        var close = new Wpf.Ui.Controls.Button { Content = "Close", MinWidth = 90 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(addTime);
        buttons.Children.Add(addApp);
        var bottom = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(buttons);

        var body = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(intro);
        body.Children.Add(bottom);
        body.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        FluentLook.Apply(this, "Switch profiles automatically", body);
        Fill();
    }

    private void Add(AutoSwitchRule rule)
    {
        _rules.Add(rule);
        _save();
        Fill();
    }

    private void Fill()
    {
        SafeUi.Clear(_rows);
        if (_rules.Count == 0)
            _rows.Children.Add(FluentLook.Secondary(new TextBlock { Text = "No rules yet. Add one below.", Margin = new Thickness(4, 8, 0, 0) }));
        for (int i = 0; i < _rules.Count; i++) _rows.Children.Add(Row(_rules[i], i));
    }

    /// <summary>One rule as a card: icon, "At [21:00]" or "While [app] runs", "use" [profile], move up, remove.</summary>
    private UIElement Row(AutoSwitchRule rule, int index)
    {
        var row = new DockPanel();
        bool time = rule.When == AutoSwitchWhen.Time;
        var icon = new Wpf.Ui.Controls.SymbolIcon(time ? Wpf.Ui.Controls.SymbolRegular.Clock24 : Wpf.Ui.Controls.SymbolRegular.Games24)
        { FontSize = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        // Right side: move up, remove.
        var remove = new Wpf.Ui.Controls.Button { Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Delete24), ToolTip = "Remove", Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { _rules.Remove(rule); _save(); Fill(); };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        if (index > 0)
        {
            var up = new Wpf.Ui.Controls.Button { Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowUp24), ToolTip = "Move up (app rules: checked first)", Margin = new Thickness(6, 0, 0, 0) };
            up.Click += (_, _) => { _rules.RemoveAt(index); _rules.Insert(index - 1, rule); _save(); Fill(); };
            DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(up);
        }

        var line = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(Label(time ? "At" : "While"));
        if (time)
        {
            var box = new TextBox { Text = rule.At ?? "", Width = 80, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "24-hour time, like 08:00 or 21:30" };
            var normal = box.BorderBrush;
            box.TextChanged += (_, _) =>
            {
                bool ok = AutoSwitchRule.TryParseTime(box.Text, out _);
                box.BorderBrush = ok ? normal : Brushes.IndianRed;
                if (!ok) return;
                rule.At = box.Text.Trim();
                _save();
            };
            line.Children.Add(box);
        }
        else
        {
            // Programs with a window right now, to pick from; any name can be typed too.
            // The list is filled when it opens (once per window): looking at every process takes a moment.
            var apps = new ComboBox { IsEditable = true, Width = 200, Text = rule.App ?? "", ToolTip = "The program's name without .exe" };
            apps.DropDownOpened += (_, _) =>
            {
                if (apps.ItemsSource != null) return;
                string typed = apps.Text;
                apps.ItemsSource = _runningApps ??= RunningApps();
                apps.Text = typed;
            };
            void SetApp() { rule.App = apps.Text.Trim(); _save(); }
            apps.SelectionChanged += (_, _) => { if (apps.SelectedItem is string s) { apps.Text = s; SetApp(); } };
            apps.LostFocus += (_, _) => SetApp();
            line.Children.Add(apps);
            line.Children.Add(Label("runs"));
        }
        line.Children.Add(Label(", use"));
        var profile = new ComboBox { Width = 180, ItemsSource = _profiles, SelectedItem = _profiles.FirstOrDefault(p => p.Equals(rule.Profile, StringComparison.CurrentCultureIgnoreCase)) };
        if (profile.SelectedItem == null && !string.IsNullOrEmpty(rule.Profile)) profile.ToolTip = $"\"{rule.Profile}\" doesn't exist any more";
        profile.SelectionChanged += (_, _) => { if (profile.SelectedItem is string name) { rule.Profile = name; _save(); } };
        line.Children.Add(profile);
        row.Children.Add(line);

        return new Wpf.Ui.Controls.Card { Content = row, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(14, 10, 14, 10) };
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    private List<string>? _runningApps;

    /// <summary>Names of programs that have a window open, sorted.</summary>
    private static List<string> RunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName); }
                catch (InvalidOperationException) { /* ended */ }
            }
        }
        return names.ToList();
    }
}
