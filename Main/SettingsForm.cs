using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// A settings window in the style of the taskbar designer: section tiles on the left (one per <see cref="Header"/>),
/// the chosen section's setting cards on the right, and "Try it" / Close at the bottom. Saves after every change.
/// Used by the search, start menu, Alt+Tab, desktop, menu and theme material settings.
/// </summary>
public abstract class SettingsForm : Wpf.Ui.Controls.FluentWindow
{
    private readonly Action _save;

    /// <summary>The section tiles (left) and the sections' pages (right; only the chosen one is visible).</summary>
    private readonly StackPanel _tiles = new();
    private readonly Grid _pages = new() { Margin = new Thickness(4, 0, 12, 12) };
    private readonly List<(Border tile, StackPanel page)> _sections = new();
    private StackPanel? _page;
    private int _chosen = -1;

    /// <summary>Where new fields go: a sub-part (see <see cref="Section"/>) or, normally, the current section's page.</summary>
    protected Panel? Target { get; set; }
    private Panel Fields => Target ?? _page ?? NewSection("General");

    /// <summary>A part of the form that can be emptied and filled again (fields for the chosen material, say).</summary>
    protected StackPanel Section()
    {
        var section = new StackPanel();
        Fields.Children.Add(section);
        return section;
    }

    /// <summary>
    /// "Material (shader)": a dropdown of the materials and, below it, the chosen one's description, compile
    /// error and settings (refilled when the choice changes; the dropdown itself stays, see the designer's crash).
    /// </summary>
    /// <param name="liveGlass">Popups only: their "Live glass" setting (value now, and how to change it).</param>
    protected void MaterialFields(string? current, Action<string?> setName, Func<Dictionary<string, string>> values, string hint,
        (bool Value, Action<bool> Set)? liveGlass = null)
    {
        Header("Material (shader)");
        var materials = Materials.LoadAll();
        var names = new[] { "(none: plain background)" }.Concat(materials.Select(m => m.Name)).ToArray();
        int index = materials.FindIndex(m => m.Name.Equals(current, StringComparison.CurrentCultureIgnoreCase));
        StackPanel? details = null; // made right after the dropdown
        Combo("Material", names, index + 1, chosen =>
        {
            setName(chosen <= 0 ? null : materials[chosen - 1].Name);
            if (details != null) Dispatcher.BeginInvoke(() => FillMaterial(details, chosen <= 0 ? null : materials[chosen - 1], values));
        });
        if (liveGlass is { } live)
        {
            Check("Live glass: show what's really behind it", live.Value, live.Set);
            Hint("For glass materials (Clear glass, Tinted glass): they show the windows and moving wallpaper behind instead of " +
                 "the wallpaper picture. While on, this window doesn't show in screenshots, recordings or screen sharing. " +
                 "Popups: from the next time they open; CHROVIQ's own windows: right away.");
        }
        details = Section();
        FillMaterial(details, index < 0 ? null : materials[index], values);
        Hint(hint + " Your own materials go in %AppData%\\WindowsCustomizer\\Materials; the taskbar designer can import and export them (.zip).");
    }

