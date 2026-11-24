using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Something shown as one button in the window list: a single window (<see cref="TaskWindow"/>)
/// or all windows of one app (<see cref="TaskGroup"/>). Both have the same properties, so one
/// button template shows either.
/// </summary>
public abstract class TaskItem : INotifyPropertyChanged
{
    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value, nameof(Title)); }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value, nameof(Icon)); }

    /// <summary>One-color version of <see cref="Icon"/> for designs with tinted icons; set by the taskbar view.</summary>
    private ImageSource? _tintedIcon;
    public ImageSource? TintedIcon { get => _tintedIcon; set => Set(ref _tintedIcon, value, nameof(TintedIcon)); }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value, nameof(IsActive)); }

    /// <summary>Badge text, like "3" for a group of three windows; null hides the badge.</summary>
    private string? _countText;
    public string? CountText { get => _countText; set => Set(ref _countText, value, nameof(CountText)); }

    // Colors for this button, set by the taskbar view: from the design, or from the icon's own color ("glow").
    private Brush? _hoverBrush, _activeBrush, _lineBrush;
    public Brush? HoverBrush { get => _hoverBrush; set => Set(ref _hoverBrush, value, nameof(HoverBrush)); }
    public Brush? ActiveBrush { get => _activeBrush; set => Set(ref _activeBrush, value, nameof(ActiveBrush)); }
    public Brush? LineBrush { get => _lineBrush; set => Set(ref _lineBrush, value, nameof(LineBrush)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One open window.</summary>
public sealed class TaskWindow : TaskItem
{
    public TaskWindow(IntPtr handle)
    {
        Handle = handle;
        ProcessPath = ProcessPathOf(handle);
    }

    public IntPtr Handle { get; }

    /// <summary>The program that owns the window (for grouping and pinned apps); null if Windows won't tell.</summary>
    public string? ProcessPath { get; }

    /// <summary>How often loading the icon failed; some windows set their icon a moment after opening.</summary>
    internal int IconAttempts { get; set; }

    /// <summary>Full path of the program that owns a window.</summary>
    public static string? ProcessPathOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}

/// <summary>
/// Keeps <see cref="Windows"/> in sync with the open windows, the way Alt+Tab lists them.
/// Updates come from the shell hook (window created, destroyed, activated, title changed),
/// plus a slow timer for changes the hook doesn't report (virtual desktop switches, cloaking).
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private const int HSHELL_WINDOWCREATED = 1;
    private const int HSHELL_REDRAW = 6; // title (and often icon) changed

    // Windows that are visible top-level windows but are part of the shell, not apps.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly uint _shellHookMessage;
    private readonly DispatcherTimer _fallbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<IntPtr, bool>? _filter;
    private readonly WinEventProc _moveSizeEndCallback; // kept in a field so the GC doesn't collect it
    private readonly IntPtr _moveSizeEndHook;
    private bool _refreshQueued;

    public ObservableCollection<TaskWindow> Windows { get; } = new();

    /// <param name="host">The window that receives the shell hook messages (the taskbar itself).</param>
    /// <param name="filter">Optional: only list windows for which this returns true (e.g. "is on my monitor").</param>
    public WindowTracker(Window host, Func<IntPtr, bool>? filter = null)
    {
        _filter = filter;
        _hwnd = new WindowInteropHelper(host).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        _shellHookMessage = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(_hwnd);

        // A window dragged to another monitor: the shell hook doesn't report that, so listen for
        // "move/resize ended" to update the per-monitor lists right away instead of at the next timer tick.
        _moveSizeEndCallback = (_, _, _, _, _, _, _) => QueueRefresh();
        _moveSizeEndHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero,
            _moveSizeEndCallback, 0, 0, WINEVENT_OUTOFCONTEXT);

        _fallbackTimer.Tick += (_, _) => Refresh();
        _fallbackTimer.Start();
        Refresh();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _shellHookMessage)
        {
            int code = wParam.ToInt32() & 0x7FFF; // strip HSHELL_HIGHBIT
            if (code is HSHELL_REDRAW or HSHELL_WINDOWCREATED)
            {
                var changed = Windows.FirstOrDefault(w => w.Handle == lParam);
                if (changed != null) changed.Icon = WindowIcons.Get(lParam) ?? changed.Icon;
            }
            QueueRefresh();
        }
        return IntPtr.Zero;
    }

    /// <summary>Hook messages come in bursts; refresh once after the burst.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _source.Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        _refreshQueued = false;
        var current = FindTaskWindows();
        if (_filter != null)
            current = current.Where(c => _filter(c.hwnd)).ToList();
        var foreground = GetForegroundWindow();
        var stillOpen = current.Select(c => c.hwnd).ToHashSet();

        for (int i = Windows.Count - 1; i >= 0; i--)
            if (!stillOpen.Contains(Windows[i].Handle))
                Windows.RemoveAt(i);

        var known = Windows.ToDictionary(w => w.Handle);

        // EnumWindows lists front to back; walk it backwards so on the first load
        // windows further back come first. New windows are appended, existing ones keep their place.
        for (int i = current.Count - 1; i >= 0; i--)
        {
            var (hwnd, title) = current[i];
            if (!known.TryGetValue(hwnd, out var window))
            {
                window = new TaskWindow(hwnd);
                Windows.Add(window);
            }
            window.Title = title;
            window.IsActive = hwnd == foreground;

            if (window.Icon == null && window.IconAttempts < 5)
            {
                window.IconAttempts++;
                window.Icon = WindowIcons.Get(hwnd);
            }
        }
    }

    /// <summary>Roughly the Alt+Tab rules: visible, not owned, not a tool window, not cloaked, has a title.</summary>
    private static List<(IntPtr hwnd, string title)> FindTaskWindows()
    {
        var result = new List<(IntPtr, string)>();
        var className = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            bool appWindow = (exStyle & WS_EX_APPWINDOW) != 0;
            if (!appWindow && (exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;

            // Cloaked: hidden by Windows, e.g. on another virtual desktop or a suspended Store app.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;

            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (ShellClasses.Contains(className.ToString())) return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            result.Add((hwnd, title.ToString()));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Taskbar click: restore if minimized, minimize if already in front, otherwise bring to front.</summary>
    public static void ActivateOrMinimize(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        else if (GetForegroundWindow() == hwnd)
        {
            ShowWindow(hwnd, SW_MINIMIZE);
        }
        else
        {
            // Allowed because our process received the last input (the click).
            SetForegroundWindow(hwnd);
        }
    }

    /// <summary>Asks the window to close, like clicking its X (it can still ask to save first).</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        _fallbackTimer.Stop();
        if (_moveSizeEndHook != IntPtr.Zero) UnhookWinEvent(_moveSizeEndHook);
        DeregisterShellHookWindow(_hwnd);
        _source.RemoveHook(WndProc);
    }
}

/// <summary>Gets a window's icon: from the window, then its window class, then its program file.</summary>
internal static class WindowIcons
{
    public static ImageSource? Get(IntPtr hwnd)
    {
        // Large (32 px) first: scaled down it stays sharp; a 16 px icon scaled up (icon-only buttons,
        // display scaling above 100%) looks blurry. Small icons only when an app has no large one.
        IntPtr icon = AskWindow(hwnd, ICON_BIG);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, GCLP_HICON);
        if (icon == IntPtr.Zero) icon = AskWindow(hwnd, ICON_SMALL2);
        if (icon == IntPtr.Zero) icon = AskWindow(hwnd, ICON_SMALL);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, GCLP_HICONSM);

        if (icon != IntPtr.Zero)
        {
            // These icons belong to the other window; copy them, never destroy them.
            try { return ToImage(icon); }
            catch (Exception) { /* invalid handle: try the exe below */ }
        }

        return FromProgramFile(hwnd);
    }

    /// <summary>WM_GETICON with a short timeout, so a hung app can't freeze the taskbar.</summary>
    private static IntPtr AskWindow(IntPtr hwnd, int type)
    {
        SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(type), IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var result);
        return result;
    }

    private static ImageSource? FromProgramFile(IntPtr hwnd)
    {
        if (TaskWindow.ProcessPathOf(hwnd) is not { } path) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon == null ? null : ToImage(icon.Handle);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource ToImage(IntPtr hIcon)
    {
        var image = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }
}
