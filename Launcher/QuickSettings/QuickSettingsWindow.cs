using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Windows.Devices.Radios;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Windows Customizer's own Quick Settings: Wi-Fi, Bluetooth, night light and mute as tiles, brightness and
/// volume sliders, battery. Works in shell mode, where Windows' own (Win+A) doesn't. Click a tile to switch it;
/// right-click it for its Windows settings page. Opens next to the button on the bar; one at a time; closes
/// when you click elsewhere.
/// </summary>
public sealed class QuickSettingsWindow : Window, ILiveGlassWindow
{
    private static QuickSettingsWindow? _open;
    public static bool IsOpen => _open != null;

    public bool WantsLiveGlass => _look.LiveGlass;

    private readonly QuickSettingsLook _look;
    private readonly Action? _openCustomizer;
    private Color _accent, _foreground;
    private bool _closing;
    private MonitorBrightness? _brightness;
    private readonly List<Action> _cleanup = new();

    /// <summary>Opens the panel, or closes it if it's open.</summary>
    public static void Toggle(QuickSettingsLook look, FrameworkElement? anchor = null, TaskbarEdge edge = TaskbarEdge.Bottom, Action? openCustomizer = null)
    {
        if (_open != null) { _open.CloseQuietly(); return; }
        var window = new QuickSettingsWindow(look, openCustomizer);
        _open = window;
        window.ShowAt(anchor, edge);
    }

