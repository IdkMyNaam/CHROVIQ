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
            bool first = true;
            foreach (var zone in new[] { _design.Left, _design.Center, _design.Right }.Where(z => z.Count > 0))
            {
                if (!first && _look.ZoneDividers) AddDivider(row);
                first = false;
                foreach (var module in zone)
                {
                    var element = BuildModuleSafely(module);
                    DockPanel.SetDock(element, Dock.Left);
                    row.Children.Add(element);
                }
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

        // Zone dividers: a line wherever two zones with modules meet.
        if (_look.ZoneDividers)
        {
            bool hasLeft = _design.Left.Count > 0, hasRight = _design.Right.Count > 0;
            if (hasCenter)
            {
                if (hasLeft) AddDivider(center, atStart: true);
                if (hasRight) AddDivider(center);
            }
            else if (hasLeft && hasRight) AddDivider(right, atStart: true);
        }
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

    /// <summary>A thin vertical line between zones (Look.ZoneDividers), at the start or the end of a zone.</summary>
    private void AddDivider(DockPanel zone, bool atStart = false)
    {
        var foreground = ParseColor(_look.Foreground, Colors.White);
        var line = new System.Windows.Shapes.Rectangle
        {
            Width = 1,
            Margin = new Thickness(6, Math.Max(4, BarHeight * 0.22), 6, Math.Max(4, BarHeight * 0.22)),
            Fill = new SolidColorBrush(ParseColor(_look.DividerColor, Color.FromArgb(0x50, foreground.R, foreground.G, foreground.B))),
            IsHitTestVisible = false,
        };
        DockPanel.SetDock(line, Dock.Left);
        if (atStart) zone.Children.Insert(0, line);
        else zone.Children.Add(line);
    }

    /// <summary>A broken module (bad options in a hand-edited file) shows a warning sign instead of breaking the bar.</summary>
    private FrameworkElement BuildModuleSafely(TaskbarModule module)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var element = BuildModule(module);
            if (clock.ElapsedMilliseconds > 15) AppTrace.Log($"Taskbar: the {module.Type} module took {clock.ElapsedMilliseconds} ms to build");
            return element;
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
        ModuleType.Start => StartModule(module),
        ModuleType.WindowList => WindowListModule(),
        ModuleType.PinnedApps => PinnedAppsModule(module),
        ModuleType.Clock => ClockModule(module),
        ModuleType.Battery => BatteryModule(),
        ModuleType.QuickSettings => QuickSettingsButton(module),
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
        ModuleType.Tray => TrayModule(module),
        ModuleType.Desktops => DesktopsModule(module),
        _ => throw new NotSupportedException("Unknown module type."),
    };

    // ---------- Modules ----------

    private FrameworkElement MenuModule()
    {
        var button = GlyphButton("", "CHROVIQ", null);
        button.Click += (_, _) => ShowAppMenu(button);
        return button;
    }

    /// <summary>Four small squares, a generic "apps" symbol; opens the Windows Start menu.</summary>
    private FrameworkElement StartModule(TaskbarModule module)
    {
        var squares = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2, Width = 14, Height = 14 };
        for (int i = 0; i < 4; i++)
        {
            var square = new Border { Margin = new Thickness(0.75), CornerRadius = new CornerRadius(1) };
            square.SetResourceReference(Border.BackgroundProperty, "BarForeground"); // text color of the design
            squares.Children.Add(square);
        }

        var button = BarButton(squares, "Start");
        button.Click += (_, _) =>
        {
            // Our start menu (next to this button) when the design asks for it, otherwise Windows' own.
            if (module.CustomStart == true && StartMenuOpener != null) StartMenuOpener(button, Edge);
            else PressWinKey();
        };
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
                ? TintableIcon(icon, Math.Clamp(_look.WindowIconSize ?? 20, 8, 64))
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
            }
            button.ContextMenu = RunningAppMenu(running, path, app, dockStyle);
            _pinOf[button] = new PinTag(module, app);
            AttachReorder(button);
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
    private ContextMenu RunningAppMenu(TaskGroup running, string path, string pinned, bool dockStyle)
    {
        // Built when it opens, so "Close" knows whether the app runs right now.
        var menu = ModernMenu.Create();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            ModernMenu.Item(menu, NewGlyph, "Open new", () => Launch(path, null));
            ModernMenu.Item(menu, UnpinGlyph, "Unpin from taskbar", () => Unpin(pinned));
            if (!dockStyle) return;
            bool runs = running.Windows.Count > 0;
            ModernMenu.Separator(menu);
            ModernMenu.Item(menu, CloseGlyph, running.Windows.Count > 1 ? "Close all windows" : "Close window",
                () => { foreach (var w in running.Windows) WindowTracker.Close(w.Handle); }, enabled: runs);
            ModernMenu.Item(menu, ForceGlyph, "Force close (unsaved work is lost)", () => ForceCloseAll(running.Windows.ToList()), enabled: runs);
        };
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
        // The clock's own font and size, if set.
        if (!string.IsNullOrWhiteSpace(module.FontFamily))
        {
            try { time.FontFamily = date.FontFamily = new FontFamily(module.FontFamily.Trim()); }
            catch (ArgumentException) { /* bad font name: keep the bar's font */ }
        }
        if (module.FontSize is double size and > 0)
        {
            time.FontSize = Math.Clamp(size, 6, 48);
            date.FontSize = Math.Max(6, time.FontSize - 1);
        }
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

        // Without seconds on display, update right after each full minute instead of every second.
        bool seconds = ShowsSeconds(timeFormat) || ShowsSeconds(dateFormat);
        TimeSpan UntilNext() => seconds ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMilliseconds(60_050 - DateTime.Now.Second * 1000 - DateTime.Now.Millisecond);
        System.Windows.Threading.DispatcherTimer? timer = null;
        timer = ModuleTimer(UntilNext(), () => { Update(); timer!.Interval = UntilNext(); });
        // The clock was changed, or the PC woke up from sleep: show the right time at once.
        EventHandler onTimeChanged = (_, _) => Dispatcher.InvokeAsync(() => { Update(); timer.Interval = UntilNext(); });
        Microsoft.Win32.SystemEvents.TimeChanged += onTimeChanged;
        Microsoft.Win32.PowerModeChangedEventHandler onPower = (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) onTimeChanged(null, EventArgs.Empty); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += onPower;
        _disposables.Add(new Unsubscribe(() =>
        {
            Microsoft.Win32.SystemEvents.TimeChanged -= onTimeChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= onPower;
        }));

        var button = BarButton(lines, "Notifications and calendar (Win+N)");
        // As the shell, Windows' own panel needs Explorer: CHROVIQ's calendar instead.
        button.Click += (_, _) =>
        {
            if (ShellMode.IsShell && CalendarOpener != null) CalendarOpener(button, Edge);
            else PressWinShortcut('N');
        };
        return button;
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>Whether a date/time format shows seconds ("T" = long time, or "s" outside quotes in a custom format).</summary>
    private static bool ShowsSeconds(string format)
    {
        if (format.Length == 1) return format is "T" or "G" or "F" or "U" or "R" or "r" or "s" or "u" or "O" or "o";
        bool quoted = false;
        char quote = '\0';
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\') { i++; continue; }
            if (quoted) { if (c == quote) quoted = false; continue; }
            if (c is '\'' or '"') { quoted = true; quote = c; continue; }
            if (c == 's') return true;
        }
        return false;
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
        void Update()
        {
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
        ModuleTimer(TimeSpan.FromSeconds(30), Update); // battery doesn't change quickly
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
                MessageBox.Show("This button has no target yet. Set \"Target\" in the design file.", "CHROVIQ");
            else
                Launch(target, module.Arguments);
        };
        return button;
    }

    // ---------- Helpers ----------

    private Button BarButton(object content, string? toolTip) => Accessibility.Named(new Button
    {
        Style = (Style)Resources["BarButton"],
        Content = content,
        ToolTip = toolTip,
    }, toolTip);

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
            ShellLaunch.Start(target, arguments);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open \"{target}\":\n{ex.Message}", "CHROVIQ",
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
        // Store apps pinned to the taskbar ("shell:AppsFolder\...") have no file: ask the shell.
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return ShellIcons.Get(path, 32);
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
