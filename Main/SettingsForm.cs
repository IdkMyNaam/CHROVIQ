using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// A simple settings window: a scrolling column of labeled fields that save after every change,
/// plus a "Try it" button. Used by the search and start menu settings.
/// </summary>
public abstract class SettingsForm : Window
{
    private readonly StackPanel _fields = new() { Margin = new Thickness(14) };
    private readonly Action _save;

    protected SettingsForm(string title, Action save, Action tryIt)
    {
        _save = save;
        Title = title;
        Width = 420;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tryButton = new Button { Content = "Try it", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 14, 14) };
        tryButton.Click += (_, _) => tryIt();
        var root = new DockPanel();
        DockPanel.SetDock(tryButton, Dock.Bottom);
        root.Children.Add(tryButton);
        root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    protected void Header(string text) =>
        _fields.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _fields.Children.Count == 0 ? 0 : 16, 0, 0) });

    private void Label(string text) =>
        _fields.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 2) });

    protected void Hint(string text) =>
        _fields.Children.Add(new TextBlock { Text = text, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });

    protected void Text(string label, string value, Action<string> set, string? hint = null)
    {
        Label(label);
        var box = new TextBox { Text = value };
        box.TextChanged += (_, _) => { set(box.Text); _save(); };
        _fields.Children.Add(box);
        if (hint != null) Hint(hint);
    }

    protected void Number(string label, double value, double min, double max, Action<double> set)
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

    protected void ColorField(string label, string value, Action<string> set, string? hint = null)
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

    protected void Check(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 10, 0, 0) };
        check.Click += (_, _) => { set(check.IsChecked == true); _save(); };
        _fields.Children.Add(check);
    }

    protected void Combo(string label, string[] options, int selected, Action<int> set)
    {
        Label(label);
        var box = new ComboBox { ItemsSource = options, SelectedIndex = selected };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(box.SelectedIndex); _save(); } };
        _fields.Children.Add(box);
    }

    /// <summary>A dropdown of the installed fonts (you can also type a name).</summary>
    protected void Font(string label, string value, Action<string> set)
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
}
