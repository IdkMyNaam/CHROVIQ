using System.IO;
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
/// The desktop when Windows Customizer is the shell (Explorer isn't running): one per monitor, always at the
/// very back, drawing the wallpaper itself. The main monitor's desktop shows the icons of the user's and the
/// shared Desktop folder (see DesktopWindow.Icons.cs; actions and menus in DesktopWindow.Actions.cs).
///
/// "Preview" mode shows the same desktop in a normal window, to try it out without being the shell.
/// </summary>
public sealed partial class DesktopWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly bool _showIcons;
    private readonly bool _preview;
    private readonly System.Windows.Shapes.Rectangle _wallpaper = new();

    /// <summary>Opens the Windows Customizer window (from the desktop's right-click menu).</summary>
    public Action? OpenCustomizer { get; set; }

    internal DesktopWindow(MonitorInfo monitor, bool showIcons, bool preview = false)
    {
        _monitor = monitor;
        _showIcons = showIcons;
        _preview = preview;
        UseLayoutRounding = true;
        // Opaque, with the wallpaper drawn here: without Explorer nothing else draws it (the screen stays black).
        Background = Brushes.Black;

        if (preview)
        {
            Title = "Desktop preview (your real Desktop folder)";
            Width = 1100;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            Title = "Desktop";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        var layers = new Grid();
        layers.Children.Add(_wallpaper);
        layers.Children.Add(_iconArea);
        Content = layers;

        SetUpIcons();
        PreviewKeyDown += OnKey; // Preview: before WPF uses the arrow keys to move the focus
        SourceInitialized += (_, _) => SetUp();
        DesktopLayout.Changed += OnLayoutChanged;
        Closed += (_, _) =>
        {
            DesktopLayout.Changed -= OnLayoutChanged;
            foreach (var w in _watchers) w.Dispose();
        };
    }

    /// <summary>The window handle.</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void SetUp()
    {
        var hwnd = Handle;
        if (!_preview)
        {
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW)); // not in Alt+Tab
            var b = _monitor.Bounds;
            SetWindowPos(hwnd, HWND_BOTTOM, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOACTIVATE);
        }
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        FitIconsToWorkArea();
        ShowWallpaper();

        if (!_showIcons) return;
        ShellNew.Preload(); // the "New" menu's file types, read in the background
        // After the window is up (and, as the shell, after the shell window is registered).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
        WatchDesktopFolders();
    }

    // ---------- Staying at the back ----------

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER = 0x0004;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SPI_SETWORKAREA = 0x002F;
    private const int SPI_SETDESKWALLPAPER = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // While Windows' classic menu is open, its submenus (Send to, Open with, New) are filled through us.
        if (ShellContextMenu.HandleMenuMessage(msg, wParam, lParam, out var menuResult)) { handled = true; return menuResult; }

        // The work area changed (a bar was added, moved or removed): move the icons with it.
        if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETWORKAREA)
            Dispatcher.InvokeAsync(FitIconsToWorkArea);
        // The wallpaper was changed (Settings > Personalization): draw the new one.
        if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETDESKWALLPAPER)
            Dispatcher.InvokeAsync(ShowWallpaper);

        // Whatever moves this window in the window order (clicking it, Windows), it stays at the back.
        if (!_preview && msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Icons start inside the usable area, so a bar at the top or bottom doesn't cover them.</summary>
    private void FitIconsToWorkArea()
    {
        if (_preview) { _iconArea.Margin = new Thickness(6); return; }
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var center = new POINT { X = (_monitor.Bounds.Left + _monitor.Bounds.Right) / 2, Y = (_monitor.Bounds.Top + _monitor.Bounds.Bottom) / 2 };
        if (!GetMonitorInfo(MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST), ref info)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var m = info.rcMonitor;
        var w = info.rcWork;
        _iconArea.Margin = new Thickness(6 + (w.Left - m.Left) / scale, 6 + (w.Top - m.Top) / scale,
                                         6 + (m.Right - w.Right) / scale, 6 + (m.Bottom - w.Bottom) / scale);
    }

    // ---------- Wallpaper ----------

    /// <summary>Draws the user's wallpaper (loaded in the background) with its placement and background color.</summary>
    private void ShowWallpaper()
    {
        var (file, placement, color) = Wallpaper.Current();
        Background = new SolidColorBrush(color);
        if (file == null) { _wallpaper.Fill = null; ShellMode.Log("Desktop: no wallpaper picture, only the background color"); return; }

        Task.Run(() => Wallpaper.Load(file)).ContinueWith(t =>
        {
            if (t.Result is not { } image) { _wallpaper.Fill = null; return; }
            var brush = new ImageBrush(image) { AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            switch (placement)
            {
                case Wallpaper.Placement.Fit: brush.Stretch = Stretch.Uniform; break;
                case Wallpaper.Placement.Stretch: brush.Stretch = Stretch.Fill; break;
                case Wallpaper.Placement.Center: brush.Stretch = Stretch.None; break;
                case Wallpaper.Placement.Tile:
                    brush.Stretch = Stretch.None;
                    brush.TileMode = TileMode.Tile;
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new Rect(0, 0, image.Width, image.Height);
                    break;
                default: brush.Stretch = Stretch.UniformToFill; break; // Fill; Span is shown as Fill on each monitor
            }
            brush.Freeze();
            _wallpaper.Fill = brush;
            ShellMode.Log($"Desktop: wallpaper drawn ({placement}): {file}");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------- Keyboard ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { Refresh(); e.Handled = true; return; }
        if (HandleSelectionKey(e)) { e.Handled = true; return; }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt+... and F10 arrive as "system" keys
        var selected = SelectedItems();

        if (ctrl && key == Key.V) { Paste(); e.Handled = true; return; }
        // The menu key, or Shift+F10: Windows' classic menu (like Explorer's Shift+F10).
        if (key == Key.Apps || (key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            if (selected.Count > 0) ShowClassicItemMenu(selected, extended: false);
            else ShowClassicDesktopMenu(Mouse.GetPosition(_iconArea), extended: false);
            e.Handled = true;
            return;
        }
        if (selected.Count == 0) return;
        if (ctrl && key == Key.C) { CopyItems(selected, cut: false); e.Handled = true; return; }
        if (ctrl && key == Key.X) { CopyItems(selected, cut: true); e.Handled = true; return; }
        if (key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && selected.Count == 1)
        {
            Properties(selected[0].Path);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter: OpenItems(selected); e.Handled = true; break;
            case Key.Delete: DeleteItems(selected, permanently: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; break;
            case Key.F2 when selected.Count == 1: Rename(selected[0].Path); e.Handled = true; break;
        }
    }
}

/// <summary>A small "type a name" dialog (for renaming).</summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(new TextBlock { Text = prompt });
        stack.Children.Add(_box);
        stack.Children.Add(buttons);
        Content = stack;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string value)
    {
        var dialog = new InputDialog(title, prompt, value) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._box.Text.Trim() : null;
    }
}
