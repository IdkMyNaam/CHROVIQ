using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Our Alt+Tab: a window switcher in the Windows 11 look with live previews of every window, most recently
/// used first. Alt+Tab / Alt+Shift+Tab step through, releasing Alt switches, Esc cancels, arrow keys move,
/// Delete closes the selected window, a click picks one. A quick Alt+Tab (released at once) switches to
/// the previous window without showing anything, like Windows.
/// </summary>
internal sealed class AltTabSwitcher : IDisposable
{
    private readonly AltTabHook _hook = new();
    private readonly SwitcherWindow _window = new();
    private readonly Func<AltTabLook> _look;
    private readonly DispatcherTimer _showDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private List<(IntPtr Handle, string Title)> _windows = new();
    private int _index;

    /// <param name="look">The look to use; asked each time the panel opens, so changes apply at once.</param>
    public AltTabSwitcher(Func<AltTabLook> look)
    {
        _look = look;
        // The hook's events come from inside the keyboard hook: do the work on the UI thread, a moment later.
        var ui = Dispatcher.CurrentDispatcher;
        _hook.Opened += (backwards, sticky) => ui.BeginInvoke(() => Open(backwards, sticky));
        _hook.Stepped += backwards => ui.BeginInvoke(() => Step(backwards ? -1 : 1));
        _hook.Committed += () => ui.BeginInvoke(Commit);
        _hook.Cancelled += () => ui.BeginInvoke(Close);
        _hook.KeyPressed += key => ui.BeginInvoke(() => OnKey(key));
        _showDelay.Tick += (_, _) => { _showDelay.Stop(); ShowWindow(); };
        _window.Picked += index => { _index = index; _hook.Close(); Commit(); };
    }

