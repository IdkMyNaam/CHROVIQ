using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The calendar panel (Win+N, or clicking the clock, as the shell, where Windows' own needs Explorer): the time and
/// date, a month calendar, and links to Windows' notification and date settings. It has the Quick Settings look.
/// One at a time; closes when you click elsewhere or press Esc.
/// </summary>
public sealed class CalendarWindow : Window, ILiveGlassWindow
{
    private static CalendarWindow? _open;
    public static bool IsOpen => _open != null;

    public bool WantsLiveGlass => _look.LiveGlass;

    private readonly QuickSettingsLook _look;
    private readonly Color _foreground, _accent;
    private readonly TextBlock _time = new() { FontSize = 30, FontWeight = FontWeights.Light };
    private readonly TextBlock _date = new() { Opacity = 0.8, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _month = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly UniformGrid _days = new() { Columns = 7 };
    private DateTime _shown = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _shownAt = DateTime.MaxValue;
    private bool _closing;

    /// <summary>Opens the calendar, or closes it if it is open.</summary>
    public static void Toggle(QuickSettingsLook look, FrameworkElement? anchor = null, TaskbarEdge edge = TaskbarEdge.Bottom)
    {
        if (_open != null) { _open.CloseQuietly(); return; }
        var window = new CalendarWindow(look);
        _open = window;
        window.ShowAt(anchor, edge);
    }

    private CalendarWindow(QuickSettingsLook look)
    {
        _look = look;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        Width = Math.Clamp(look.Width, 300, 800);

        _foreground = TaskbarView.ParseColor(look.Foreground, Colors.White);
        var windowsAccent = SystemParameters.WindowGlassBrush is SolidColorBrush glass
            ? Color.FromRgb(glass.Color.R, glass.Color.G, glass.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);
        _accent = TaskbarView.ParseColor(look.Accent, windowsAccent);
        Foreground = new SolidColorBrush(_foreground);
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(look.FontFamily) ? "Segoe UI" : look.FontFamily); }
        catch (ArgumentException) { /* keep Segoe UI */ }
        FontSize = Math.Clamp(look.FontSize, 8, 24);

        Content = BuildLayout();
        ShowMonth();
        Tick();
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Tick();
        clock.Start();

        Loaded += (_, _) => Loc.TranslateTree(this);
        Activated += (_, _) => AppTrace.Log("Calendar: has the focus");
        Deactivated += (_, _) =>
        {
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 700)
            {
                AppTrace.Log("Calendar: lost the focus right after opening; taking it back");
                Dispatcher.BeginInvoke(() => { if (!_closing) Activate(); });
                return;
            }
            AppTrace.Log("Calendar: closed (clicked elsewhere)");
            CloseQuietly();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { AppTrace.Log("Calendar: closed (Esc)"); CloseQuietly(); } };
        PreviewMouseLeftButtonDown += (_, e) => AppTrace.Log($"Calendar: pressed at {e.GetPosition(this)}");
        Closed += (_, _) => { clock.Stop(); if (_open == this) _open = null; };
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("T");
        _date.Text = now.ToString("D");
        if (_shown.Year == now.Year && _shown.Month == now.Month && _days.Children.Count > 0 && now.TimeOfDay.TotalSeconds < 1) ShowMonth(); // past midnight
    }

    // ---------- Layout ----------

    private FrameworkElement BuildLayout()
    {
        var root = new StackPanel();
        root.Children.Add(_time);
        root.Children.Add(_date);

        // Month: name in the middle (click: back to today), arrows on the right.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var next = SmallButton("", "Next month", () => { _shown = _shown.AddMonths(1); ShowMonth(); });
        var previous = SmallButton("", "Previous month", () => { _shown = _shown.AddMonths(-1); ShowMonth(); });
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(previous, Dock.Right);
        header.Children.Add(next);
        header.Children.Add(previous);
        var title = new Button { Content = _month, Style = FlatButton(), Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = Loc.T("Today") };
        title.Click += (_, _) => { _shown = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); ShowMonth(); };
        header.Children.Add(title);
        root.Children.Add(header);

        // Weekday names, starting on the first day of the week of your language.
        var names = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
            names.Children.Add(new TextBlock
            {
                Text = format.ShortestDayNames[((int)format.FirstDayOfWeek + i) % 7], Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4),
            });
        root.Children.Add(names);
        root.Children.Add(_days);
        // The mouse wheel goes through the months.
        _days.MouseWheel += (_, e) => { _shown = _shown.AddMonths(e.Delta > 0 ? -1 : 1); ShowMonth(); };

        // Footer: Windows' own settings for notifications and the date and time.
        var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(TextButton("", "Notification settings", () => Launch("ms-settings:notifications")));
        footer.Children.Add(TextButton("", "Date and time", () => Launch("ms-settings:dateandtime")));
        root.Children.Add(footer);

        // The background (and its material) on its own layer behind the content, in the same shape (like Quick Settings).
        var corner = new CornerRadius(Math.Clamp(_look.CornerRadius, 0, 40));
        var frame = new Border { Child = root, Padding = new Thickness(16), CornerRadius = corner };
        var materialLayer = new Border
        {
            CornerRadius = corner,
            Background = new SolidColorBrush(TaskbarView.ParseColor(_look.Background, Color.FromArgb(0xF2, 0x20, 0x20, 0x20))),
        };
        var layers = new Grid();
        layers.Children.Add(materialLayer);
        layers.Children.Add(frame);
        if (MaterialHost.Attach(materialLayer, layers, _look.Material, _look.MaterialValues, _accent) is { } error)
            AppTrace.Log($"Calendar material: {error}");
        return layers;
    }

    /// <summary>The shown month: six weeks, from the first day of the week before the 1st; today marked, other months dimmed.</summary>
    private void ShowMonth()
    {
        _month.Text = _shown.ToString("Y"); // "November 2026", in your language
        _days.Children.Clear();
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int back = ((int)_shown.DayOfWeek - (int)firstDay + 7) % 7;
        var day = _shown.AddDays(-back);
        for (int i = 0; i < 42; i++, day = day.AddDays(1))
        {
            bool today = day == DateTime.Today, inMonth = day.Month == _shown.Month;
            var number = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Opacity = inMonth ? 1 : 0.35, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(today ? Contrast(_accent) : _foreground),
            };
            _days.Children.Add(new Border
            {
                Height = 34, Width = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(1),
                Background = today ? new SolidColorBrush(_accent) : Brushes.Transparent, Child = number,
                ToolTip = day.ToString("D"),
            });
        }
    }

    /// <summary>Black or white, whichever reads better on this color.</summary>
    private static Color Contrast(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 150 ? Colors.Black : Colors.White;

    // ---------- Buttons ----------

    private Style? _flat;

    private Style FlatButton() => _flat ??= (Style)System.Windows.Markup.XamlReader.Parse($"""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Setter Property="Foreground" Value="#{_foreground.R:X2}{_foreground.G:X2}{_foreground.B:X2}" />
          <Setter Property="Focusable" Value="False" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Bd" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" CornerRadius="6" Padding="{"{"}TemplateBinding Padding{"}"}">
                  <ContentPresenter VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="#28{_foreground.R:X2}{_foreground.G:X2}{_foreground.B:X2}" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center,
    };

    private Button SmallButton(string glyph, string tip, Action click)
    {
        var button = new Button { Content = Glyph(glyph, 12), Style = FlatButton(), Padding = new Thickness(9, 7, 9, 7), ToolTip = Loc.T(tip) };
        button.Click += (_, _) => { AppTrace.Log($"Calendar: {tip}"); click(); };
        return button;
    }

    private Button TextButton(string glyph, string text, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Glyph(glyph, 13));
        row.Children.Add(new TextBlock { Text = Loc.T(text), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = row, Style = FlatButton(), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => { AppTrace.Log($"Calendar: {text}"); click(); };
        return button;
    }

    private void Launch(string target)
    {
        try { ShellLaunch.Start(target); }
        catch (Exception ex) { AppTrace.Log($"Calendar: couldn't open {target}: {ex.Message}"); }
        CloseQuietly();
    }

    // ---------- Showing (like Quick Settings) ----------

    private void ShowAt(FrameworkElement? anchor, TaskbarEdge edge)
    {
        if (Content is FrameworkElement content)
            (_look.Opening ?? new PopupAnimation()).Play(content, fromBelow: edge != TaskbarEdge.Top);
        _shownAt = DateTime.Now;
        Show();
        UpdateLayout();
        Place(anchor, edge);
        bool active = Activate();
        AppTrace.Log($"Calendar: opened ({(active ? "active" : "not active")})");
    }

    /// <summary>Above (or below) the clock, its right edge lined up with it; or the bottom right of the monitor.</summary>
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