    private void FillMaterial(StackPanel panel, Material? material, Func<Dictionary<string, string>> values)
    {
        SafeUi.Clear(panel);
        if (material == null) return;
        var previous = Target;
        Target = panel;
        try
        {
            Hint((material.Description ?? "") + (material.Animated ? " Animated." : ""));
            Materials.Compile(material, out var error);
            if (error != null) Hint("This material doesn't compile, so the plain background shows:\n" + error);
            var settings = values();
            foreach (var parameter in material.Parameters)
            {
                string value = settings.TryGetValue(parameter.Name, out var v) ? v : parameter.Default;
                if (parameter.Type == MaterialParameterType.Color)
                    ColorField(parameter.Name, value, text => settings[parameter.Name] = text, parameter.Hint);
                else
                {
                    double number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : parameter.Min;
                    Slider(parameter.Name, number, parameter.Min, parameter.Max,
                        n => settings[parameter.Name] = n.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
        }
        finally { Target = previous; }
    }

    /// <summary>
    /// The opening animation: a style (presets or Custom), length and easing; Custom shows its own settings
    /// (fade, slide distance and direction, start size) below.
    /// </summary>
    protected void AnimationFields(PopupAnimation animation)
    {
        Combo("Animation", new[] { "None", "Fade in", "Slide in", "Zoom in", "Pop (zoom with a bounce)", "Custom" },
            (int)animation.Style, i =>
            {
                animation.Style = (AnimationStyle)i;
                if (_customAnimation != null)
                    _customAnimation.Visibility = animation.Style == AnimationStyle.Custom ? Visibility.Visible : Visibility.Collapsed;
            });
        Slider("Length (ms)", animation.DurationMs, 0, 1500, v => animation.DurationMs = Math.Round(v / 10) * 10);
        Combo("Speed curve", new[] { "Even", "Smooth (slows at the end)", "Strong (fast start)", "Overshoot", "Bounce", "Elastic" },
            (int)animation.Easing, i => animation.Easing = (AnimationEasing)i);

        var custom = Section();
        _customAnimation = custom;
        custom.Visibility = animation.Style == AnimationStyle.Custom ? Visibility.Visible : Visibility.Collapsed;
        var previous = Target;
        Target = custom;
        try
        {
            Check("Fade in", animation.Fade, v => animation.Fade = v);
            Slider("Slide distance (pixels, 0 = no slide)", animation.Distance, 0, 600, v => animation.Distance = Math.Round(v));
            Combo("Slide direction", new[] { "From the taskbar", "Up", "Down", "Left", "Right" },
                (int)animation.Direction, i => animation.Direction = (AnimationDirection)i);
            Slider("Start size (%, 100 = no zoom)", animation.StartSize, 10, 200, v => animation.StartSize = Math.Round(v));
        }
        finally { Target = previous; }
        Hint("Press \"Try it\" to see it. Custom mixes fade, slide and zoom the way you set them.");
    }

    private StackPanel? _customAnimation;

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
        Width = 900;
        Height = 660;
        MinWidth = 720;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Bottom: status, Try it, Close (like the designer).
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var close = new Wpf.Ui.Controls.Button { Content = "Close", MinWidth = 90 };
        close.Click += (_, _) => Close();
        var tryButton = new Wpf.Ui.Controls.Button
        {
            Content = "Try it", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Play24), Margin = new Thickness(0, 0, 8, 0),
        };
        tryButton.Click += (_, _) => tryIt();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(tryButton, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(tryButton);
        bottom.Children.Add(FluentLook.Secondary(new TextBlock
        {
            Text = "Changes are saved automatically.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        }));

        // Left: the sections as tiles, in a card.
        var sectionsPanel = new DockPanel();
        var sectionsTitle = new TextBlock { Text = "Sections", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(sectionsTitle, Dock.Top);
        sectionsPanel.Children.Add(sectionsTitle);
        sectionsPanel.Children.Add(new ScrollViewer
        {
            Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var sectionsCard = new Wpf.Ui.Controls.Card
        {
            Content = sectionsPanel, Padding = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        // Right: the chosen section's settings.
        var options = new ScrollViewer
        {
            Content = _pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(12, 0, 0, 0),
        };

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(options, 1);
        columns.Children.Add(sectionsCard);
        columns.Children.Add(options);

        var body = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(columns);
        FluentLook.Apply(this, title, body);
    }

    /// <summary>Starts a new section: a tile on the left and its own page of settings on the right.</summary>
    protected void Header(string text)
    {
        Target = null;
        NewSection(text);
    }

    private StackPanel NewSection(string name)
    {
        var page = new StackPanel { Visibility = Visibility.Collapsed };
        page.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        _pages.Children.Add(page);

        var label = new DockPanel();
        var icon = new Wpf.Ui.Controls.SymbolIcon(SectionIcon(name)) { FontSize = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        label.Children.Add(icon);
        label.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var tile = new Border { Child = label, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 2, 0, 2), Cursor = System.Windows.Input.Cursors.Hand };
        int index = _sections.Count;
        tile.MouseLeftButtonUp += (_, _) => Choose(index);
        Accessibility.Clickable(tile, name, () => Choose(index));
        tile.MouseEnter += (_, _) => PaintTile(index);
        tile.MouseLeave += (_, _) => PaintTile(index);
        _tiles.Children.Add(tile);
        _sections.Add((tile, page));
        _page = page;

        if (_chosen < 0) Choose(index);
        else PaintTile(index);
        return page;
    }

    /// <summary>Shows a section's page and marks its tile.</summary>
    private void Choose(int index)
    {
        int previous = _chosen;
        _chosen = index;
        for (int i = 0; i < _sections.Count; i++)
            _sections[i].page.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (previous >= 0) PaintTile(previous);
        PaintTile(index);
    }

    /// <summary>The tile look of the taskbar designer: a card, lighter on hover, an accent frame when chosen.</summary>
    private void PaintTile(int index)
    {
        var tile = _sections[index].tile;
        bool chosen = index == _chosen;
        tile.SetResourceReference(Border.BackgroundProperty,
            chosen || tile.IsMouseOver ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, chosen ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
        tile.BorderThickness = new Thickness(chosen ? 2 : 1);
        tile.Padding = chosen ? new Thickness(9, 7, 9, 7) : new Thickness(10, 8, 10, 8);
    }

    /// <summary>An icon that fits the section's name.</summary>
    private static Wpf.Ui.Controls.SymbolRegular SectionIcon(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("material")) return Wpf.Ui.Controls.SymbolRegular.Sparkle24;
        if (n.Contains("color")) return Wpf.Ui.Controls.SymbolRegular.Color24;
        if (n.Contains("size")) return Wpf.Ui.Controls.SymbolRegular.ResizeLarge24;
        if (n.Contains("shape")) return Wpf.Ui.Controls.SymbolRegular.ShapeOrganic20;
        if (n.Contains("user")) return Wpf.Ui.Controls.SymbolRegular.Person24;
        if (n.Contains("opening") || n.Contains("timing")) return Wpf.Ui.Controls.SymbolRegular.Timer24;
        if (n.Contains("result")) return Wpf.Ui.Controls.SymbolRegular.List24;
        if (n.Contains("tile") || n.Contains("icon")) return Wpf.Ui.Controls.SymbolRegular.Grid24;
        if (n.Contains("name") || n.Contains("text")) return Wpf.Ui.Controls.SymbolRegular.TextFont24;
        if (n.Contains("monitor") || n.Contains("where")) return Wpf.Ui.Controls.SymbolRegular.Desktop24;
        if (n.Contains("window")) return Wpf.Ui.Controls.SymbolRegular.Window24;
        if (n.Contains("menu")) return Wpf.Ui.Controls.SymbolRegular.Navigation24;
        if (n.Contains("layout") || n.Contains("section")) return Wpf.Ui.Controls.SymbolRegular.LayoutRowTwo24;
        return Wpf.Ui.Controls.SymbolRegular.Settings24;
    }

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

    /// <summary>Any control of your own on the current section's page.</summary>
    protected void Add(UIElement element) => Fields.Children.Add(element);

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