    /// <summary>"Try it" in the settings: the panel with the current windows for a few seconds.</summary>
    public static void Preview(AltTabLook look)
    {
        var windows = Listed(look);
        if (windows.Count == 0) return;
        var window = new SwitcherWindow();
        window.ShowList(windows, Math.Min(1, windows.Count - 1), look);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); window.HideList(); window.Close(); };
        timer.Start();
    }

    private void Open(bool backwards, bool sticky)
    {
        AppTrace.Log($"Alt+Tab: open (backwards {backwards}, sticky {sticky})");
        var look = _look();
        _windows = Listed(look); // front to back = most recently used first
        AppTrace.Log($"Alt+Tab: {_windows.Count} windows");
        if (_windows.Count == 0) { _hook.Close(); return; }
        // The window in front is the current one: start at the next (or, backwards, the last).
        _index = _windows.Count == 1 ? 0 : backwards ? _windows.Count - 1 : 1;
        if (sticky || look.ShowDelayMs <= 0) { ShowWindow(); return; } // Ctrl+Alt+Tab, or no delay: at once
        _showDelay.Interval = TimeSpan.FromMilliseconds(Math.Clamp(look.ShowDelayMs, 1, 1000));
        _showDelay.Start();
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    private const uint MONITOR_NEAREST = 2, MONITOR_PRIMARY = 1;

    /// <summary>The monitor the panel opens on (Alt+Tab settings: under the mouse, the active window's, or the main one).</summary>
    internal static IntPtr PanelMonitor(AltTabLook look)
    {
        switch (look.Monitor)
        {
            case AltTabMonitor.ActiveWindow:
                var active = GetForegroundWindow();
                if (active != IntPtr.Zero) return MonitorFromWindow(active, MONITOR_NEAREST);
                break;
            case AltTabMonitor.Main:
                return MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_PRIMARY);
        }
        GetCursorPos(out var mouse);
        return MonitorFromPoint(mouse, MONITOR_NEAREST);
    }

    /// <summary>The windows to list: all (most recently used first), without minimized ones or other monitors' if so set.</summary>
    private static List<(IntPtr Handle, string Title)> Listed(AltTabLook look)
    {
        var windows = WindowTracker.FindTaskWindows();
        if (!look.IncludeMinimized) windows = windows.Where(w => !IsIconic(w.hwnd)).ToList();
        if (look.OnlyThisMonitor)
        {
            var monitor = PanelMonitor(look);
            windows = windows.Where(w => MonitorFromWindow(w.hwnd, MONITOR_NEAREST) == monitor).ToList();
        }
        return windows;
    }

    private void ShowWindow()
    {
        if (!_hook.IsOpen || _windows.Count == 0) return;
        AppTrace.Log("Alt+Tab: showing the panel");
        _window.ShowList(_windows, _index, _look());
        AppTrace.Log("Alt+Tab: panel shown");
    }

    private void Step(int direction)
    {
        if (_windows.Count == 0) return;
        _index = ((_index + direction) % _windows.Count + _windows.Count) % _windows.Count;
        _window.Select(_index);
    }

    private void OnKey(AltTabHook.SwitcherKey key)
    {
        switch (key)
        {
            case AltTabHook.SwitcherKey.Left: Step(-1); break;
            case AltTabHook.SwitcherKey.Right: Step(1); break;
            case AltTabHook.SwitcherKey.Up: Step(-_window.Columns); break;
            case AltTabHook.SwitcherKey.Down: Step(_window.Columns); break;
            case AltTabHook.SwitcherKey.Delete:
                if (_index < _windows.Count)
                {
                    WindowTracker.Close(_windows[_index].Handle);
                    _windows.RemoveAt(_index);
                    if (_windows.Count == 0) { _hook.Close(); Close(); return; }
                    _index = Math.Min(_index, _windows.Count - 1);
                    if (_window.IsVisible) _window.ShowList(_windows, _index, _look());
                }
                break;
        }
    }

    private void Commit()
    {
        _showDelay.Stop();
        var target = _index < _windows.Count ? _windows[_index] : default;
        AppTrace.Log($"Alt+Tab: switch to \"{target.Title}\" ({target.Handle})");
        Close();
        // An error here must never take the app (and its taskbar) down: just log it.
        try { if (target.Handle != IntPtr.Zero) BringToFront(target.Handle); }
        catch (Exception ex) { AppTrace.Log($"Alt+Tab: could not switch: {ex.GetType().Name}: {ex.Message}"); }
        AppTrace.Log("Alt+Tab: switched");
    }

    private void Close()
    {
        _showDelay.Stop();
        AppTrace.Log("Alt+Tab: closing the panel");
        _window.HideList();
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId(); // kernel32, not user32
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);

    /// <summary>
    /// Switches to the window. Our app isn't the foreground app, so Windows would normally refuse; joining the
    /// foreground app's input for a moment is the usual way around that.
    /// </summary>
    private static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, SW_RESTORE);
        var foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint ourThread = GetCurrentThreadId();
        // Joining a frozen program's input would freeze us too: only when the window in front is responding.
        bool attached = foregroundThread != 0 && foregroundThread != ourThread && !IsHungAppWindow(foreground)
                        && AttachThreadInput(ourThread, foregroundThread, true);
        AppTrace.Log($"Alt+Tab: bringing to front (joined input: {attached})");
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        if (attached) AttachThreadInput(ourThread, foregroundThread, false);
    }

    public void Dispose()
    {
        _hook.Dispose();
        _window.Close();
    }

    // ---------- The window ----------

    private sealed class SwitcherWindow : Window
    {
        // From the look (Alt+Tab settings).
        private double TileWidth = 236, PreviewHeight = 140, SelectionThickness = 2, TitleSize = 12;
        private bool ShowTitles = true, ShowIcons = true;
        private FontFamily TitleFont = new("Segoe UI Variable Text, Segoe UI");
        private AltTabLook _currentLook = new();
        private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
        private readonly List<(IntPtr Thumbnail, Border Box, SIZE Source)> _thumbnails = new();
        private readonly List<Border> _tileBorders = new();
        private IntPtr _hwnd;

        // From the look (ApplyLook).
        private Brush _selectedBorder = Frozen(new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)));
        private Brush _tileHover = Frozen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
        private Brush _foreground = Brushes.White;

        // The material layer (behind the tiles; the live previews are drawn by Windows on top) and the frame.
        private readonly Border _materialLayer = new();
        private readonly Border _frame = new() { BorderThickness = new Thickness(1), Padding = new Thickness(12) };
        private string? _materialKey;

        public event Action<int>? Picked;

        /// <summary>How many tiles fit in a row (for Up/Down).</summary>
        public int Columns { get; private set; } = 1;

        private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

        public SwitcherWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            _frame.Child = _tiles;
            var layers = new Grid();
            layers.Children.Add(_materialLayer);
            layers.Children.Add(_frame);
            Content = layers;
            ApplyLook(new AltTabLook());
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)); // never takes the focus
            };
        }

        /// <summary>Colors, corners and the material from the look (the material only when it changed).</summary>
        private void ApplyLook(AltTabLook look)
        {
            var foreground = TaskbarView.ParseColor(look.Foreground, Colors.White);
            _foreground = Frozen(new SolidColorBrush(foreground));
            _tileHover = Frozen(new SolidColorBrush(Color.FromArgb(0x18, foreground.R, foreground.G, foreground.B)));
            var accent = TaskbarView.ParseColor(look.Selection, SystemParameters.WindowGlassColor);
            _selectedBorder = Frozen(new SolidColorBrush(accent));
            _currentLook = look;
            TileWidth = Math.Clamp(look.TileWidth, 120, 480);
            PreviewHeight = Math.Clamp(look.PreviewHeight, 60, 360);
            SelectionThickness = Math.Clamp(look.SelectionThickness, 0, 8);
            ShowTitles = look.ShowTitles;
            ShowIcons = look.ShowIcons;
            TitleSize = Math.Clamp(look.FontSize, 8, 24);
            try { TitleFont = new FontFamily(string.IsNullOrWhiteSpace(look.FontFamily) ? "Segoe UI Variable Text, Segoe UI" : look.FontFamily); }
            catch (ArgumentException) { TitleFont = new FontFamily("Segoe UI"); }
            var corner = new CornerRadius(Math.Clamp(look.CornerRadius, 0, 40));
            _materialLayer.CornerRadius = corner;
            _frame.CornerRadius = corner;
            _materialLayer.Background = Frozen(new SolidColorBrush(TaskbarView.ParseColor(look.Background, Color.FromArgb(0xEE, 0x20, 0x20, 0x20))));
            _frame.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, foreground.R, foreground.G, foreground.B)));

            string key = look.Material + "|" + string.Join(";", (look.MaterialValues ?? new()).Select(kv => kv.Key + "=" + kv.Value)) + "|" + accent;
            if (key == _materialKey) return;
            _materialKey = key;
            if (MaterialHost.Attach(_materialLayer, this, look.Material, look.MaterialValues, accent) is { } error)
                AppTrace.Log($"Alt+Tab material: {error}");
        }

        public void ShowList(List<(IntPtr Handle, string Title)> windows, int selected, AltTabLook look)
        {
            ApplyLook(look);
            Unregister();
            _tiles.Children.Clear();
            _tileBorders.Clear();
            if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();

            // On the monitor the mouse is on; at most 90 % of its width.
            var monitor = MonitorInfoOf(PanelMonitor(look));
            double scale = Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleX);
            double maxWidth = (monitor.rcWork.Right - monitor.rcWork.Left) / scale * 0.9;
            Columns = Math.Max(1, Math.Min(windows.Count, (int)((maxWidth - 26) / (TileWidth + 8))));
            if (look.MaxColumns > 0) Columns = Math.Min(Columns, look.MaxColumns);
            _tiles.MaxWidth = Columns * (TileWidth + 8);

            for (int i = 0; i < windows.Count; i++) _tiles.Children.Add(Tile(windows[i], i));
            Select(selected);

            if (!IsVisible) Show();
            UpdateLayout();
            // Centered on that monitor (physical pixels).
            int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
            int x = (monitor.rcWork.Left + monitor.rcWork.Right - width) / 2;
            int y = look.Position == AltTabPosition.Top
                ? monitor.rcWork.Top + (int)(60 * scale)
                : (monitor.rcWork.Top + monitor.rcWork.Bottom - height) / 2;
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
            UpdateLayout();
            UpdateThumbnails();
        }

        public void HideList()
        {
            Unregister();
            Hide();
        }

        public void Select(int index)
        {
            for (int i = 0; i < _tileBorders.Count; i++)
                _tileBorders[i].BorderBrush = i == index ? _selectedBorder : Brushes.Transparent;
        }

        private FrameworkElement Tile((IntPtr Handle, string Title) window, int index)
        {
            var preview = new Border { Width = TileWidth - 16, Height = PreviewHeight, Margin = new Thickness(0, ShowTitles || ShowIcons ? 8 : 0, 0, 0) };
            bool minimized = IsIconic(window.Handle);
            AppTrace.Log($"Alt+Tab: preview of \"{window.Title}\"");
            if (!minimized && DwmRegisterThumbnail(_hwnd, window.Handle, out var thumbnail) == 0)
            {
                DwmQueryThumbnailSourceSize(thumbnail, out var source);
                _thumbnails.Add((thumbnail, preview, source));
            }
            else
            {
                preview.Child = new TextBlock
                {
                    Text = minimized ? "Minimized" : "No preview", Foreground = _foreground, Opacity = 0.6,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }

            var title = new DockPanel();
            if (ShowIcons)
            {
                var icon = new Image { Source = WindowIcons.Get(window.Handle), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(icon, Dock.Left);
                title.Children.Add(icon);
            }
            if (ShowTitles)
                title.Children.Add(new TextBlock
                {
                    Text = window.Title, Foreground = _foreground, FontSize = TitleSize, FontFamily = TitleFont,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                });

            var stack = new StackPanel();
            if (ShowTitles || ShowIcons) stack.Children.Add(title);
            stack.Children.Add(preview);
            var tile = new Border
            {
                Width = TileWidth, Margin = new Thickness(4), Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(SelectionThickness), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Child = stack,
                ToolTip = ShowTitles ? null : window.Title, // the title on hover when titles are off
            };
            tile.MouseEnter += (_, _) => tile.Background = _tileHover;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
            tile.MouseLeftButtonUp += (_, _) => Picked?.Invoke(index);
            _tileBorders.Add(tile);
            return tile;
        }

        /// <summary>Tells Windows where to draw each live preview (physical pixels, keeping the window's shape).</summary>
        private void UpdateThumbnails()
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            foreach (var (thumbnail, box, source) in _thumbnails)
            {
                var origin = box.TranslatePoint(new Point(0, 0), this);
                double boxW = box.ActualWidth * scale, boxH = box.ActualHeight * scale;
                double fit = Math.Min(boxW / Math.Max(1, source.cx), boxH / Math.Max(1, source.cy));
                double w = source.cx * fit, h = source.cy * fit;
                double left = origin.X * scale + (boxW - w) / 2, top = origin.Y * scale + (boxH - h) / 2;
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECT { Left = (int)left, Top = (int)top, Right = (int)(left + w), Bottom = (int)(top + h) },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false,
                };
                DwmUpdateThumbnailProperties(thumbnail, ref props);
            }
        }

        private void Unregister()
        {
            foreach (var (thumbnail, _, _) in _thumbnails) DwmUnregisterThumbnail(thumbnail);
            _thumbnails.Clear();
        }

        private static MONITORINFO MonitorInfoOf(IntPtr monitor)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);
            return info;
        }
    }
}
