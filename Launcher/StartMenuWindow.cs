using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>What the start menu needs from the rest of the app.</summary>
public sealed class StartMenuContext
{
    public required StartMenuLook Look { get; init; }
    public required SearchLook Search { get; init; }
    /// <summary>Pinned apps; changed in place, then <see cref="Save"/> is called.</summary>
    public required List<StartPin> Pins { get; init; }
    public required Action Save { get; init; }
    /// <summary>Adds an app to the pinned apps of the taskbar design in use (null: not offered).</summary>
    public Action<AppEntry>? PinToTaskbar { get; init; }
    /// <summary>Shows the Windows Customizer window.</summary>
    public Action? OpenCustomizer { get; init; }
}

/// <summary>
/// The start menu: search at the top, pinned apps and all apps below, user and power buttons at the
/// bottom. Opened by the Start module, the Windows key (when chosen) or the app menu. One at a time;
/// opening it again closes it. Closes when you click elsewhere.
/// </summary>
public sealed class StartMenuWindow : Window, ILiveGlassWindow
{
    public bool WantsLiveGlass => _context.Look.LiveGlass;

    private static StartMenuWindow? _open;
    public static bool IsOpen => _open != null;

    private readonly StartMenuContext _context;
    private readonly TextBox _box = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _placeholder = new() { Text = "Type to search apps and the web", Opacity = 0.55, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Pinned apps and all apps. Virtualized: only the rows you can see are laid out and drawn.</summary>
    private readonly ItemsControl _home = HomeList();
    private readonly ListBox _results = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
    private List<AppEntry> _apps = new();
    private bool _closing;
    /// <summary>The home page is built; the opening animation is done (icons load after it, so it stays smooth).</summary>
    private readonly TaskCompletionSource _filled = new(), _opened = new();
    private bool _dialogOpen; // a confirmation is showing: don't close on losing focus
    private Style _flatButton = null!;

    private sealed record WebSearch(string Text)
    {
        public override string ToString() => $"Search the web for \"{Text}\"";
    }

    /// <summary>Opens the start menu, or closes it if it is open.</summary>
    /// <param name="anchor">Open next to this element (the Start button on the bar); null: in the middle of the screen.</param>
    public static StartMenuWindow? Toggle(StartMenuContext context, FrameworkElement? anchor = null, TaskbarEdge edge = TaskbarEdge.Bottom)
    {
        if (_open != null)
        {
            _open.CloseQuietly();
            return null;
        }
        var window = new StartMenuWindow(context);
        _open = window;
        window.ShowAt(anchor, edge);
        return window;
    }

    private StartMenuWindow(StartMenuContext context)
    {
        _context = context;
        var look = context.Look;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Width = Math.Clamp(look.Width, 360, 1600);
        Height = Math.Clamp(look.Height, 300, 1400);

        ApplyColors();
        Content = BuildLayout();
        Loaded += (_, _) => Loc.TranslateTree(this); // its texts in the chosen language

        _box.TextChanged += (_, _) => UpdateSearch();
        _box.PreviewKeyDown += OnKey;
        _results.ItemContainerStyle = ResultStyle();
        _results.ItemTemplate = AppTemplate();
        _results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is { } item and (AppEntry or WebSearch)) Run(item);
        };

