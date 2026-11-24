using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Live previews of windows, shown above (or below) a taskbar button while the mouse rests on it.
/// Windows itself draws the previews (DWM thumbnails) into this window, so they stay live and cost
/// almost nothing. That only works in a normal window, which is why this isn't see-through like the bar.
/// </summary>
internal sealed class ThumbnailPopup : Window
{
    private const double ThumbWidth = 200;   // WPF units
    private const double MaxThumbHeight = 140;
    private const int MaxWindows = 5;

    private readonly StackPanel _cells = new() { Orientation = Orientation.Horizontal };
    private readonly List<(IntPtr thumbnail, FrameworkElement placeholder, SIZE source)> _thumbnails = new();
    private IntPtr _hwnd;

    /// <summary>A preview was clicked: the window to switch to.</summary>
    public event Action<IntPtr>? WindowClicked;

    public ThumbnailPopup()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        Content = new Border { Padding = new Thickness(6), Child = _cells };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

            // Windows 11: rounded corners like other flyouts (ignored on Windows 10).
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        Closed += (_, _) => Unregister();
    }

    /// <summary>Shows previews of <paramref name="windows"/> next to <paramref name="anchor"/>, on the screen side of the bar.</summary>
    public void ShowFor(IReadOnlyList<TaskWindow> windows, FrameworkElement anchor, TaskbarEdge edge)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Unregister();
        _cells.Children.Clear();

        foreach (var window in windows.Take(MaxWindows))
            _cells.Children.Add(BuildCell(window));

        if (!IsVisible) Show();
        UpdateLayout();
        PlaceNear(anchor, edge);
        UpdateLayout();
        UpdateThumbnails();
    }

    /// <summary>Title and icon on top, the live preview (or a "minimized" note) below. Click to switch.</summary>
    private FrameworkElement BuildCell(TaskWindow window)
    {
        // Ask Windows for the window's size first, to give the preview the right shape.
        IntPtr thumbnail = IntPtr.Zero;
        SIZE source = default;
        bool minimized = IsIconic(window.Handle);
        if (!minimized && DwmRegisterThumbnail(_hwnd, window.Handle, out thumbnail) == 0)
            DwmQueryThumbnailSourceSize(thumbnail, out source);

        double height = source.cx > 0
            ? Math.Clamp(ThumbWidth * source.cy / source.cx, 50, MaxThumbHeight)
            : 60;

        var placeholder = new Border { Width = ThumbWidth, Height = height, Margin = new Thickness(0, 6, 0, 0) };
        if (thumbnail == IntPtr.Zero)
        {
            placeholder.Child = new TextBlock
            {
                Text = minimized ? "Minimized" : "No preview",
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            _thumbnails.Add((thumbnail, placeholder, source));
        }

        var title = new DockPanel();
        var icon = new Image { Source = window.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        title.Children.Add(icon);
        title.Children.Add(new TextBlock { Text = window.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ThumbWidth - 22 });

        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(placeholder);

        var cell = new Border
        {
            Padding = new Thickness(6),
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = window.IsActive ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
            Child = content,
            Cursor = Cursors.Hand,
        };
        var normal = cell.Background;
        cell.MouseEnter += (_, _) => cell.Background = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
        cell.MouseLeave += (_, _) => cell.Background = normal;
        var handle = window.Handle;
        cell.MouseLeftButtonUp += (_, _) => WindowClicked?.Invoke(handle);
        return cell;
    }

    /// <summary>Centers the popup on the button, just off the bar, kept on the button's monitor.</summary>
    private void PlaceNear(FrameworkElement anchor, TaskbarEdge edge)
    {
        double scale = VisualTreeHelper.GetDpi(anchor).DpiScaleX;
        var topLeft = anchor.PointToScreen(new Point(0, 0)); // physical pixels
        int anchorWidth = (int)(anchor.ActualWidth * scale), anchorHeight = (int)(anchor.ActualHeight * scale);
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        int gap = (int)(10 * scale);

        int x = (int)topLeft.X + anchorWidth / 2 - width / 2;
        int y = edge == TaskbarEdge.Top ? (int)topLeft.Y + anchorHeight + gap : (int)topLeft.Y - height - gap;

        // Stay on the monitor the button is on.
        var center = new POINT { X = (int)topLeft.X + anchorWidth / 2, Y = (int)topLeft.Y };
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST), ref info))
            x = Math.Clamp(x, info.rcWork.Left + gap, Math.Max(info.rcWork.Left + gap, info.rcWork.Right - width - gap));

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
    }

    /// <summary>Tells Windows where in this popup to draw each preview (physical pixels, keeping the window's shape).</summary>
    private void UpdateThumbnails()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (thumbnail, placeholder, source) in _thumbnails)
        {
            var origin = placeholder.TranslatePoint(new Point(0, 0), this);
            double boxW = placeholder.ActualWidth * scale, boxH = placeholder.ActualHeight * scale;

            // Fit the window's shape into the box, centered.
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
        foreach (var (thumbnail, _, _) in _thumbnails)
            DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
    }
}
