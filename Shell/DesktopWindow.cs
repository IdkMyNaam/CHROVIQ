using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// The desktop when Windows Customizer is the shell (Explorer isn't running): one per monitor,
/// always at the very back. Transparent, so the wallpaper Windows draws itself shows through.
/// The main monitor's desktop shows the icons of the user's and the shared Desktop folder.
/// </summary>
public sealed class DesktopWindow : Window
{
    private const double ItemWidth = 84, ItemHeight = 96;

    private readonly MonitorInfo _monitor;
    private readonly bool _showIcons;
    private readonly WrapPanel _icons = new() { Orientation = Orientation.Vertical, Margin = new Thickness(6) };
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private Border? _selected;

    /// <summary>Opens the Windows Customizer window (from the desktop's right-click menu).</summary>
    public Action? OpenCustomizer { get; set; }

    private static readonly string UserDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string SharedDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    internal DesktopWindow(MonitorInfo monitor, bool showIcons)
    {
        _monitor = monitor;
        _showIcons = showIcons;
        Title = "Desktop";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Alpha 1, not 0: fully transparent pixels would let clicks fall through to nothing.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        Content = _icons;
        MouseLeftButtonDown += (_, _) => Select(null);
        MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource == this || e.OriginalSource == _icons) { Select(null); DesktopMenu().IsOpen = true; }
        };
        KeyDown += OnKey;

        SourceInitialized += (_, _) => SetUp();
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
        Closed += (_, _) => { foreach (var w in _watchers) w.Dispose(); };
    }

    /// <summary>The window handle, for registering as the shell window.</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void SetUp()
    {
        var hwnd = Handle;
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW)); // not in Alt+Tab

        var b = _monitor.Bounds;
        SetWindowPos(hwnd, HWND_BOTTOM, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOACTIVATE);
        HwndSource.FromHwnd(hwnd).AddHook(KeepAtBottom);
        FitIconsToWorkArea();

        if (!_showIcons) return;
        // After the window is up and registered as the shell window, not before (that has to happen first).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
        foreach (var folder in new[] { UserDesktop, SharedDesktop }.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder) { EnableRaisingEvents = true, IncludeSubdirectories = false };
            void Changed(object? s, EventArgs e) => Dispatcher.InvokeAsync(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += Changed;
            _watchers.Add(watcher);
        }
    }

    // ---------- Staying at the back ----------

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SPI_SETWORKAREA = 0x002F;

    /// <summary>Icons start inside the usable area, so a bar at the top or bottom doesn't cover them.</summary>
    private void FitIconsToWorkArea()
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var center = new POINT { X = (_monitor.Bounds.Left + _monitor.Bounds.Right) / 2, Y = (_monitor.Bounds.Top + _monitor.Bounds.Bottom) / 2 };
        if (!GetMonitorInfo(MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST), ref info)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var m = info.rcMonitor;
        var w = info.rcWork;
        _icons.Margin = new Thickness(6 + (w.Left - m.Left) / scale, 6 + (w.Top - m.Top) / scale,
                                      6 + (m.Right - w.Right) / scale, 6 + (m.Bottom - w.Bottom) / scale);
    }

    /// <summary>Whatever moves this window in the window order (clicking it, Windows), it stays at the back.</summary>
    private IntPtr KeepAtBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The work area changed (a bar was added, moved or removed): move the icons with it.
        if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETWORKAREA)
            Dispatcher.InvokeAsync(FitIconsToWorkArea);

        if (msg == WM_WINDOWPOSCHANGING)
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

    // ---------- Icons ----------

    /// <summary>Reads both Desktop folders again (user's first; the shared one adds what isn't there yet).</summary>
    public void Refresh()
    {
        if (!_showIcons) return;
        string? selectedPath = (_selected?.Tag as string);
        _icons.Children.Clear();
        _selected = null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { UserDesktop, SharedDesktop }.Where(Directory.Exists))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder).ToList(); }
            catch (Exception) { continue; }

            foreach (var path in entries.OrderBy(p => !Directory.Exists(p)).ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue; // desktop.ini etc.
                var name = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
                if (!names.Add(name)) continue;
                _icons.Children.Add(IconItem(path, name));
            }
        }

        ShellMode.Log($"Desktop icons listed: {_icons.Children.Count}");
        if (selectedPath != null)
            Select(_icons.Children.OfType<Border>().FirstOrDefault(b => string.Equals(b.Tag as string, selectedPath, StringComparison.OrdinalIgnoreCase)));
    }

    private Border IconItem(string path, string name)
    {
        var image = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 4, 0, 4) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        // Icons one by one in the background: the names show at once and the desktop stays responsive.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => image.Source = ShellIcons.Get(path, 48));
        var label = new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            FontSize = 12,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.9 }, // readable on any wallpaper
        };
        var stack = new StackPanel();
        stack.Children.Add(image);
        stack.Children.Add(label);

        var item = new Border
        {
            Width = ItemWidth, Height = ItemHeight, Margin = new Thickness(2), Padding = new Thickness(2),
            CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = stack, Tag = path, ToolTip = name,
        };
        item.MouseEnter += (_, _) => { if (item != _selected) item.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); };
        item.MouseLeave += (_, _) => { if (item != _selected) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonDown += (_, e) =>
        {
            Select(item);
            Activate(); // keyboard keys (Enter, Delete, F2) need the desktop focused
            if (e.ClickCount == 2) Open(path);
            e.Handled = true;
        };
        item.MouseRightButtonUp += (_, e) =>
        {
            Select(item);
            ItemMenu(path).IsOpen = true;
            e.Handled = true;
        };
        return item;
    }

    private void Select(Border? item)
    {
        if (_selected != null) _selected.Background = Brushes.Transparent;
        _selected = item;
        if (item != null) item.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x9F, 0xFF));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { Refresh(); return; }
        if (_selected?.Tag is not string path) return;
        switch (e.Key)
        {
            case Key.Enter: Open(path); break;
            case Key.Delete: Delete(path); break;
            case Key.F2: Rename(path); break;
        }
    }

    // ---------- Actions ----------

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { MessageBox.Show($"Could not open it:\n{ex.Message}", "Desktop", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>To the Recycle Bin, with Windows' own confirmation (so it can be undone).</summary>
    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            else FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { /* the user said no */ }
        catch (Exception ex) { MessageBox.Show($"Could not delete it:\n{ex.Message}", "Desktop", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Rename(string path)
    {
        bool shortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        string extension = shortcut ? Path.GetExtension(path) : "";
        string current = shortcut ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        if (InputDialog.Ask(this, "Rename", "New name:", current) is not { } newName || newName == current) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "A name can't contain any of these: \\ / : * ? \" < > |", "Rename");
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(path)!, newName + extension);
        try
        {
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not rename it:\n{ex.Message}", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void NewFolder()
    {
        string path = Path.Combine(UserDesktop, "New folder");
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(UserDesktop, $"New folder ({n})");
        try
        {
            Directory.CreateDirectory(path);
            Refresh();
            Rename(path);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not create the folder:\n{ex.Message}", "Desktop"); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    /// <summary>Windows' own Properties window for a file or folder.</summary>
    private void Properties(string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd = Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
        };
        ShellExecuteEx(ref info);
    }

    // ---------- Menus ----------

    private ContextMenu ItemMenu(string path)
    {
        var menu = new ContextMenu();
        Add(menu, "Open", () => Open(path));
        Add(menu, "Open file location", () => Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose());
        menu.Items.Add(new Separator());
        Add(menu, "Rename", () => Rename(path));
        Add(menu, "Delete", () => Delete(path));
        menu.Items.Add(new Separator());
        Add(menu, "Properties", () => Properties(path));
        return menu;
    }

    private ContextMenu DesktopMenu()
    {
        var menu = new ContextMenu();
        Add(menu, "Refresh", Refresh);
        Add(menu, "New folder", NewFolder);
        Add(menu, "Open desktop folder", () => Process.Start("explorer.exe", $"\"{UserDesktop}\"")?.Dispose());
        menu.Items.Add(new Separator());
        Add(menu, "Display settings", () => Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true })?.Dispose());
        Add(menu, "Personalize", () => Process.Start(new ProcessStartInfo("ms-settings:personalization") { UseShellExecute = true })?.Dispose());
        if (OpenCustomizer != null) Add(menu, "Windows Customizer", OpenCustomizer);
        return menu;
    }

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
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