        Deactivated += (_, _) => { if (!_dialogOpen) CloseQuietly(); };
        Closed += (_, _) => { if (_open == this) _open = null; };
        Loaded += async (_, _) =>
        {
            try
            {
                _apps = await AppSearch.Index();
                FillHome();
                UpdateSearch();
            }
            finally { _filled.TrySetResult(); } // the opening animation waits for this (briefly)
        };
    }

    // ---------- Look ----------

    private void ApplyColors()
    {
        var look = _context.Look;
        var foreground = TaskbarView.ParseColor(look.Foreground, Colors.White);
        var accent = SystemParameters.WindowGlassBrush is SolidColorBrush glass
            ? Color.FromRgb(glass.Color.R, glass.Color.G, glass.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);
        var highlight = TaskbarView.ParseColor(look.Highlight, accent);

        Resources["MenuBackground"] = new SolidColorBrush(TaskbarView.ParseColor(look.Background, Color.FromArgb(0xF2, 0x20, 0x20, 0x20)));
        Resources["MenuForeground"] = new SolidColorBrush(foreground);
        Resources["MenuHover"] = new SolidColorBrush(Color.FromArgb(0x22, foreground.R, foreground.G, foreground.B));
        Resources["MenuHighlight"] = new SolidColorBrush(Color.FromArgb(0xCC, highlight.R, highlight.G, highlight.B));
        Resources["MenuField"] = new SolidColorBrush(Color.FromArgb(0x18, foreground.R, foreground.G, foreground.B));
        SetResourceReference(ForegroundProperty, "MenuForeground");
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(look.FontFamily) ? "Segoe UI" : look.FontFamily); }
        catch (ArgumentException) { /* keep Segoe UI */ }
        FontSize = Math.Clamp(look.FontSize, 8, 30);

        // Flat button: transparent, hover highlight, used for tiles, rows and footer buttons.
        _flatButton = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Setter Property="Foreground" Value="{DynamicResource MenuForeground}" />
              <Setter Property="Focusable" Value="False" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}">
                      <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="Background" Value="{DynamicResource MenuHover}" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
    }

    private Style ResultStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
          <Setter Property="Foreground" Value="{DynamicResource MenuForeground}" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="Bd" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" CornerRadius="6" Padding="8,6">
                  <ContentPresenter />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource MenuHover}" /></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource MenuHighlight}" /></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    // ---------- Layout ----------

    private FrameworkElement BuildLayout()
    {
        // Search field.
        _box.SetResourceReference(ForegroundProperty, "MenuForeground");
        _box.SetResourceReference(TextBoxBase_CaretBrush, "MenuForeground");
        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(_box);
        var search = new DockPanel();
        var glass = Glyph("", 14);
        glass.Margin = new Thickness(0, 0, 10, 0);
        glass.Opacity = 0.8;
        DockPanel.SetDock(glass, Dock.Left);
        search.Children.Add(glass);
        search.Children.Add(field);
        bool searchAtBottom = _context.Look.SearchAtBottom;
        var searchFrame = new Border
        {
            Child = search, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(8),
            Margin = searchAtBottom ? new Thickness(0, 14, 0, 0) : new Thickness(0, 0, 0, 14),
        };
        searchFrame.SetResourceReference(Border.BackgroundProperty, "MenuField");

        // Footer: user on the left; settings, Windows Customizer and power on the right.
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var power = FooterButton("", "Power");
        power.Click += (_, _) => ShowPowerMenu(power);
        var customizer = FooterButton("", "Windows Customizer");
        customizer.Click += (_, _) => { CloseQuietly(); _context.OpenCustomizer?.Invoke(); };
        var settings = FooterButton("", "Settings");
        settings.Click += (_, _) => { Start("ms-settings:"); CloseQuietly(); };
        foreach (var b in new[] { power, customizer, settings })
        {
            DockPanel.SetDock(b, Dock.Right);
            footer.Children.Add(b);
        }
        if (_context.Look.ShowUser) footer.Children.Add(UserButton());

        var middle = new Grid();
        middle.Children.Add(_home);
        middle.Children.Add(_results);

        var root = new DockPanel();
        // The search box at the top, or at the bottom just above the footer (docked after it).
        DockPanel.SetDock(searchFrame, searchAtBottom ? Dock.Bottom : Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(searchFrame);
        root.Children.Add(middle);

        // The background (and its material, if any) on its own layer behind the content, in the same shape.
        var corner = new CornerRadius(Math.Clamp(_context.Look.CornerRadius, 0, 40));
        // The content is kept as a ready-drawn picture (redrawn only when it changes), so an animated material
        // or live glass underneath doesn't redraw every app name on every frame.
        var frame = new Border { Child = root, Padding = new Thickness(18), CornerRadius = corner, CacheMode = new BitmapCache { SnapsToDevicePixels = true } };
        var scrollColor = TaskbarView.ParseColor(_context.Look.Foreground, Colors.White);
        ScrollbarLook.Apply(_home, _context.Look.Scrollbar, scrollColor);
        ScrollbarLook.Apply(_results, _context.Look.Scrollbar, scrollColor);
        var materialLayer = new Border { CornerRadius = corner };
        materialLayer.SetResourceReference(Border.BackgroundProperty, "MenuBackground");
        var layers = new Grid();
        layers.Children.Add(materialLayer);
        layers.Children.Add(frame);
        if (MaterialHost.Attach(materialLayer, layers, _context.Look.Material, _context.Look.MaterialValues,
                TaskbarView.ParseColor(_context.Look.Highlight, SystemParameters.WindowGlassColor)) is { } error)
            AppTrace.Log($"Start menu material: {error}");
        return layers;
    }

    /// <summary>The user's picture and name; click to open the account settings.</summary>
    private Button UserButton()
    {
        var look = _context.Look;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (UserPicture(look.UserPicture) is { } picture)
        {
            var brush = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 32, Height = 32, Fill = brush });
        }
        else row.Children.Add(Glyph("\uE77B", 16)); // person symbol
        row.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(look.UserName) ? DisplayName() : look.UserName.Trim(),
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button { Style = _flatButton, Content = row, Padding = new Thickness(8, 6, 10, 6), ToolTip = "Account settings" };
        button.Click += (_, _) => { Start("ms-settings:yourinfo"); CloseQuietly(); };
        return button;
    }

    /// <summary>The chosen picture file, else the Windows account picture (where Windows keeps it), else null.</summary>
    private static ImageSource? UserPicture(string? chosen)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(chosen)) candidates.Add(Environment.ExpandEnvironmentVariables(chosen.Trim()));
        try
        {
            string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            using var key = sid == null ? null : Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            foreach (var name in new[] { "Image192", "Image96", "Image208", "Image240", "Image64" })
                if (key?.GetValue(name) is string path) candidates.Add(path);
        }
        catch (Exception) { /* no access: no picture */ }

        foreach (var path in candidates)
        {
            try
            {
                if (!System.IO.File.Exists(path)) continue;
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 64;
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file open
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception) { /* not a readable image: try the next */ }
        }
        return null;
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserNameEx(int format, System.Text.StringBuilder name, ref uint size);

    /// <summary>The account's display name ("Jane Doe"), or the sign-in name when Windows doesn't have one.</summary>
    private static string DisplayName()
    {
        try
        {
            uint size = 256;
            var name = new System.Text.StringBuilder((int)size);
            if (GetUserNameEx(3 /* NameDisplay */, name, ref size) && name.Length > 0) return name.ToString();
        }
        catch (Exception) { /* not available */ }
        return Environment.UserName;
    }

    // TextBoxBase.CaretBrushProperty, named here because the static is on the base class.
    private static readonly DependencyProperty TextBoxBase_CaretBrush = System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty;

    private Button FooterButton(string glyph, string toolTip) => new()
    {
        Style = _flatButton,
        Content = Glyph(glyph, 16),
        ToolTip = toolTip,
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(4, 0, 0, 0),
    };

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Pinned tiles and the alphabetical list. Icons are filled in afterwards, a few at a time.</summary>
    private void FillHome()
    {
        var look = _context.Look;
        var page = new List<UIElement>();
        var pendingIcons = new List<(Image image, AppEntry app)>();

        if (look.ShowPinned)
        {
            page.Add(SectionHeader("Pinned"));
            var tiles = new WrapPanel();
            int columns = Math.Clamp(look.Columns, 2, 12);
            double tileWidth = Math.Floor((Width - 2 * 18 - 20) / columns);
            var pins = _context.Pins.ToList();
            if (pins.Count == 0)
                page.Add(new TextBlock { Text = "Right-click an app below and choose \"Pin to Start\".", Opacity = 0.6, Margin = new Thickness(4, 0, 0, 8) });
            for (int i = 0; i < pins.Count; i++)
            {
                var app = pins[i].ToEntry();
                double iconSize = Math.Clamp(look.PinnedIconSize, 16, 96);
                var image = new Image { Width = iconSize, Height = iconSize, Margin = new Thickness(0, 0, 0, 6) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                pendingIcons.Add((image, app));
                var content = new StackPanel();
                content.Children.Add(image);
                content.Children.Add(new TextBlock { Text = app.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = FontSize - 1 });
                var tile = new Button { Style = _flatButton, Content = content, Width = tileWidth, Padding = new Thickness(6, 10, 6, 10), ToolTip = app.Name };
                tile.Click += (_, _) => Run(app);
                tile.ContextMenu = AppMenu(app, pinIndex: i);
                tiles.Children.Add(tile);
            }
            page.Add(tiles);
        }

        if (look.ShowAllApps)
        {
            page.Add(SectionHeader("All apps"));
            foreach (var group in _apps.GroupBy(a => char.IsLetter(a.Name.FirstOrDefault()) ? char.ToUpperInvariant(a.Name[0]) : '#'))
            {
                page.Add(new TextBlock { Text = group.Key.ToString(), FontWeight = FontWeights.SemiBold, Opacity = 0.7, Margin = new Thickness(8, 10, 0, 4) });
                foreach (var app in group)
                {
                    var image = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0) };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    // Its icon once the row is scrolled into view (and the opening animation is done).
                    var iconOf = app;
                    image.Loaded += (_, _) =>
                    {
                        if (image.Source != null) return;
                        _opened.Task.ContinueWith(_ => Dispatcher.InvokeAsync(() => image.Source ??= iconOf.Icon, DispatcherPriority.Background),
                            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                    };
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(image);
                    row.Children.Add(new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center });
                    var button = new Button { Style = _flatButton, Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5) };
                    var entry = app;
                    button.Click += (_, _) => Run(entry);
                    button.ContextMenu = AppMenu(entry, pinIndex: null);
                    page.Add(button);
                }
            }
        }

        _home.ItemsSource = page;
        LoadIconsGradually(pendingIcons);
    }

    /// <summary>A list with its own scroll viewer and a virtualizing panel, scrolling smoothly by pixels.</summary>
    private static ItemsControl HomeList()
    {
        var list = new ItemsControl
        {
            Focusable = false,
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse("""
                <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><VirtualizingStackPanel /></ItemsPanelTemplate>
                """),
            Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ItemsControl">
                  <ScrollViewer CanContentScroll="True" Focusable="False" HorizontalScrollBarVisibility="Disabled"
                                VerticalScrollBarVisibility="{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}">
                    <ItemsPresenter />
                  </ScrollViewer>
                </ControlTemplate>
                """),
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        return list;
    }

    private TextBlock SectionHeader(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = FontSize + 2, Margin = new Thickness(4, 4, 0, 8) };

    /// <summary>Loads icons in small batches while the window is idle, so opening the menu stays instant.</summary>
    private void LoadIconsGradually(List<(Image image, AppEntry app)> pending)
    {
        int next = 0;
        void Batch()
        {
            if (_closing) return;
            for (int i = 0; i < 15 && next < pending.Count; i++, next++)
                pending[next].image.Source = pending[next].app.Icon;
            if (next < pending.Count) Dispatcher.InvokeAsync(Batch, DispatcherPriority.Background);
        }
        // After the opening animation: loading icons during it would make it stutter.
        _opened.Task.ContinueWith(_ => Dispatcher.InvokeAsync(Batch, DispatcherPriority.Background),
            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------- Right-click menu ----------

    private ContextMenu AppMenu(AppEntry app, int? pinIndex)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _dialogOpen = true;   // the menu takes focus for a moment: stay open
        menu.Closed += (_, _) => _dialogOpen = false;

        bool pinned = _context.Pins.Any(p => p.LaunchPath.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase));
        Add(menu, pinned ? "Unpin from Start" : "Pin to Start", () =>
        {
            if (pinned) _context.Pins.RemoveAll(p => p.LaunchPath.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase));
            else _context.Pins.Add(StartPin.From(app));
            _context.Save();
            FillHome();
        });

        if (pinIndex is int index)
        {
            Add(menu, "Move left", () => MovePin(index, -1), enabled: index > 0);
            Add(menu, "Move right", () => MovePin(index, +1), enabled: index < _context.Pins.Count - 1);
        }

        if (_context.PinToTaskbar != null)
            Add(menu, "Pin to taskbar", () => _context.PinToTaskbar(app));

        menu.Items.Add(new Separator());
        // Store apps have no file to show or to run as administrator.
        Add(menu, "Open file location", () => { ShellLaunch.ShowInFolder(app.LaunchPath); CloseQuietly(); }, enabled: !app.IsShellApp);
        Add(menu, "Run as administrator", () =>
        {
            try { Process.Start(new ProcessStartInfo(app.LaunchPath) { UseShellExecute = true, Verb = "runas" })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { /* the user said no to the admin prompt */ }
            CloseQuietly();
        }, enabled: !app.IsShellApp);
        return menu;
    }

    private void MovePin(int index, int step)
    {
        int target = index + step;
        if (target < 0 || target >= _context.Pins.Count) return;
        (_context.Pins[index], _context.Pins[target]) = (_context.Pins[target], _context.Pins[index]);
        _context.Save();
        FillHome();
    }

    private static void Add(ItemsControl menu, string header, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    // ---------- Power ----------

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvents);

    private void ShowPowerMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Opened += (_, _) => _dialogOpen = true;
        menu.Closed += (_, _) => _dialogOpen = false;
        Add(menu, "Lock", () => { CloseQuietly(); LockWorkStation(); });
        Add(menu, "Sleep", () => { CloseQuietly(); SetSuspendState(false, false, false); });
        menu.Items.Add(new Separator());
        Add(menu, "Sign out", () => Confirmed("Sign out? Programs that are open will be closed.", () => ExitWindowsEx(0 /* EWX_LOGOFF */, 0)));
        Add(menu, "Restart", () => Confirmed("Restart the PC? Programs that are open will be closed.", () => Start("shutdown.exe", "/r /t 0")));
        Add(menu, "Shut down", () => Confirmed("Shut down the PC? Programs that are open will be closed.", () => Start("shutdown.exe", "/s /t 0")));
        menu.IsOpen = true;
    }

    /// <summary>Asks first (these close the user's programs), then runs <paramref name="action"/>.</summary>
    private void Confirmed(string question, Action action)
    {
        _dialogOpen = true;
        var answer = MessageBox.Show(this, question, "Windows Customizer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        _dialogOpen = false;
        if (answer != MessageBoxResult.Yes) { Activate(); return; }
        CloseQuietly();
        action();
    }

    // ---------- Search ----------

    private void UpdateSearch()
    {
        _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = _box.Text.Trim();
        bool searching = text.Length > 0;
        _home.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        _results.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (!searching) return;

        var items = new List<object>(AppSearch.Find(_apps, text, Math.Clamp(_context.Search.MaxResults, 1, 30)));
        if (_context.Search.ShowWebSearch) items.Add(new WebSearch(text));
        _results.ItemsSource = items;
        _results.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _results.Items.Count > 0:
                _results.SelectedIndex = Math.Min(_results.SelectedIndex + 1, _results.Items.Count - 1);
                _results.ScrollIntoView(_results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when _results.Items.Count > 0:
                _results.SelectedIndex = Math.Max(_results.SelectedIndex - 1, 0);
                _results.ScrollIntoView(_results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                var text = _box.Text.Trim();
                if (text.Length > 0) Run(_results.SelectedItem ?? new WebSearch(text));
                e.Handled = true;
                break;
            case Key.Escape:
                if (_box.Text.Length > 0) _box.Text = ""; // first Esc clears the search, the second closes
                else CloseQuietly();
                e.Handled = true;
                break;
        }
    }

    private void Run(object choice)
    {
        try
        {
            switch (choice)
            {
                case AppEntry app:
                    app.Launch();
                    break;
                case WebSearch web:
                    var url = string.IsNullOrWhiteSpace(_context.Search.SearchUrl) ? TaskbarModule.DefaultSearchUrl : _context.Search.SearchUrl!;
                    var query = Uri.EscapeDataString(web.Text);
                    Start(url.Contains("{0}") ? url.Replace("{0}", query) : url + query);
                    break;
            }
        }
        catch (Exception ex)
        {
            _dialogOpen = true;
            MessageBox.Show(this, $"Could not open it:\n{ex.Message}", "Windows Customizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            _dialogOpen = false;
        }
        CloseQuietly();
    }

    private static void Start(string target, string? arguments = null) =>
        ShellLaunch.Start(target, arguments);

    /// <summary>A search result row: icon (or a symbol) and name.</summary>
    private static DataTemplate AppTemplate()
    {
        return (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Orientation="Horizontal">
                <Grid Width="24" Height="24" Margin="0,0,12,0">
                  <Image Source="{Binding Icon, FallbackValue={x:Null}}" RenderOptions.BitmapScalingMode="HighQuality"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
                </Grid>
                <TextBlock Text="{Binding}" VerticalAlignment="Center" />
              </StackPanel>
            </DataTemplate>
            """);
    }

    // ---------- Showing ----------

    private void ShowAt(FrameworkElement? anchor, TaskbarEdge edge)
    {
        // The start state goes on before the window shows, so it never flashes up fully drawn first.
        var animation = _context.Look.Opening ?? new PopupAnimation();
        if (Content is FrameworkElement content)
            animation.Play(content, fromBelow: edge != TaskbarEdge.Top, done: () => _opened.TrySetResult(), startAfter: _filled.Task);
        else _opened.TrySetResult();
        Show();
        UpdateLayout();
        Place(anchor, edge);
        TakeFocus();
        _box.Focus();
        Keyboard.Focus(_box);
    }

    /// <summary>Next to the Start button (on the screen side of the bar), or centered on the monitor with the mouse.</summary>
    private void Place(FrameworkElement? anchor, TaskbarEdge edge)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetCursorPos(out var reference);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        RECT? anchorRect = null;
        if (anchor != null && PresentationSource.FromVisual(anchor) != null)
        {
            scale = VisualTreeHelper.GetDpi(anchor).DpiScaleX;
            var topLeft = anchor.PointToScreen(new Point(0, 0));
            anchorRect = new RECT { Left = (int)topLeft.X, Top = (int)topLeft.Y, Right = (int)(topLeft.X + anchor.ActualWidth * scale), Bottom = (int)(topLeft.Y + anchor.ActualHeight * scale) };
            reference = new POINT { X = anchorRect.Value.Left, Y = anchorRect.Value.Top };
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(reference, MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;
        int width = (int)(Width * scale), height = (int)(Height * scale), gap = (int)(8 * scale);

        int x, y;
        if (anchorRect is { } a)
        {
            x = a.Left;
            y = edge == TaskbarEdge.Top ? a.Bottom + gap : a.Top - height - gap;
        }
        else
        {
            x = work.Left + (work.Right - work.Left - width) / 2;
            y = work.Top + (work.Bottom - work.Top - height) / 2;
        }
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, 0);
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId(); // kernel32, not user32
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);

    /// <summary>Takes the keyboard even when opened by the Windows key (our app isn't in front then).</summary>
    private void TakeFocus()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Activate();
        if (GetForegroundWindow() == hwnd) return;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint ourThread = GetCurrentThreadId();
        AttachThreadInput(ourThread, foregroundThread, true);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        AttachThreadInput(ourThread, foregroundThread, false);
        Activate();
    }

    private void CloseQuietly()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
