using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// One desktop widget in its own small window, kept just above the desktop: above Explorer's desktop (and a
/// Wallpaper Engine wallpaper), or above Windows Customizer's own desktop in shell mode, but under every other
/// window. Drag it to move it, the corner to resize it (unless locked); right-click for its settings.
/// </summary>
internal sealed class WidgetWindow : Window, ILiveGlassWindow
{
    public DesktopWidget Widget { get; }

    public bool WantsLiveGlass => Widget.LiveGlass;
    private readonly WidgetContent _content;
    private readonly Action _changed;
    private readonly Action<WidgetWindow> _openSettings, _remove;
    private IntPtr _hwnd;

    /// <param name="changed">The widget's place, size or text changed (save it).</param>
    public WidgetWindow(DesktopWidget widget, Action changed, Action<WidgetWindow> openSettings, Action<WidgetWindow> remove)
    {
        Widget = widget;
        _changed = changed;
        _openSettings = openSettings;
        _remove = remove;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Title = "CHROVIQ widget";
        Left = widget.Left;
        Top = widget.Top;
        Width = Math.Clamp(widget.Width, 80, 2000);
        Height = Math.Clamp(widget.Height, 50, 2000);

        var foreground = TaskbarView.ParseColor(widget.Foreground, Colors.White);
        var windowsAccent = SystemParameters.WindowGlassBrush is SolidColorBrush glass
            ? Color.FromRgb(glass.Color.R, glass.Color.G, glass.Color.B) : Color.FromRgb(0x00, 0x78, 0xD4);
        var accent = TaskbarView.ParseColor(widget.Accent, windowsAccent);
        Foreground = new SolidColorBrush(foreground);
        try { FontFamily = new FontFamily(string.IsNullOrWhiteSpace(widget.FontFamily) ? "Segoe UI" : widget.FontFamily); }
        catch (ArgumentException) { /* keep Segoe UI */ }
        FontSize = Math.Clamp(widget.FontSize, 8, 72);

        _content = WidgetContent.Create(widget, foreground, accent, changed);

        // The background (and its material) on its own layer behind the content, in the same shape.
        var corner = new CornerRadius(Math.Clamp(widget.CornerRadius, 0, 100));
        var materialLayer = new Border { CornerRadius = corner, Background = new SolidColorBrush(TaskbarView.ParseColor(widget.Background, Color.FromArgb(0x99, 0x20, 0x20, 0x20))) };
        bool web = _content is WebWidgetContent;
        // A web widget draws its own layout (no padding), cut to the widget's rounded shape.
        // A web widget draws its own layout, inside a thin edge that belongs to the widget window: the web view takes the
        // mouse over the page, so moving (drag the edge), resizing (the corner) and the menu (right-click) work there.
        var frame = new Border { CornerRadius = corner, Padding = new Thickness(web ? WebEdge : 14), Child = _content.View,
            Background = web ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent }; // 1/255: invisible, but catches the mouse
        if (web)
            frame.SizeChanged += (_, e) => frame.Clip = new RectangleGeometry(new Rect(e.NewSize), corner.TopLeft, corner.TopLeft);
        var layers = new Grid();
        layers.Children.Add(materialLayer);
        layers.Children.Add(frame);
        layers.Children.Add(ResizeCorner());
        Content = layers;
        if (MaterialHost.Attach(materialLayer, layers, widget.Material, widget.MaterialValues, accent) is { } error)
            AppTrace.Log($"Widget {widget}: material: {error}");

        layers.ContextMenu = Menu();
        if (_content is WebWidgetContent page)
        {
            // The web view takes the mouse, so the window itself handles the menu, moving and resizing (its "Preview"
            // events come before the web view gets the mouse). The page can also ask for them.
            // At the mouse pointer as Windows has it: over the page WPF doesn't see the mouse, so "at the mouse" would
            // use the last place WPF saw it.
            page.OpenMenu = _openWebMenu = () =>
            {
                var menu = layers.ContextMenu;
                GetCursorPos(out var p);
                var dpi = VisualTreeHelper.GetDpi(this);
                menu.Placement = PlacementMode.AbsolutePoint;
                menu.HorizontalOffset = p.X / dpi.DpiScaleX;
                menu.VerticalOffset = p.Y / dpi.DpiScaleY;
                menu.PlacementTarget = layers;
                menu.IsOpen = true;
            };
            page.StartDrag = DragByPage;
            PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                page.OpenMenu();
            };
            PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (Widget.Locked) return;
                var at = e.GetPosition(this);
                if (InResizeCorner(at)) { e.Handled = true; NativeDrag(17 /* HTBOTTOMRIGHT */); return; }
                // Alt + drag anywhere, or the widget's edge: move it.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || OnEdge(at)) { e.Handled = true; DragByPage(); }
            };
            PreviewMouseMove += (_, e) =>
            {
                if (Widget.Locked) return;
                var at = e.GetPosition(this);
                Mouse.OverrideCursor = InResizeCorner(at) ? Cursors.SizeNWSE : OnEdge(at) ? Cursors.SizeAll : null;
                if (_grip != null) _grip.Opacity = 0.7;
                _hoverCheck.Stop();
                _hoverCheck.Start();
            };
            _hoverCheck.Tick += (_, _) =>
            {
                // The web view keeps the mouse, so "mouse left" is checked by where the pointer is.
                GetCursorPos(out var p);
                var topLeft = PointToScreen(new Point(0, 0));
                var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
                if (p.X >= topLeft.X && p.X < bottomRight.X && p.Y >= topLeft.Y && p.Y < bottomRight.Y) return;
                _hoverCheck.Stop();
                Mouse.OverrideCursor = null;
                if (_grip != null) _grip.Opacity = 0;
            };
            Closed += (_, _) => { _hoverCheck.Stop(); Mouse.OverrideCursor = null; };
        }
        MouseLeftButtonDown += (_, e) =>
        {
            if (Widget.Locked || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { return; }
            Widget.Left = Left;
            Widget.Top = Top;
            _changed();
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW)); // not in Alt+Tab or the taskbar
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            if (_content is WebWidgetContent) WatchMouseBeforePage();
        };
        Loaded += (_, _) => GoBehindEverything();
        LocationChanged += (_, _) =>
        {
            if (!_draggingByPage) return;
            Widget.Left = Left;
            Widget.Top = Top;
        };
        Closed += (_, _) => _content.Dispose();
    }

    private bool _draggingByPage;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---------- Web widgets: the mouse before the web view gets it ----------

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private SubclassProc? _mouseProc;   // kept, so it isn't collected while Windows calls it
    private POINT? _pressedAt;          // the left button went down here (screen pixels), and hasn't moved far yet

    /// <summary>
    /// The web view handles the window's mouse messages itself, before WPF (and so before the widget's own handlers)
    /// sees them. A subclass sees them first: right-click anywhere opens the widget's menu, and pressing inside the
    /// page and dragging a few pixels moves the widget. A click that doesn't move still goes to the page.
    /// </summary>
    private void WatchMouseBeforePage()
    {
        _mouseProc = MouseBeforePage;
        if (!SetWindowSubclass(_hwnd, _mouseProc, (UIntPtr)1, UIntPtr.Zero)) { AppTrace.Log($"Widget {Widget}: no mouse subclass"); return; }
        Closed += (_, _) => RemoveWindowSubclass(_hwnd, _mouseProc, (UIntPtr)1);
    }

    private IntPtr MouseBeforePage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        const int MK_LBUTTON = 0x0001, DragDistance = 6;
        switch (msg)
        {
            case WM_RBUTTONDOWN:
                return IntPtr.Zero; // the page doesn't get right-clicks: they're for the widget's menu
            case WM_RBUTTONUP:
                AppTrace.Log($"Widget {Widget}: right-click (window), menu");
                Dispatcher.BeginInvoke(() => _openWebMenu?.Invoke());
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                _pressedAt = GetCursorPos(out var down) ? down : null;
                break;
            case WM_LBUTTONUP:
                _pressedAt = null;
                break;
            case WM_MOUSEMOVE when _pressedAt is { } start && (wParam.ToInt64() & MK_LBUTTON) != 0 && !Widget.Locked:
                if (GetCursorPos(out var now) && (Math.Abs(now.X - start.X) >= DragDistance || Math.Abs(now.Y - start.Y) >= DragDistance))
                {
                    _pressedAt = null;
                    // After this message: Windows' own move, while the button is still down.
                    Dispatcher.BeginInvoke(() => { if (System.Windows.Forms.Control.MouseButtons.HasFlag(System.Windows.Forms.MouseButtons.Left)) DragByPage(); });
                    return IntPtr.Zero;
                }
                break;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private Action? _openWebMenu;

    private readonly System.Windows.Threading.DispatcherTimer _hoverCheck = new() { Interval = TimeSpan.FromMilliseconds(400) };

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);

    private const double WebEdge = 7, Edge = 8, Corner = 18;

    private bool InResizeCorner(Point at) => at.X >= ActualWidth - Corner && at.Y >= ActualHeight - Corner;

    private bool OnEdge(Point at) => at.X < Edge || at.Y < Edge || at.X >= ActualWidth - Edge || at.Y >= ActualHeight - Edge;

    /// <summary>Moving a web widget: Windows' own move, while the mouse button is still down.</summary>
    private void DragByPage() => NativeDrag(2 /* HTCAPTION */);

    /// <summary>Hands the mouse to Windows' own move (HTCAPTION) or resize (HTBOTTOMRIGHT); returns when it's released.</summary>
    private void NativeDrag(int hit)
    {
        if (Widget.Locked || _hwnd == IntPtr.Zero) return;
        _draggingByPage = true;
        Mouse.Capture(null);
        ReleaseCapture();
        SendMessage(_hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(hit), IntPtr.Zero);
        _draggingByPage = false;
        Mouse.OverrideCursor = null;
        Widget.Left = Left;
        Widget.Top = Top;
        Widget.Width = Width;
        Widget.Height = Height;
        _changed();
        GoBehindEverything();
    }

    /// <summary>Settings, lock, remove.</summary>
    private ContextMenu Menu()
    {
        var menu = ModernMenu.Create();
        ModernMenu.Item(menu, "\uE713", "Widget settings...", () => _openSettings(this));
        MenuItem? locked = null;
        locked = ModernMenu.Check(menu, "Lock position and size", Widget.Locked, () =>
        {
            Widget.Locked = !Widget.Locked;
            locked!.IsChecked = Widget.Locked;
            _changed();
            UpdateResizeCorner();
        });
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, "\uE74D", "Remove widget", () =>
        {
            if (MessageBox.Show($"Remove the {Widget} widget?", "CHROVIQ", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                _remove(this);
        });
        return menu;
    }

    // ---------- Resizing ----------

    private Thumb? _grip;

    /// <summary>A small corner at the bottom right to drag; shows while the mouse is on the widget (not when locked).</summary>
    private Thumb ResizeCorner()
    {
        _grip = new Thumb
        {
            Width = 14, Height = 14, Cursor = Cursors.SizeNWSE, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 3),
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
                  <Grid Background="Transparent">
                    <Path Data="M 12,2 L 2,12 M 12,6 L 6,12 M 12,10 L 10,12" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" />
                  </Grid>
                </ControlTemplate>
                """),
            Foreground = Foreground,
        };
        _grip.DragDelta += (_, e) =>
        {
            Width = Math.Max(80, Width + e.HorizontalChange);
            Height = Math.Max(50, Height + e.VerticalChange);
        };
        _grip.DragCompleted += (_, _) => { Widget.Width = Width; Widget.Height = Height; _changed(); };
        MouseEnter += (_, _) => { if (!Widget.Locked) _grip.Opacity = 0.7; };
        MouseLeave += (_, _) => _grip.Opacity = 0;
        UpdateResizeCorner();
        return _grip;
    }

    private void UpdateResizeCorner()
    {
        if (_grip == null) return;
        _grip.IsEnabled = !Widget.Locked;
        if (Widget.Locked) _grip.Opacity = 0;
    }

    // ---------- Staying on the desktop ----------

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
    private const uint GW_HWNDPREV = 3;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    /// <summary>The window the widgets sit on: Explorer's desktop (the one with the icons), or ours in shell mode.</summary>
    private static IntPtr DesktopHost()
    {
        if (DesktopWindow.BackHandles.Count > 0) return DesktopWindow.BackHandles[0];
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            found = hwnd; // Progman, or a WorkerW (with Wallpaper Engine or after Win+Tab)
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Where to go in the window order: right above the desktop (null: stay where it is).</summary>
    private IntPtr? PlaceAfter()
    {
        var desktop = DesktopHost();
        if (desktop == IntPtr.Zero) return HWND_BOTTOM;
        var above = GetWindow(desktop, GW_HWNDPREV); // the window just above the desktop
        if (above == _hwnd) return null;             // already there
        if (above == IntPtr.Zero) return HWND_BOTTOM;
        return above; // "insert after it" = just under it = just above the desktop
    }

    private void GoBehindEverything()
    {
        if (_hwnd == IntPtr.Zero || PlaceAfter() is not { } after) return;
        SetWindowPos(_hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Whatever moves the widget in the window order (clicking it, typing in a note), it stays on the desktop.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING) return IntPtr.Zero;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SWP_NOZORDER) != 0) return IntPtr.Zero;
        if (PlaceAfter() is { } after) pos.hwndInsertAfter = after;
        else pos.flags |= SWP_NOZORDER;
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }
}
