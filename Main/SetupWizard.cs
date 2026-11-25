using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace WindowsCustomizer;

/// <summary>What the setup wizard's steps were set to; the main window applies it.</summary>
internal sealed class WizardChoices
{
    public string AppTheme { get; set; } = AppThemes.DefaultName;
    /// <summary>Null: keep the Windows taskbar.</summary>
    public string? TaskbarDesign { get; set; }
    public TaskbarEdge Edge { get; set; } = TaskbarEdge.Bottom;
    public bool HideWindowsTaskbar { get; set; }
    public WinKeyAction WinKey { get; set; } = WinKeyAction.WindowsStart;
    public bool OwnAltTab { get; set; }
}

/// <summary>
/// The first-start wizard (and "Run the setup wizard" on the About page): the app's look, the taskbar, and what
/// the Windows key and Alt+Tab do, in a few steps. Nothing changes until Finish; Skip leaves everything as it is.
/// </summary>
internal sealed class SetupWizard : FluentWindow
{
    public WizardChoices Choices { get; } = new();

    private int _step;
    private readonly ContentControl _page = new();
    private readonly TextBlock _stepText = FluentLook.Secondary(new TextBlock { VerticalAlignment = VerticalAlignment.Center });
    private readonly Wpf.Ui.Controls.Button _back = new() { Content = "Back", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Wpf.Ui.Controls.Button _next = new() { Content = "Next", MinWidth = 110, Appearance = ControlAppearance.Primary };
    private const int Steps = 4;

    public SetupWizard(TaskbarEdge currentEdge)
    {
        Choices.Edge = currentEdge;
        Width = 820;
        Height = 620;
        MinWidth = 680;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var skip = new Wpf.Ui.Controls.Button { Content = "Skip", MinWidth = 90, Appearance = ControlAppearance.Transparent, ToolTip = "Leave everything as it is; you can run this again from the About page" };
        skip.Click += (_, _) => { DialogResult = false; };
        _back.Click += (_, _) => Show(_step - 1);
        _next.Click += (_, _) =>
        {
            if (_step < Steps - 1) Show(_step + 1);
            else DialogResult = true;
        };

        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(_next, Dock.Right);
        DockPanel.SetDock(_back, Dock.Right);
        DockPanel.SetDock(skip, Dock.Left);
        bottom.Children.Add(_next);
        bottom.Children.Add(_back);
        bottom.Children.Add(skip);
        _stepText.Margin = new Thickness(12, 0, 0, 0);
        bottom.Children.Add(_stepText);

        var body = new DockPanel { Margin = new Thickness(24, 4, 24, 20) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        FluentLook.Apply(this, "Set up Windows Customizer", body);
        Show(0);
    }

    private void Show(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        _page.Content = _step switch
        {
            0 => LookPage(),
            1 => TaskbarPage(),
            2 => KeysPage(),
            _ => DonePage(),
        };
        _back.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = _step == Steps - 1 ? "Finish" : "Next";
        _stepText.Text = $"Step {_step + 1} of {Steps}";
    }

    // ---------- Pages ----------

    private UIElement LookPage()
    {
        var page = Page("Welcome!", "Windows Customizer changes how Windows looks and works: the taskbar, start menu, desktop, Alt+Tab and more. " +
                                    "First, how should this app's own windows look? (You see it right away.)");
        var tiles = new WrapPanel();
        foreach (var theme in AppThemes.BuiltIns)
        {
            bool dark = theme.Mode == AppThemeMode.Dark;
            var swatch = new Border
            {
                Width = 150, Height = 70, CornerRadius = new CornerRadius(Math.Min(theme.CornerRadius, 12)), Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(TaskbarView.ParseColor(theme.BackgroundColor, dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3))),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1),
            };
            if (theme.Mode == AppThemeMode.System) swatch.Background = new LinearGradientBrush(Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x20, 0x20, 0x20), 0);
            var accent = new Border
            {
                Width = 56, Height = 14, CornerRadius = new CornerRadius(4), Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(TaskbarView.ParseColor(theme.Accent, SystemParameters.WindowGlassColor)),
            };
            swatch.Child = accent;
            var content = new StackPanel();
            content.Children.Add(swatch);
            content.Children.Add(new TextBlock { Text = theme.Name, FontWeight = FontWeights.SemiBold });
            if (theme.Name == AppThemes.DefaultName) content.Children.Add(Small("Light or dark, like Windows"));
            tiles.Children.Add(Tile(content, Choices.AppTheme == theme.Name, () =>
            {
                Choices.AppTheme = theme.Name;
                AppThemes.Apply(theme); // see it now (Skip puts the saved one back)
            }, tiles));
        }
        page.Children.Add(tiles);
        return page;
    }

    private UIElement TaskbarPage()
    {
        var page = Page("Taskbar", "Keep the Windows taskbar, or use one of Windows Customizer's. You can design your own later in the taskbar designer.");
        var tiles = new WrapPanel();
        var options = new (string? design, string title, string text, SymbolRegular icon)[]
        {
            (null, "Windows taskbar", "Keep it as it is", SymbolRegular.Window24),
            ("Classic", "Classic", "Menu and windows on the left, clock on the right", SymbolRegular.PanelBottom20),
            ("Centered", "Centered", "Start, pinned apps and windows in the middle", SymbolRegular.AlignCenterHorizontal20),
            ("Minimal", "Minimal", "Only windows and a clock", SymbolRegular.LineHorizontal120),
            ("Dock", "Dock", "A floating, rounded dock with icons", SymbolRegular.Apps24),
        };
        var extras = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        void UpdateExtras() => extras.Visibility = Choices.TaskbarDesign == null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (design, title, text, icon) in options)
        {
            var content = new StackPanel { Width = 150 };
            content.Children.Add(new SymbolIcon(icon) { FontSize = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            content.Children.Add(Small(text));
            tiles.Children.Add(Tile(content, Choices.TaskbarDesign == design, () => { Choices.TaskbarDesign = design; UpdateExtras(); }, tiles));
        }
        page.Children.Add(tiles);

        // Where, and whether to hide Windows' own.
        var edges = new WrapPanel();
        foreach (var edge in new[] { TaskbarEdge.Bottom, TaskbarEdge.Top })
        {
            var label = new TextBlock { Text = edge == TaskbarEdge.Bottom ? "At the bottom" : "At the top", FontWeight = FontWeights.SemiBold, Width = 150 };
            edges.Children.Add(Tile(label, Choices.Edge == edge, () => Choices.Edge = edge, edges));
        }
        extras.Children.Add(Heading("Screen edge"));
        extras.Children.Add(edges);
        extras.Children.Add(Toggle("Hide the Windows taskbar", "Only yours shows. It comes back by itself when Windows Customizer closes.",
            Choices.HideWindowsTaskbar, v => Choices.HideWindowsTaskbar = v));
        UpdateExtras();
        page.Children.Add(extras);
        return page;
    }

    private UIElement KeysPage()
    {
        var page = Page("Windows key and Alt+Tab", "What should the Windows key open?");
        var tiles = new WrapPanel();
        var options = new (WinKeyAction action, string title, string text, SymbolRegular icon)[]
        {
            (WinKeyAction.WindowsStart, "Windows Start", "Leave the key as it is", SymbolRegular.Window24),
            (WinKeyAction.StartMenu, "Our start menu", "Pinned apps, all apps and search, in your own look", SymbolRegular.Grid24),
            (WinKeyAction.Search, "Our search", "Just a search box: type and press Enter", SymbolRegular.Search24),
        };
        foreach (var (action, title, text, icon) in options)
        {
            var content = new StackPanel { Width = 170 };
            content.Children.Add(new SymbolIcon(icon) { FontSize = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            content.Children.Add(Small(text));
            tiles.Children.Add(Tile(content, Choices.WinKey == action, () => Choices.WinKey = action, tiles));
        }
        page.Children.Add(tiles);
        page.Children.Add(Toggle("Use Windows Customizer's Alt+Tab", "Live previews of your windows, with your own colors, size and animation.",
            Choices.OwnAltTab, v => Choices.OwnAltTab = v));
        return page;
    }

    private UIElement DonePage()
    {
        var page = Page("All set", "Press Finish to use this. Everything can be changed later in the app, and the About page can run this wizard again.");
        string taskbar = Choices.TaskbarDesign == null ? "the Windows taskbar"
            : $"the {Choices.TaskbarDesign} taskbar at the {(Choices.Edge == TaskbarEdge.Top ? "top" : "bottom")}" +
              (Choices.HideWindowsTaskbar ? ", Windows' own hidden" : "");
        string key = Choices.WinKey switch
        {
            WinKeyAction.StartMenu => "opens Windows Customizer's start menu",
            WinKeyAction.Search => "opens Windows Customizer's search",
            _ => "opens Windows Start",
        };
        foreach (var (icon, text) in new[]
                 {
                     (SymbolRegular.PaintBrush24, $"App look: {Choices.AppTheme}"),
                     (SymbolRegular.PanelBottom20, $"Taskbar: {taskbar}"),
                     (SymbolRegular.Keyboard24, $"Windows key: {key}"),
                     (SymbolRegular.WindowMultiple20, $"Alt+Tab: {(Choices.OwnAltTab ? "Windows Customizer's" : "Windows' own")}"),
                 })
        {
            page.Children.Add(new CardControl
            {
                Icon = new SymbolIcon(icon), Header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 4),
            });
        }
        return page;
    }

    // ---------- Building blocks ----------

    private static StackPanel Page(string title, string text)
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        page.Children.Add(FluentLook.Secondary(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), FontSize = 14 }));
        return page;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) };

    private static TextBlock Small(string text) =>
        FluentLook.Secondary(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

    private static UIElement Toggle(string title, string text, bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsChecked = value };
        toggle.Click += (_, _) => set(toggle.IsChecked == true);
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Small(text));
        return new CardControl { Header = header, Content = toggle, Margin = new Thickness(0, 12, 0, 0) };
    }

    /// <summary>A choice tile (like the designer's): a card, an accent frame when chosen; one chosen per <paramref name="group"/>.</summary>
    private static Border Tile(UIElement content, bool chosen, Action onChoose, Panel group)
    {
        var tile = new Border { Child = content, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand, Tag = chosen };
        void Paint(Border t)
        {
            bool on = t.Tag is true;
            t.SetResourceReference(Border.BackgroundProperty, on || t.IsMouseOver ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
            t.SetResourceReference(Border.BorderBrushProperty, on ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            t.BorderThickness = new Thickness(on ? 2 : 1);
            t.Padding = on ? new Thickness(13) : new Thickness(14);
        }
        tile.MouseEnter += (_, _) => Paint(tile);
        tile.MouseLeave += (_, _) => Paint(tile);
        tile.MouseLeftButtonUp += (_, _) =>
        {
            foreach (var other in group.Children.OfType<Border>()) { other.Tag = false; Paint(other); }
            tile.Tag = true;
            Paint(tile);
            onChoose();
        };
        Paint(tile);
        return tile;
    }
}
