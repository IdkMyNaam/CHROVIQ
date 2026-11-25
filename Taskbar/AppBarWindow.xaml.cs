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

        // Reserve the bar's height (plus the gap around a floating dock). Nothing for a dock over windows
        // or an auto-hiding bar: those cover windows when they show.
        bool overWindows = _design.AutoHide || (_view.IsFloating && !look.Floating!.ReserveSpace);
        double reserve = overWindows ? 0 : _view.BarHeight + 2 * _view.DockMargin;
        _appBar = new AppBar(this, _monitorDevice, _edge, reserve, PlaceWindow);
        _appBar.FullscreenChanged += SetFullscreen;
        // While hidden for a fullscreen app, look every 2 seconds whether it's still there: Windows doesn't always
        // say when it ends (and a bar that stays hidden leaves you without a taskbar).
        _fullscreenCheck.Tick += (_, _) => { if (!_appBar.IsFullscreenOnOurMonitor()) SetFullscreen(false); };
        Closed += (_, _) => _fullscreenCheck.Stop();

        if (_design.AutoHide) StartAutoHide();
    }

    private readonly System.Windows.Threading.DispatcherTimer _fullscreenCheck = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Gets out of the way of games and fullscreen videos on this monitor, and comes back after.</summary>
    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == _fullscreen) return;
        _fullscreen = fullscreen;
        AppTrace.Log($"Taskbar on {_monitorDevice}: {(fullscreen ? "hidden for a fullscreen app" : "back")}");
        if (fullscreen) { Hide(); _fullscreenCheck.Start(); return; }
        _fullscreenCheck.Stop();
        if (!_design.AutoHide) Show();
        else _hidden = true; // auto-hide: stay away until the mouse comes to the edge
    }

    // ---------- Auto-hide ----------

    private readonly System.Windows.Threading.DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly System.Windows.Threading.DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private RECT _strip;            // the strip at the edge from the last PlaceWindow (physical pixels)
    private RECT _shownRect;        // where the bar is when fully shown
    private bool _hidden;           // slid away (or sliding away)
    private bool _fullscreen;       // a fullscreen app is on this monitor
    private DateTime? _mouseLeftAt; // when the mouse left the bar (hide after a short delay)
    private double _slide;          // 0 = fully shown, 1 = fully hidden
    private double _slideTarget;

    private const double HideDelayMs = 700;

    /// <summary>
    /// Checks the mouse 10 times a second: at the screen edge the bar slides in; once the mouse has
    /// been off the bar for a moment (and no menu is open) it slides away. Polling instead of mouse
    /// events also works for a floating dock, whose see-through window parts don't receive the mouse.
    /// </summary>
    private void StartAutoHide()
    {
        _autoHideTimer.Tick += (_, _) => CheckMouse();
        _slideTimer.Tick += (_, _) => SlideStep();
        _autoHideTimer.Start();
        Closed += (_, _) => { _autoHideTimer.Stop(); _slideTimer.Stop(); };
    }

    private void CheckMouse()
    {
        if (_fullscreen || !GetCursorPos(out var p)) return;

        bool withinWidth = p.X >= _strip.Left && p.X < _strip.Right;
        bool atEdge = withinWidth && (_edge == TaskbarEdge.Top ? p.Y <= _strip.Top + 1 : p.Y >= _strip.Bottom - 2);

        // Over the shown bar, with a little slack so it doesn't flicker at its border.
        const int slack = 8;
        bool overBar = p.X >= _shownRect.Left - slack && p.X < _shownRect.Right + slack
                       && p.Y >= _shownRect.Top - slack && p.Y < _shownRect.Bottom + slack;

        if (_hidden)
        {
            if (atEdge) SlideTo(0);
            return;
        }

        if (overBar || atEdge || _view.IsMenuOpen)
        {
            _mouseLeftAt = null;
        }
        else
        {
            _mouseLeftAt ??= DateTime.Now;
            if ((DateTime.Now - _mouseLeftAt.Value).TotalMilliseconds > HideDelayMs) SlideTo(1);
        }
    }

    private void SlideTo(double target)
    {
        _slideTarget = target;
        _hidden = target > 0;
        _mouseLeftAt = null;
        if (target == 0 && !IsVisible) Show(); // appears at its hidden position, then slides in
        _slideTimer.Start();
    }

    /// <summary>One animation step (about 150 ms in total), easing toward the target.</summary>
    private void SlideStep()
    {
        _slide += Math.Sign(_slideTarget - _slide) * 0.1;
        if (Math.Abs(_slide - _slideTarget) < 0.05) _slide = _slideTarget;
        MoveToSlidePosition();

        if (_slide == _slideTarget)
        {
            _slideTimer.Stop();
            if (_slide >= 1) Hide(); // fully away: also never peeks onto a monitor beyond this edge
        }
    }

    /// <summary>Moves the window to the shown position, pushed off the edge by the current slide amount.</summary>
    private void MoveToSlidePosition()
    {
        int height = _shownRect.Bottom - _shownRect.Top;
        int distanceToEdge = _edge == TaskbarEdge.Top ? _shownRect.Bottom - _strip.Top : _strip.Bottom - _shownRect.Top;
        double eased = 1 - Math.Pow(1 - _slide, 2); // starts fast, slows down at the end
        int offset = (int)Math.Round(Math.Min(distanceToEdge, height + 20) * (_slideTarget > 0 ? eased : _slide));
        if (_edge == TaskbarEdge.Top) offset = -offset;

        var rc = _shownRect;
        rc.Top += offset;
        rc.Bottom += offset;
        AppBar.MoveWindow(this, rc);
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
        _strip = strip;
        _shownRect = new RECT { Left = left, Top = top, Right = right, Bottom = top + height };

        if (_design.AutoHide) MoveToSlidePosition(); // keep the current slide state
        else AppBar.MoveWindow(this, _shownRect);
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
