using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>Edits <see cref="SearchLook"/>: how the search window looks and behaves. Saves after every change.</summary>
public sealed class SearchSettingsWindow : Window
{
    private readonly SearchLook _look;
    private readonly Action _save;
    private readonly StackPanel _fields = new() { Margin = new Thickness(14) };

    public SearchSettingsWindow(SearchLook look, Action save)
    {
        _look = look;
        _save = save;
        Title = "Search settings";
        Width = 420;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tryIt = new Button { Content = "Try it", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 14, 14) };
        tryIt.Click += (_, _) => SearchWindow.Toggle(_look);
        var root = new DockPanel();
        DockPanel.SetDock(tryIt, Dock.Bottom);
        root.Children.Add(tryIt);
        root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Build();
    }

    private void Build()
    {
        Combo("Position (when opened by the Windows key)", new[] { "Center of the screen", "Top of the screen" },
            (int)_look.Position, i => _look.Position = (SearchPosition)i);
        Number("Width (pixels)", _look.Width, 240, 1600, v => _look.Width = v);
        Color("Background", _look.Background, v => _look.Background = v, "#RRGGBB, or #AARRGGBB to make it see-through.");
        Color("Text", _look.Foreground, v => _look.Foreground = v);
        Color("Selected result", _look.Highlight ?? "", v => _look.Highlight = string.IsNullOrWhiteSpace(v) ? null : v, "Empty = the Windows accent color.");
        Number("Corner radius", _look.CornerRadius, 0, 40, v => _look.CornerRadius = v);
        Font("Font", _look.FontFamily, v => _look.FontFamily = v);
        Number("Font size", _look.FontSize, 8, 40, v => _look.FontSize = v);
        Number("Number of app results", _look.MaxResults, 1, 30, v => _look.MaxResults = (int)v);
        Check("Offer \"Search the web for ...\"", _look.ShowWebSearch, v => _look.ShowWebSearch = v);
        Text("Web search address", _look.SearchUrl ?? "", v => _look.SearchUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            "{0} is replaced by what you typed. Empty = Google. Example: https://duckduckgo.com/?q={0}");
        Text("Placeholder text", _look.Placeholder, v => _look.Placeholder = v);
    }

    // ---------- Small field helpers ----------

    private void Label(string text) =>
        _fields.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 2) });

    private void Hint(string text) =>
        _fields.Children.Add(new TextBlock { Text = text, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });

    private void Text(string label, string value, Action<string> set, string? hint = null)
    {
        Label(label);
        var box = new TextBox { Text = value };
        box.TextChanged += (_, _) => { set(box.Text); _save(); };
        _fields.Children.Add(box);
        if (hint != null) Hint(hint);
    }

    private void Number(string label, double value, double min, double max, Action<double> set)
    {
        Label($"{label}  ({min:0}–{max:0})");
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.CurrentCulture) };
        var normal = box.BorderBrush;
        box.TextChanged += (_, _) =>
        {
            bool ok = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) && v >= min && v <= max;
            box.BorderBrush = ok ? normal : Brushes.IndianRed;
            if (!ok) return;
            set(v);
            _save();
        };
        _fields.Children.Add(box);
    }

    private void Color(string label, string value, Action<string> set, string? hint = null)
    {
        Label(label);
        var swatch = new Border { Width = 24, Height = 22, Margin = new Thickness(0, 0, 6, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var box = new TextBox { Text = value, VerticalContentAlignment = VerticalAlignment.Center };
        var normal = box.BorderBrush;
        void Show()
        {
            bool ok = TaskbarView.IsValidColor(box.Text);
            box.BorderBrush = ok ? normal : Brushes.IndianRed;
            swatch.Background = ok && !string.IsNullOrWhiteSpace(box.Text)
                ? new SolidColorBrush(TaskbarView.ParseColor(box.Text, Colors.Transparent)) : Brushes.Transparent;
        }
        Show();
        box.TextChanged += (_, _) => { Show(); set(box.Text.Trim()); _save(); };
        var row = new DockPanel();
        DockPanel.SetDock(swatch, Dock.Left);
        row.Children.Add(swatch);
        row.Children.Add(box);
        _fields.Children.Add(row);
        if (hint != null) Hint(hint);
    }

    /// <summary>A dropdown of the installed fonts (you can also type a name), like in the taskbar designer.</summary>
    private void Font(string label, string value, Action<string> set)
    {
        Label(label);
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
        var box = new ComboBox { IsEditable = true, ItemsSource = fonts, Text = value };

        void Apply()
        {
            set(string.IsNullOrWhiteSpace(box.Text) ? "Segoe UI" : box.Text.Trim());
            _save();
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) { box.Text = s; Apply(); } };
        box.LostFocus += (_, _) => Apply();
        _fields.Children.Add(box);
    }

    private void Check(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 10, 0, 0) };
        check.Click += (_, _) => { set(check.IsChecked == true); _save(); };
        _fields.Children.Add(check);
    }

    private void Combo(string label, string[] options, int selected, Action<int> set)
    {
        Label(label);
        var box = new ComboBox { ItemsSource = options, SelectedIndex = selected };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(box.SelectedIndex); _save(); } };
        _fields.Children.Add(box);
    }
}
