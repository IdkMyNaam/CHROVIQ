using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The search window: type to find installed apps (and search the web). Opened by the Windows key
/// (when that setting is on) or by a search box on the custom taskbar. Only one is open at a time;
/// opening it again closes it, like the Start menu.
/// </summary>
public partial class SearchWindow : Window
{
    private static SearchWindow? _open;

    private readonly SearchLook _look;
    private readonly string _searchUrl;
    private List<AppEntry> _apps = new();
    private bool _closing;

    /// <summary>A web search entry in the results list.</summary>
    private sealed record WebSearch(string Text)
    {
        public override string ToString() => $"Search the web for \"{Text}\"";
    }

    public static bool IsOpen => _open != null;

    /// <summary>
    /// Opens the search window, or closes it if it is already open.
    /// </summary>
    /// <param name="anchor">Open next to this element (a search box on the bar); null: the position from the look.</param>
    /// <param name="edge">The bar's edge, so the window opens toward the middle of the screen.</param>
    /// <param name="searchUrl">Web search address for this opening (a search box's own setting), or null for the look's.</param>
    public static SearchWindow? Toggle(SearchLook look, FrameworkElement? anchor = null,
        TaskbarEdge edge = TaskbarEdge.Top, string? searchUrl = null)
    {
        if (_open != null)
        {
            _open.CloseQuietly();
            return null;
        }
        var window = new SearchWindow(look, searchUrl);
        _open = window;
        window.ShowAt(anchor, edge);
        return window;
    }