    private QuickSettingsWindow(QuickSettingsLook look, Action? openCustomizer)
    {
        _look = look;
        _openCustomizer = openCustomizer;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        Width = Math.Clamp(look.Width, 280, 800);

        _foreground = TaskbarView.ParseColor(look.Foreground, Colors.White);
        var windowsAccent = SystemParameters.WindowGlassBrush is SolidColorBrush glass
            ? Color.FromRgb(glass.Color.R, glass.Color.G, glass.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);
        _accent = TaskbarView.ParseColor(look.Accent, windowsAccent);
        Resources["QsForeground"] = new SolidColorBrush(_foreground);
        Resources["QsAccent"] = new SolidColorBrush(_accent);
        Resources["QsTile"] = new SolidColorBrush(Color.FromArgb(0x1E, _foreground.R, _foreground.G, _foreground.B));
        Resources["QsTileHover"] = new SolidColorBrush(Color.FromArgb(0x30, _foreground.R, _foreground.G, _foreground.B));
        Resources["QsTrack"] = new SolidColorBrush(Color.FromArgb(0x40, _foreground.R, _foreground.G, _foreground.B));
        Resources[typeof(Slider)] = SliderStyle();
        SetResourceReference(ForegroundProperty, "QsForeground");
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(look.FontFamily) ? "Segoe UI" : look.FontFamily); }
        catch (ArgumentException) { /* keep Segoe UI */ }
        FontSize = Math.Clamp(look.FontSize, 8, 24);

        Content = BuildLayout();
        Loaded += (_, _) => Loc.TranslateTree(this); // its texts in the chosen language
        Deactivated += (_, _) => CloseQuietly();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseQuietly(); };
        Closed += (_, _) =>
        {
            if (_open == this) _open = null;
            foreach (var cleanup in _cleanup) cleanup();
            _brightness?.Dispose();
        };
    }

    // ---------- Layout ----------

    private FrameworkElement BuildLayout()
    {
        var root = new StackPanel();

        // Tiles: three per row.
        var tiles = new UniformGrid { Columns = 3 };
        if (_look.ShowWifi) tiles.Children.Add(RadioTile(RadioKind.WiFi, "", "Wi-Fi", "ms-settings:network-wifi"));
        if (_look.ShowBluetooth) tiles.Children.Add(RadioTile(RadioKind.Bluetooth, "", "Bluetooth", "ms-settings:bluetooth"));
        if (_look.ShowNightLight) tiles.Children.Add(NightLightTile());
        if (_look.ShowMute) tiles.Children.Add(MuteTile());
        if (tiles.Children.Count > 0) root.Children.Add(tiles);

        // Sliders.
        if (_look.ShowBrightness) root.Children.Add(BrightnessRow());
        if (_look.ShowVolume) root.Children.Add(VolumeRow());

        // Footer: battery on the left; Windows Customizer and all settings on the right.
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var settings = FooterButton("", "All settings", () => Launch("ms-settings:"));
        DockPanel.SetDock(settings, Dock.Right);
        footer.Children.Add(settings);
        if (_openCustomizer != null)
        {
            var customizer = FooterButton("", "Windows Customizer", () => { CloseQuietly(); _openCustomizer(); });
            DockPanel.SetDock(customizer, Dock.Right);
            footer.Children.Add(customizer);
        }
        if (_look.ShowBattery && BatteryText() is { } battery)
            footer.Children.Add(new TextBlock { Text = battery, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        else footer.Children.Add(new Border());
        root.Children.Add(footer);

        // The background (and its material) on its own layer behind the content, in the same shape.
        var corner = new CornerRadius(Math.Clamp(_look.CornerRadius, 0, 40));
        var frame = new Border { Child = root, Padding = new Thickness(14), CornerRadius = corner };
        var materialLayer = new Border
        {
            CornerRadius = corner,
            Background = new SolidColorBrush(TaskbarView.ParseColor(_look.Background, Color.FromArgb(0xF2, 0x20, 0x20, 0x20))),
        };
        var layers = new Grid();
        layers.Children.Add(materialLayer);
        layers.Children.Add(frame);
        if (MaterialHost.Attach(materialLayer, layers, _look.Material, _look.MaterialValues, _accent) is { } error)
            AppTrace.Log($"Quick Settings material: {error}");
        return layers;
    }

    /// <summary>A tile: symbol, name and a small status line; accent-colored while on. Right-click opens its settings.</summary>
    private (Border Tile, Action<bool?, string> Show) Tile(string glyph, string name, string settingsPage, Action onClick)
    {
        var icon = Glyph(glyph, 16);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { FontSize = Math.Max(9, FontSize - 2), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(status);
        var tile = new Border { Child = stack, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 10, 10, 8), Margin = new Thickness(3), Cursor = Cursors.Hand, ToolTip = $"{name} (right-click: settings)" };
        bool on = false;
        void Paint()
        {
            if (on) tile.Background = new SolidColorBrush(_accent);
            else tile.SetResourceReference(Border.BackgroundProperty, tile.IsMouseOver ? "QsTileHover" : "QsTile");
            // Readable text on the accent: dark or light, whichever contrasts.
            var text = on ? (Luminance(_accent) > 0.55 ? Colors.Black : Colors.White) : _foreground;
            stack.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new SolidColorBrush(text));
            tile.Opacity = tile.IsEnabled ? 1 : 0.5;
        }
        tile.MouseEnter += (_, _) => Paint();
        tile.MouseLeave += (_, _) => Paint();
        tile.MouseLeftButtonUp += (_, _) => { if (tile.IsEnabled) onClick(); };
        Accessibility.Clickable(tile, name, () => { if (tile.IsEnabled) onClick(); });
        tile.MouseRightButtonUp += (_, _) => Launch(settingsPage);
        Paint();
        return (tile, (state, text) =>
        {
            on = state == true;
            tile.IsEnabled = state != null;
            status.Text = text;
            Paint();
        });
    }

    private UIElement RadioTile(RadioKind kind, string glyph, string name, string settingsPage)
    {
        Radio? radio = null;
        (Border tile, Action<bool?, string> show) = (null!, null!);
        void Update()
        {
            if (radio == null) { show(null, "Not available"); return; }
            bool on = radio.State == RadioState.On;
            string text = !on ? "Off" : kind == RadioKind.WiFi ? SystemRadios.ConnectedNetwork() ?? "On" : "On";
            show(on, text);
        }
        (tile, show) = Tile(glyph, name, settingsPage, async () =>
        {
            if (radio == null) return;
            await SystemRadios.SetAsync(radio, radio.State != RadioState.On);
        });
        show(null, "...");
        Loaded += async (_, _) =>
        {
            radio = await SystemRadios.FindAsync(kind);
            if (_closing) return;
            if (radio != null)
            {
                Windows.Foundation.TypedEventHandler<Radio, object> changed = (_, _) => Dispatcher.InvokeAsync(Update);
                radio.StateChanged += changed;
                _cleanup.Add(() => radio.StateChanged -= changed);
            }
            Update();
        };
        return tile;
    }

    private UIElement NightLightTile()
    {
        (Border tile, Action<bool?, string> show) = (null!, null!);
        void Update()
        {
            var state = NightLight.IsOn();
            // Unknown layout: the tile still works, as a way to Windows' Night light settings.
            if (state == null) { show(false, "Settings"); return; }
            show(state, state == true ? "On" : "Off");
        }
        (tile, show) = Tile("", "Night light", "ms-settings:nightlight", () =>
        {
            var state = NightLight.IsOn();
            if (state == null || !NightLight.Set(state != true)) { Launch("ms-settings:nightlight"); return; }
            Update();
        });
        Update();
        return tile;
    }

    private UIElement MuteTile()
    {
        (Border tile, Action<bool?, string> show) = (null!, null!);
        void Update()
        {
            if (AudioVolume.Get() is not { } volume) { show(null, "No speakers"); return; }
            show(volume.muted, volume.muted ? "Muted" : "Sound on");
        }
        (tile, show) = Tile("", "Mute", "ms-settings:sound", AudioVolume.ToggleMute);
        Action changed = () => Dispatcher.InvokeAsync(Update);
        AudioVolume.Changed += changed;
        _cleanup.Add(() => AudioVolume.Changed -= changed);
        Update();
        return tile;
    }

    private UIElement VolumeRow()
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var icon = Glyph("", 16);
        bool updating = false;
        void Update()
        {
            if (AudioVolume.Get() is not { } v) { slider.IsEnabled = false; value.Text = ""; return; }
            updating = true;
            slider.Value = v.percent;
            updating = false;
            value.Text = v.percent.ToString();
            icon.Text = v.muted ? "" : "";
        }
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            AudioVolume.Set((int)Math.Round(slider.Value));
            value.Text = ((int)Math.Round(slider.Value)).ToString();
        };
        Action changed = () => Dispatcher.InvokeAsync(Update);
        AudioVolume.Changed += changed;
        _cleanup.Add(() => AudioVolume.Changed -= changed);
        Update();
        var mute = FlatIconButton(icon, "Mute", AudioVolume.ToggleMute);
        return SliderRow(mute, slider, value);
    }

    /// <summary>
    /// One brightness slider per monitor that allows it (with its model name when there are several), and
    /// "Put back" to return every monitor to the level it had when the panel opened.
    /// </summary>
    private UIElement BrightnessRow()
    {
        var rows = new StackPanel { Visibility = Visibility.Collapsed };
        // Asking the monitors takes a moment: in the background; the rows show once they answer (hidden if none can).
        Loaded += async (_, _) =>
        {
            var found = await MonitorBrightness.OpenAsync();
            if (_closing) { found?.Dispose(); return; }
            if (found == null) return;
            _brightness = found;

            var putBack = new TextBlock
            {
                Text = "Put back", Cursor = Cursors.Hand, Opacity = 0.8, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0), TextDecorations = TextDecorations.Underline,
                ToolTip = "Every monitor back to the brightness it had when this panel opened",
            };
            var resets = new List<Action>();
            bool several = found.Screens.Count > 1;
            foreach (var screen in found.Screens)
            {
                var slider = new Slider { Minimum = 0, Maximum = 100, Value = screen.StartPercent, VerticalAlignment = VerticalAlignment.Center };
                var value = new TextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = screen.StartPercent.ToString() };
                bool resetting = false;
                slider.ValueChanged += (_, _) =>
                {
                    int percent = (int)Math.Round(slider.Value);
                    value.Text = percent.ToString();
                    found.Set(screen, percent);
                    if (!resetting) putBack.Visibility = Visibility.Visible;
                };
                resets.Add(() => { resetting = true; slider.Value = screen.StartPercent; found.Set(screen, screen.StartPercent); resetting = false; });

                var icon = FlatIconButton(Glyph("\uE706", 16), "Display settings", () => Launch("ms-settings:display"));
                var row = SliderRow(icon, slider, value);
                if (several)
                {
                    var labeled = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                    labeled.Children.Add(new TextBlock { Text = screen.Name, FontSize = Math.Max(9, FontSize - 2), Opacity = 0.7, Margin = new Thickness(2, 0, 0, -6) });
                    labeled.Children.Add(row);
                    rows.Children.Add(labeled);
                }
                else rows.Children.Add(row);
            }
            putBack.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var reset in resets) reset();
                putBack.Visibility = Visibility.Collapsed;
            };
            rows.Children.Add(putBack);
            rows.Visibility = Visibility.Visible;
        };
        return rows;
    }

    private static FrameworkElement SliderRow(UIElement icon, Slider slider, TextBlock value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(value);
        slider.Margin = new Thickness(8, 0, 4, 0);
        row.Children.Add(slider);
        return row;
    }

    private Border FlatIconButton(TextBlock icon, string toolTip, Action onClick)
    {
        var button = new Border { Child = icon, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, ToolTip = toolTip, Cursor = Cursors.Hand };
        button.MouseEnter += (_, _) => button.SetResourceReference(Border.BackgroundProperty, "QsTileHover");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, _) => onClick();
        Accessibility.Clickable(button, toolTip, onClick);
        return button;
    }

    private Border FooterButton(string glyph, string toolTip, Action onClick)
    {
        var button = FlatIconButton(Glyph(glyph, 16), toolTip, onClick);
        button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center,
    };

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    /// <summary>"Battery 85 % (charging)", or null on a PC without a battery.</summary>
    private static string? BatteryText()
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) || power.BatteryLifePercent > 1) return null;
        string text = $"Battery {Math.Round(power.BatteryLifePercent * 100)} %";
        return power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? text + " (plugged in)" : text;
    }

    private void Launch(string target)
    {
        try { ShellLaunch.Start(target, null); }
        catch (Exception ex) { AppTrace.Log($"Quick Settings: {target}: {ex.Message}"); }
        CloseQuietly();
    }

    /// <summary>A thin track with the accent-colored part up to the thumb, and a round thumb.</summary>
    private static Style SliderStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Setter Property="Focusable" Value="False" />
          <Setter Property="IsMoveToPointEnabled" Value="True" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Slider">
                <Grid Height="20" Background="Transparent">
                  <Border Height="4" CornerRadius="2" Background="{DynamicResource QsTrack}" VerticalAlignment="Center" />
                  <Track x:Name="PART_Track">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="RepeatButton">
                            <Border Height="4" CornerRadius="2" Background="{DynamicResource QsAccent}" VerticalAlignment="Center" />
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb Width="16" Height="16">
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Ellipse Fill="{DynamicResource QsAccent}" Stroke="{DynamicResource QsForeground}" StrokeThickness="2" />
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    // ---------- Showing ----------

    private void ShowAt(FrameworkElement? anchor, TaskbarEdge edge)
    {
        // The start state goes on before the window shows, so it never flashes up fully drawn first.
        if (Content is FrameworkElement content)
            (_look.Opening ?? new PopupAnimation()).Play(content, fromBelow: edge != TaskbarEdge.Top);
        Show();
        UpdateLayout();
        Place(anchor, edge);
        Activate();
    }

    /// <summary>Above (or below) the button, its right edge lined up with the button's; or the bottom right of the monitor.</summary>
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
        int width = (int)(ActualWidth * scale), height = (int)(ActualHeight * scale), gap = (int)(8 * scale);
        int x, y;
        if (anchorRect is { } a)
        {
            x = a.Right - width;
            y = edge == TaskbarEdge.Top ? a.Bottom + gap : a.Top - height - gap;
        }
        else
        {
            x = work.Right - width - gap;
            y = work.Bottom - height - gap;
        }
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, 0);
    }

    private void CloseQuietly()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
