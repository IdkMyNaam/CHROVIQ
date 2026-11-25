using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// A simple settings window in the Windows 11 look: sections of setting cards that save after every change,
/// plus a "Try it" button. Used by the search and start menu settings.
/// </summary>
public abstract class SettingsForm : Wpf.Ui.Controls.FluentWindow
{
    private readonly StackPanel _fields = new() { Margin = new Thickness(16, 4, 16, 16) };
    private readonly Action _save;

    /// <summary>Where new fields go: a section (see <see cref="Section"/>) or, normally, the window's list.</summary>
    protected Panel? Target { get; set; }
    private Panel Fields => Target ?? _fields;

    /// <summary>A part of the form that can be emptied and filled again (fields for the chosen material, say).</summary>
    protected StackPanel Section()
    {
        var section = new StackPanel();
        Fields.Children.Add(section);
        return section;
    }

    /// <summary>A slider with its value next to it.</summary>
    protected void Slider(string label, double value, double min, double max, Action<double> set)
    {
        var shown = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = Math.Max(max, min + 0.001), Value = value, VerticalAlignment = VerticalAlignment.Center };
        shown.Text = value.ToString("0.##", CultureInfo.CurrentCulture);
        slider.ValueChanged += (_, _) =>
        {
            shown.Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture);
            set(slider.Value);
            _save();
        };
        var row = new DockPanel();
        DockPanel.SetDock(shown, Dock.Right);
        row.Children.Add(shown);
        row.Children.Add(slider);
        Card(label, row);
    }

    protected SettingsForm(string title, Action save, Action tryIt)
    {
        _save = save;
        Width = 480;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tryButton = new Wpf.Ui.Controls.Button
        {
            Content = "Try it", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Play24),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 8, 16, 16),
        };
        tryButton.Click += (_, _) => tryIt();
        var body = new DockPanel();
        DockPanel.SetDock(tryButton, Dock.Bottom);
        body.Children.Add(tryButton);
        body.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        FluentLook.Apply(this, title, body);
    }

    protected void Header(string text) =>
        Fields.Children.Add(new TextBlock
        {
            Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, Fields.Children.Count == 0 ? 4 : 20, 0, 8),
        });

    /// <summary>A card: the label (and a hint) above, the control below.</summary>
    private void Card(string label, UIElement control, string? hint = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(control);
        if (hint != null) stack.Children.Add(HintText(hint));
        Fields.Children.Add(new Wpf.Ui.Controls.Card { Content = stack, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(14, 10, 14, 12) });
    }

    private static TextBlock HintText(string text) =>
        FluentLook.Secondary(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

    protected void Hint(string text) => Fields.Children.Add(HintText(text));

    protected void Text(string label, string value, Action<string> set, string? hint = null)
    {
        var box = new TextBox { Text = value };
        box.TextChanged += (_, _) => { set(box.Text); _save(); };
        Card(label, box, hint);
    }

    protected void Number(string label, double value, double min, double max, Action<double> set)
    {
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
        Card($"{label}  ({min:0}–{max:0})", box);
    }

    protected void ColorField(string label, string value, Action<string> set, string? hint = null)
    {
        var swatch = new Border { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
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
        Card(label, row, hint);
    }

    /// <summary>An on/off setting: a card with the label on the left and a toggle switch on the right.</summary>
    protected void Check(string label, bool value, Action<bool> set)
    {
        var toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = value };
        toggle.Click += (_, _) => { set(toggle.IsChecked == true); _save(); };
        Fields.Children.Add(new Wpf.Ui.Controls.CardControl
        {
            Header = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            Content = toggle,
            Margin = new Thickness(0, 0, 0, 4),
        });
    }

    protected void Combo(string label, string[] options, int selected, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = options, SelectedIndex = selected };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(box.SelectedIndex); _save(); } };
        Card(label, box);
    }

    /// <summary>A dropdown of the installed fonts (you can also type a name).</summary>
    protected void Font(string label, string value, Action<string> set)
    {
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
        var box = new ComboBox { IsEditable = true, ItemsSource = fonts, Text = value };
        void Apply()
        {
            set(string.IsNullOrWhiteSpace(box.Text) ? "Segoe UI" : box.Text.Trim());
            _save();
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) { box.Text = s; Apply(); } };
        box.LostFocus += (_, _) => Apply();
        Card(label, box);
    }
}