    private SearchWindow(SearchLook look, string? searchUrl)
    {
        _look = look;
        _searchUrl = !string.IsNullOrWhiteSpace(searchUrl) ? searchUrl!
            : !string.IsNullOrWhiteSpace(look.SearchUrl) ? look.SearchUrl!
            : TaskbarModule.DefaultSearchUrl;
        InitializeComponent();
        ApplyLook();

        Results.ItemTemplate = ResultTemplate();
        Box.TextChanged += (_, _) => UpdateResults();
        Box.PreviewKeyDown += OnKey;
        Results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is { } item and (AppEntry or WebSearch))
                Run(item);
        };

        // Clicking anywhere else closes it, like the Start menu.
        Deactivated += (_, _) => CloseQuietly();
        Closed += (_, _) => { if (_open == this) _open = null; };
        Loaded += async (_, _) =>
        {
            _apps = await AppSearch.Index(); // built in the background on first use
            UpdateResults();
        };
    }

    private void ApplyLook()
    {
        var foreground = TaskbarView.ParseColor(_look.Foreground, Colors.White);
        var accent = SystemParameters.WindowGlassBrush is SolidColorBrush glass
            ? Color.FromRgb(glass.Color.R, glass.Color.G, glass.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);
        var highlight = TaskbarView.ParseColor(_look.Highlight, accent);

        Resources["SearchBackground"] = new SolidColorBrush(TaskbarView.ParseColor(_look.Background, Color.FromArgb(0xF2, 0x20, 0x20, 0x20)));
        Resources["SearchForeground"] = new SolidColorBrush(foreground);
        Resources["SearchHighlight"] = new SolidColorBrush(Color.FromArgb(0xCC, highlight.R, highlight.G, highlight.B));
        Resources["SearchHover"] = new SolidColorBrush(Color.FromArgb(0x22, foreground.R, foreground.G, foreground.B));

        Width = Math.Clamp(_look.Width, 240, 1600);
        Frame.CornerRadius = new CornerRadius(Math.Clamp(_look.CornerRadius, 0, 40));
        FontSize = Math.Clamp(_look.FontSize, 8, 40);
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_look.FontFamily) ? "Segoe UI" : _look.FontFamily); }
        catch (ArgumentException) { /* bad font name: keep Segoe UI */ }
        Placeholder.Text = string.IsNullOrWhiteSpace(_look.Placeholder) ? "Search apps and the web" : _look.Placeholder;
    }

    // ---------- Showing ----------

    private void ShowAt(FrameworkElement? anchor, TaskbarEdge edge)
    {
        Show();
        UpdateLayout();
        PlaceWindow(anchor, edge);
        TakeFocus();
        Box.Focus();
        Keyboard.Focus(Box);
    }

    /// <summary>Next to the search box on the bar, or on the monitor with the mouse (centered or at the top).</summary>
    private void PlaceWindow(FrameworkElement? anchor, TaskbarEdge edge)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetCursorPos(out var cursor);
        var reference = cursor;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        RECT anchorRect = default;
        if (anchor != null && PresentationSource.FromVisual(anchor) != null)
        {
            scale = VisualTreeHelper.GetDpi(anchor).DpiScaleX;
            var topLeft = anchor.PointToScreen(new Point(0, 0));
            anchorRect = new RECT
            {
                Left = (int)topLeft.X, Top = (int)topLeft.Y,
                Right = (int)(topLeft.X + anchor.ActualWidth * scale), Bottom = (int)(topLeft.Y + anchor.ActualHeight * scale),
            };
            reference = new POINT { X = anchorRect.Left, Y = anchorRect.Top };
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(reference, MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;

        int width = (int)(ActualWidth * scale);
        int x, y;
        if (anchor != null && anchorRect.Right > 0)
        {
            int gap = (int)(8 * scale);
            x = anchorRect.Left;
            // Above a bottom bar the window grows upward as results appear: measure from its bottom later.
            y = edge == TaskbarEdge.Top ? anchorRect.Bottom + gap : anchorRect.Top - gap;
            _growUpFrom = edge == TaskbarEdge.Bottom ? y : null;
        }
        else
        {
            x = work.Left + (work.Right - work.Left - width) / 2;
            y = _look.Position == SearchPosition.Top
                ? work.Top + (int)(24 * scale)
                : work.Top + (work.Bottom - work.Top) / 4;
            _growUpFrom = null;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        _placedX = x;
        _placedY = y;
        _scale = scale;
        MoveTo(hwnd);
    }

    private int _placedX, _placedY;
    private int? _growUpFrom;
    private double _scale = 1;

    private void MoveTo(IntPtr hwnd)
    {
        int height = (int)(ActualHeight * _scale);
        int y = _growUpFrom is int bottom ? bottom - height : _placedY;
        SetWindowPos(hwnd, HWND_TOPMOST, _placedX, y, 0, 0, 0x0001 /* SWP_NOSIZE */);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        // Opened above a bottom bar: keep the bottom edge in place while results come and go.
        if (_growUpFrom != null) MoveTo(new WindowInteropHelper(this).Handle);
    }

    [DllImport("user32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);

    /// <summary>
    /// Brings the window to the front with the keyboard. Opened by the Windows key, our app isn't the
    /// foreground app, and Windows normally refuses then; joining the foreground app's input for a moment is
    /// the usual way around that.
    /// </summary>
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

    // ---------- Searching ----------

    private void UpdateResults()
    {
        Placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = Box.Text.Trim();
        if (text.Length == 0)
        {
            Results.ItemsSource = null;
            Results.Visibility = Visibility.Collapsed;
            return;
        }

        var items = new List<object>(AppSearch.Find(_apps, text, Math.Clamp(_look.MaxResults, 1, 30)));
        if (_look.ShowWebSearch) items.Add(new WebSearch(text));
        Results.ItemsSource = items;
        Results.SelectedIndex = items.Count > 0 ? 0 : -1;
        Results.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when Results.Items.Count > 0:
                Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                var text = Box.Text.Trim();
                if (text.Length > 0) Run(Results.SelectedItem ?? new WebSearch(text));
                e.Handled = true;
                break;
            case Key.Escape:
                CloseQuietly();
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
                    var query = Uri.EscapeDataString(web.Text);
                    var url = _searchUrl.Contains("{0}") ? _searchUrl.Replace("{0}", query) : _searchUrl + query;
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open it:\n{ex.Message}", "Windows Customizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        CloseQuietly();
    }

    /// <summary>A result row: the app's icon (Store apps and web search get a symbol) and its name.</summary>
    private DataTemplate ResultTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));

        var icon = new FrameworkElementFactory(typeof(Grid));
        icon.SetValue(WidthProperty, 24.0);
        icon.SetValue(HeightProperty, 24.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 12, 0));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(AppEntry.Icon)) { FallbackValue = null });
        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        icon.AppendChild(image);

        // Symbol for entries without an icon (Store apps, web search); hidden behind a real icon.
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new GlyphConverter() });
        glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        glyph.SetValue(TextBlock.FontSizeProperty, 16.0);
        glyph.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.AppendChild(glyph);
        row.AppendChild(icon);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(name);

        return new DataTemplate { VisualTree = row };
    }

    /// <summary>Globe for web search, app symbol for apps without a file icon, nothing otherwise.</summary>
    private sealed class GlyphConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value switch
        {
            WebSearch => "",
            AppEntry { IsShellApp: true } => "",
            _ => "",
        };

        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
