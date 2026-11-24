using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// One custom taskbar on one monitor: a window docked on the top or bottom edge (an app bar),
/// showing a <see cref="TaskbarView"/> for the design. This class handles everything that is about
/// the window itself: reserving screen space, position, blur and fullscreen apps.
/// The Windows taskbar keeps running; this bar only reserves its own strip of screen.
/// </summary>
public partial class AppBarWindow : Window
{
    private readonly TaskbarEdge _edge;
    private readonly string _monitorDevice;
    private readonly TaskbarDesign _design;
    private readonly TaskbarView _view;
    private AppBar? _appBar;

    /// <summary>Entries for the Menu module; set by the main window.</summary>
    public Func<IEnumerable<AppMenuItem>>? GetMenuItems
    {
        get => _view.GetMenuItems;
        set => _view.GetMenuItems = value;
    }

    /// <param name="monitorDevice">The monitor this bar docks on (see <see cref="Monitors"/>).</param>
    /// <param name="onlyOwnMonitorWindows">Show only buttons for windows on this monitor, instead of all windows.</param>
    /// <param name="design">Which modules to show where, and how the bar looks.</param>
    public AppBarWindow(string monitorDevice, TaskbarEdge edge, bool onlyOwnMonitorWindows, TaskbarDesign design)
    {
        _monitorDevice = monitorDevice;
        _edge = edge;
        _design = design;
        InitializeComponent();

        _view = new TaskbarView(design)
        {
            Edge = edge,
            WindowFilter = onlyOwnMonitorWindows ? w => Monitors.DeviceOf(w) == _monitorDevice : null,
        };
        Content = _view;

        SourceInitialized += (_, _) => SetUp();
        Closed += (_, _) => _appBar?.Dispose(); // gives the reserved screen space back
    }

    /// <summary>Runs once the window has a handle, before it is first shown.</summary>
    private void SetUp()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: not listed in Alt+Tab or in our own window list.
        // No-activate: clicking the bar doesn't take focus away from the app you were using,
        // so "click the active window's button to minimize it" works.
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        // Blur covers the whole window; a floating dock's window spans the screen width, so no blur there (yet).
        var look = _design.Look ?? new TaskbarLook();
        if (look.Blur && !_view.IsFloating)
            _view.SetBlurActive(EnableBlur(hwnd, look));

        // Reserve the bar's height (plus the gap around a floating dock), or nothing for a dock over windows.
        double reserve = _view.IsFloating && !look.Floating!.ReserveSpace ? 0 : _view.BarHeight + 2 * _view.DockMargin;
        _appBar = new AppBar(this, _monitorDevice, _edge, reserve, PlaceWindow);
        _appBar.FullscreenChanged += fullscreen =>
        {
            // Get out of the way of games and fullscreen videos on this monitor.
            if (fullscreen) Hide();
            else Show();
        };
    }

    /// <summary>
    /// Called by <see cref="AppBar"/> with the reserved strip (physical pixels).
    /// Full-width bar: fills the strip. Floating dock: the window spans the strip minus the margins,
    /// and the dock itself is drawn centered inside it, as wide as its contents. The rest of the window
    /// is fully transparent, which Windows treats as click-through, so it never blocks the windows behind.
    /// </summary>
    private void PlaceWindow(RECT strip, double scale)
    {
        int height = (int)Math.Round(_view.BarHeight * scale);
        int margin = (int)Math.Round(_view.DockMargin * scale);
        int left = strip.Left + margin;
        int right = strip.Right - margin;

        // Top edge: measure from the top of the strip; bottom edge: from its bottom.
        int top = _edge == TaskbarEdge.Top ? strip.Top + margin : strip.Bottom - margin - height;
        AppBar.MoveWindow(this, new RECT { Left = left, Top = top, Right = right, Bottom = top + height });
    }

    /// <summary>
    /// Blurs what is behind the bar, tinted with the Background color. Returns false if Windows refused.
    /// Undocumented Windows feature: if it fails, the bar simply shows its normal background.
    /// </summary>
    private static bool EnableBlur(IntPtr hwnd, TaskbarLook look)
    {
        var tint = TaskbarView.ParseColor(look.Background, Color.FromArgb(0x80, 0x1C, 0x1C, 0x1C));
        var policy = new ACCENT_POLICY
        {
            AccentState = AccentState.AcrylicBlurBehind,
            AccentFlags = 2,
            // 0xAABBGGRR; acrylic needs some transparency to show the blur.
            GradientColor = ((uint)Math.Min(tint.A, (byte)0xCC) << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R,
        };

        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        bool ok;
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = pointer, SizeOfData = size };
            ok = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        // Windows 11: round the blurred area's corners too (ignored on Windows 10).
        if (ok && look.CornerRadius > 0)
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        return ok;
    }
}
