using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>Builds the bar's contents (zones and modules) from its <see cref="TaskbarDesign"/>.</summary>
public partial class TaskbarView
{
    // ---------- Layout: three zones ----------

    /// <summary>
    /// Left, center and right zones. With a center zone, the two sides get equal widths so the
    /// center is truly centered on the bar. Without one, the left zone gets all space the right
    /// zone doesn't need (so a window list on the left can use the whole width).
    /// </summary>
    private UIElement BuildLayout()
    {
        // Floating dock: one row, left + center + right after each other, as wide as the contents.
        if (_look.Floating != null)
        {
            var row = new DockPanel { LastChildFill = false };
            foreach (var module in _design.Left.Concat(_design.Center).Concat(_design.Right))
            {
                var element = BuildModuleSafely(module);
                DockPanel.SetDock(element, Dock.Left);
                row.Children.Add(element);
            }
            return row;
        }

        var grid = new Grid();
        bool hasCenter = _design.Center.Count > 0;

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = hasCenter ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
        });

        var left = BuildZone(_design.Left, HorizontalAlignment.Left);
        var center = BuildZone(_design.Center, HorizontalAlignment.Center);
        var right = BuildZone(_design.Right, HorizontalAlignment.Right);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(center, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(center);
        grid.Children.Add(right);

        // Keep a long centered window list from pushing the side zones off the bar.
        grid.SizeChanged += (_, e) => center.MaxWidth = Math.Max(0, e.NewSize.Width * 0.6);
        return grid;
    }

    /// <summary>
    /// A row of modules. DockPanel (not StackPanel) so each module gets the space that is still
    /// free, which lets the window list shrink its buttons instead of running off the bar.
    /// </summary>
    private DockPanel BuildZone(IEnumerable<TaskbarModule> modules, HorizontalAlignment alignment)
    {
        var zone = new DockPanel { LastChildFill = false, HorizontalAlignment = alignment };
        foreach (var module in modules)
        {
            var element = BuildModuleSafely(module);
            DockPanel.SetDock(element, Dock.Left);
            zone.Children.Add(element);
        }
        return zone;
    }

    /// <summary>A broken module (bad options in a hand-edited file) shows a warning sign instead of breaking the bar.</summary>
    private FrameworkElement BuildModuleSafely(TaskbarModule module)
    {
        try
        {
            return BuildModule(module);
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = "⚠",
                Foreground = Brushes.Orange,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"{module.Type}: {ex.Message}",
            };
        }
    }

    private FrameworkElement BuildModule(TaskbarModule module) => module.Type switch
    {
        ModuleType.Menu => MenuModule(),
        ModuleType.Start => StartModule(),
        ModuleType.WindowList => WindowListModule(),
        ModuleType.PinnedApps => PinnedAppsModule(module),
        ModuleType.Clock => ClockModule(module),
        ModuleType.Battery => BatteryModule(),
        ModuleType.QuickSettings => GlyphButton("", "Quick Settings (Win+A)", () => PressWinShortcut('A')),
        ModuleType.Spacer => new Border { Width = Math.Max(0, module.Width ?? 16) },
        ModuleType.Text => new TextBlock
        {
            Text = module.Text ?? "",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        },
        ModuleType.Button => CustomButtonModule(module),
        ModuleType.ShowDesktop => ShowDesktopModule(),
        ModuleType.WindowsSearch => GlyphButton("", "Search (Win+S)", () => PressWinShortcut('S')),
        ModuleType.SearchBox => SearchBoxModule(module),
        ModuleType.Volume => VolumeModule(module),
        ModuleType.SystemStats => SystemStatsModule(),
        ModuleType.Media => MediaModule(module),
        _ => throw new NotSupportedException("Unknown module type."),
    };

    // ---------- Modules ----------

    private FrameworkElement MenuModule()
    {
        var button = GlyphButton("", "Windows Customizer", null);
        button.Click += (_, _) => ShowAppMenu(button);
        return button;
    }

    /// <summary>Four small squares, a generic "apps" symbol; opens the Windows Start menu.</summary>
    private FrameworkElement StartModule()
    {
        var squares = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2, Width = 14, Height = 14 };
        for (int i = 0; i < 4; i++)
        {
            var square = new Border { Margin = new Thickness(0.75), CornerRadius = new CornerRadius(1) };
            square.SetResourceReference(Border.BackgroundProperty, "BarForeground"); // text color of the design
            squares.Children.Add(square);
        }

        var button = BarButton(squares, "Start");
        button.Click += (_, _) => PressWinKey();
        return button;
    }

    private FrameworkElement WindowListModule()
    {
        // Icon-only buttons are square-ish; titled buttons get the design's maximum width.
        double maxWidth = _look.WindowIconsOnly
            ? Math.Max(32, BarHeight)
            : Math.Clamp(_look.WindowButtonWidth, 40, 600);

        var panel = new FrameworkElementFactory(typeof(TaskButtonPanel));
        panel.SetValue(TaskButtonPanel.MaxItemWidthProperty, maxWidth);

        return new ItemsControl
        {
            // One button per window, or per app with GroupWindows (see TaskItemList).
            ItemsSource = _items?.Items,
            ItemTemplate = (DataTemplate)Resources[_look.WindowIconsOnly ? "TaskWindowIconTemplate" : "TaskWindowTemplate"],
            ItemsPanel = new ItemsPanelTemplate(panel),
            Focusable = false,
        };
    }

    /// <summary>
    /// Launcher buttons. Dock style (ShowRunning, on by default): a dot under running apps; clicking a
    /// running app switches to its window (or offers a choice of windows) instead of starting it again.
    /// </summary>
    private FrameworkElement PinnedAppsModule(TaskbarModule module)
    {
        bool dockStyle = module.ShowRunning != false;
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var app in module.Apps ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(app)) continue;
            var path = Environment.ExpandEnvironmentVariables(app.Trim());
            var name = Path.GetFileNameWithoutExtension(path);
            var program = PinnedTargets.ProgramOf(app);

            // The button's DataContext is a group with this program's running windows (empty when not running),
            // so clicking, thumbnails and "Close" work exactly like for the window list.
            var running = new TaskGroup(program ?? path);

            var icon = FileIcons.Get(path);
            FrameworkElement picture = icon != null
                ? TintableIcon(icon, 20)
                : new TextBlock { Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?", FontWeight = FontWeights.SemiBold };

            // Running indicator on the screen-edge side of the icon, inside the button so it isn't clipped.
            var dot = new Border
            {
                Width = 5, Height = 3, CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = Edge == TaskbarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
            };
            dot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

            picture.Margin = new Thickness(0, 5, 0, 5); // room for the dot above or below the icon
            picture.VerticalAlignment = VerticalAlignment.Center;
            var content = new Grid();
            content.Children.Add(picture);
            content.Children.Add(dot);

            var button = BarButton(content, module.ToolTip ?? name);
            button.Padding = new Thickness(6, 0, 6, 0);
            button.DataContext = running;
            button.Click += (_, _) =>
            {
                if (running.Windows.Count == 0) Launch(path, null);
                else ActivateWindows(running.Windows.ToList(), button);
            };
            if (dockStyle)
            {
                button.MouseUp += TaskButton_MouseUp;
                button.MouseEnter += TaskButton_MouseEnter;
                button.MouseLeave += TaskButton_MouseLeave;
                button.ContextMenu = RunningAppMenu(running, path);
            }
            row.Children.Add(button);

            // Keep the running windows (and the dot) up to date.
            if (dockStyle && program != null && _tracker != null)
            {
                void Update()
                {
                    running.Windows.Clear();
                    running.Windows.AddRange(_tracker.Windows.Where(w =>
                        string.Equals(w.ProcessPath, program, StringComparison.OrdinalIgnoreCase)));
                    dot.Visibility = running.Windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    // Front: a wider line instead of a dot, like the active window's line.
                    dot.Width = running.Windows.Any(w => w.IsActive) ? 14 : 5;
                }
                Update();
                _items!.Updated += Update;
            }
        }
        return row;
    }

    /// <summary>Right-click menu of a pinned app: start another one, or close its windows.</summary>
    private ContextMenu RunningAppMenu(TaskGroup running, string path)
    {
        var menu = new ContextMenu();
        var start = new MenuItem { Header = "Open new" };
        start.Click += (_, _) => Launch(path, null);
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => { foreach (var w in running.Windows) WindowTracker.Close(w.Handle); };
        menu.Items.Add(start);
        menu.Items.Add(close);
        menu.Opened += (_, _) => close.IsEnabled = running.Windows.Count > 0;
        return menu;
    }

    /// <summary>
    /// An app icon that follows the design's tint (Look.TintIcons): a one-color copy over the real icon,
    /// switching to full color while the mouse is on the button.
    /// </summary>
    private FrameworkElement TintableIcon(ImageSource icon, double size)
    {
        var color = new Image { Source = icon, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(color, BitmapScalingMode.HighQuality);
        color.SetResourceReference(OpacityProperty, "ColorIconOpacity");

        // One-color copy that keeps the icon's details (see IconColors.Tinted).
        var tint = new Image { Source = _look.TintIcons ? IconColors.Tinted(icon, TintColor) : null, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(tint, BitmapScalingMode.HighQuality);
        tint.SetResourceReference(VisibilityProperty, "TintIconVisibility");

        var grid = new Grid();
        grid.Children.Add(color);
        grid.Children.Add(tint);

        if (_look.TintIcons)
        {
            // Full color while hovered (the whole button, not just the icon).
            grid.Loaded += (_, _) =>
            {
                if (FindParent<Button>(grid) is not { } button) return;
                button.MouseEnter += (_, _) => { color.Opacity = 1; tint.Opacity = 0; };
                button.MouseLeave += (_, _) => { color.SetResourceReference(OpacityProperty, "ColorIconOpacity"); tint.Opacity = 1; };
            };
        }
        return grid;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(child); node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    private FrameworkElement ClockModule(TaskbarModule module)
    {
        string timeFormat = string.IsNullOrEmpty(module.TimeFormat) ? "t" : module.TimeFormat;
        string? dateFormat = module.DateFormat == null ? "d" : module.DateFormat; // "" = no date

        var time = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        var date = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11, Opacity = 0.8 };
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(time);
        if (dateFormat.Length > 0) lines.Children.Add(date);

        void Update()
        {
            var now = DateTime.Now;
            time.Text = Format(now, timeFormat);
            date.Text = dateFormat.Length > 0 ? Format(now, dateFormat) : "";
        }
        Update();
        EverySecond += Update;

        var button = BarButton(lines, "Notifications and calendar (Win+N)");
        button.Click += (_, _) => PressWinShortcut('N');
        return button;
    }

    /// <summary>Formats a date with a format from the design file; a broken format shows itself in brackets.</summary>
    private static string Format(DateTime value, string format)
    {
        try { return value.ToString(format, CultureInfo.CurrentCulture); }
        catch (FormatException) { return $"[{format}?]"; }
    }

    private FrameworkElement BatteryModule()
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        DateTime lastCheck = DateTime.MinValue;

        void Update()
        {
            // Battery doesn't change quickly; checking every 30 seconds is plenty.
            if (DateTime.Now - lastCheck < TimeSpan.FromSeconds(30)) return;
            lastCheck = DateTime.Now;

            var power = Forms.SystemInformation.PowerStatus;
            bool hasBattery = !power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)
                              && power.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;
            text.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
            if (!hasBattery) return;

            int percent = (int)Math.Round(power.BatteryLifePercent * 100);
            bool charging = power.PowerLineStatus == Forms.PowerLineStatus.Online;
            text.Text = charging ? $"{percent}% ⚡" : $"{percent}%";
            text.ToolTip = charging ? $"Battery {percent}%, plugged in" : $"Battery {percent}%";
        }
        Update();
        EverySecond += Update;
        return text;
    }

    private FrameworkElement CustomButtonModule(TaskbarModule module)
    {
        var target = string.IsNullOrWhiteSpace(module.Target) ? null : Environment.ExpandEnvironmentVariables(module.Target.Trim());

        // Icon: a Segoe icon code ("E721"), a file whose icon to use, or the target's own icon.
        FrameworkElement? icon = null;
        if (TryParseGlyph(module.Icon, out var glyph))
            icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Resources["IconFont"], FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        else if (FileIcons.Get(Environment.ExpandEnvironmentVariables(module.Icon ?? target ?? "")) is { } image)
            icon = TintableIcon(image, 20);

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) content.Children.Add(icon);
        if (!string.IsNullOrEmpty(module.Text))
            content.Children.Add(new TextBlock
            {
                Text = module.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(icon != null ? 6 : 0, 0, 0, 0),
            });
        if (content.Children.Count == 0)
            content.Children.Add(new TextBlock { Text = "?", VerticalAlignment = VerticalAlignment.Center });

        var button = BarButton(content, module.ToolTip ?? module.Text ?? target ?? "Button");
        button.Click += (_, _) =>
        {
            if (target == null)
                MessageBox.Show("This button has no target yet. Set \"Target\" in the design file.", "Windows Customizer");
            else
                Launch(target, module.Arguments);
        };
        return button;
    }

    // ---------- Helpers ----------

    private Button BarButton(object content, string? toolTip) => new()
    {
        Style = (Style)Resources["BarButton"],
        Content = content,
        ToolTip = toolTip,
    };

    private Button GlyphButton(string glyph, string toolTip, Action? onClick)
    {
        var button = BarButton(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Resources["IconFont"],
            FontSize = 14,
        }, toolTip);
        if (onClick != null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>"E721" (or "" / "U+E721") becomes that Segoe icon character.</summary>
    private static bool TryParseGlyph(string? text, out string glyph)
    {
        glyph = "";
        var hex = text?.Trim().Replace("\\u", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("U+", "", StringComparison.OrdinalIgnoreCase);
        if (hex is not { Length: >= 4 and <= 5 } ||
            !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
            return false;
        glyph = char.ConvertFromUtf32(code);
        return true;
    }

    /// <summary>Opens a program, file, folder or web address like double-clicking it would.</summary>
    private static void Launch(string target, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = arguments ?? "",
            })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open \"{target}\":\n{ex.Message}", "Windows Customizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>Icons of files (programs, shortcuts, documents), as Windows Explorer shows them.</summary>
internal static class FileIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? result = null;
        if (File.Exists(path) || Directory.Exists(path))
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON);
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    result = image;
                }
                finally
                {
                    DestroyIcon(info.hIcon); // we own icons from SHGetFileInfo
                }
            }
        }

        Cache[path] = result;
        return result;
    }
}
