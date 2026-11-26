using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

public enum WelcomeBackground { Wallpaper, Color, Picture }

/// <summary>The welcome screen's look. Saved in settings.json; every value has a default.</summary>
public sealed class WelcomeLook
{
    /// <summary>Shown at sign-in as the shell. Off: only a small spinner.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>On every monitor (the others show only the background); off: the main monitor only.</summary>
    public bool AllMonitors { get; set; } = true;

    public WelcomeBackground Background { get; set; } = WelcomeBackground.Wallpaper;
    public string? Picture { get; set; }
    public string Color { get; set; } = "#101418";

    /// <summary>Blur of the wallpaper or picture, in pixels (0 = sharp).</summary>
    public double Blur { get; set; } = 30;

    /// <summary>How much darker the background is, in percent (so the text stands out).</summary>
    public double Dim { get; set; } = 35;

    public bool ShowUser { get; set; } = true;
    public bool ShowGreeting { get; set; } = true;
    /// <summary>Your own greeting; empty = "Good morning / afternoon / evening, (first name)".</summary>
    public string? Greeting { get; set; }
    public bool ShowClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowSpinner { get; set; } = true;

    public string TextColor { get; set; } = "#FFFFFF";
    /// <summary>Null: Segoe UI Variable.</summary>
    public string? Font { get; set; }

    /// <summary>It stays at least this long, even when the taskbar is up sooner.</summary>
    public double MinimumMs { get; set; } = 1200;
    public double FadeMs { get; set; } = 500;
}

/// <summary>
/// The welcome screen: right after signing in as the shell, until the taskbar is up, instead of a black screen.
/// It runs in its own windows on its own thread, so its clock and spinner keep moving while the app is busy starting.
/// Gone by itself after 25 seconds at most.
/// </summary>
internal sealed class WelcomeScreen
{
    private static WelcomeScreen? _current;
    private Dispatcher? _dispatcher;
    private readonly List<Window> _windows = new();
    private DateTime _shownAt;
    private bool _stopping;
    private readonly WelcomeLook _look;

    private WelcomeScreen(WelcomeLook look) => _look = look;

    /// <summary>Shows the welcome screen on these monitors (bounds in physical pixels; the main one gets the text).</summary>
    public static void Start(WelcomeLook look, IReadOnlyList<(RECT Bounds, bool IsPrimary)> monitors)
    {
        if (_current != null || monitors.Count == 0) return;
        var screen = new WelcomeScreen(look);
        _current = screen;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                screen._dispatcher = Dispatcher.CurrentDispatcher;
                // So a picture read in the background comes back to this thread (before Dispatcher.Run there's none yet).
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(screen._dispatcher));
                foreach (var (bounds, primary) in monitors)
                {
                    if (!primary && !look.AllMonitors) continue;
                    var window = screen.Build(bounds, primary);
                    window.Show();
                    screen._windows.Add(window);
                }
                screen._shownAt = DateTime.Now;
                var limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                limit.Tick += (_, _) => { limit.Stop(); screen.FadeOut(); };
                limit.Start();
            }
            catch (Exception ex)
            {
                ShellMode.Log($"Welcome screen: {ex.Message}");
                ready.Set();
                screen._dispatcher?.InvokeShutdown();
                return;
            }
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Welcome screen" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // At most a moment: the app goes on starting in the meantime (the welcome screen covers it once it's up).
        ready.Wait(TimeSpan.FromMilliseconds(500));
    }

    /// <summary>The taskbar is up: fades out (after the minimum time).</summary>
    public static void Stop()
    {
        var screen = _current;
        _current = null;
        try { screen?._dispatcher?.BeginInvoke(screen.StopWhenShownLongEnough); }
        catch (Exception) { /* its thread is gone already */ }
    }

    /// <summary>A preview ("Try it"): shown for a few seconds, without being the shell.</summary>
    public static void Preview(WelcomeLook look)
    {
        if (_current != null) return;
        var monitors = Monitors.All().Select(m => (m.Bounds, m.IsPrimary)).ToList();
        Start(look, monitors);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { timer.Stop(); Stop(); };
        timer.Start();
    }

    private void StopWhenShownLongEnough()
    {
        double left = Math.Clamp(_look.MinimumMs, 0, 10000) - (DateTime.Now - _shownAt).TotalMilliseconds;
        if (left <= 0) { FadeOut(); return; }
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(left) };
        wait.Tick += (_, _) => { wait.Stop(); FadeOut(); };
        wait.Start();
    }

    private void FadeOut()
    {
        if (_stopping) return;
        _stopping = true;
        if (_windows.Count == 0) { _dispatcher?.InvokeShutdown(); return; }
        var length = TimeSpan.FromMilliseconds(Math.Clamp(_look.FadeMs, 0, 3000));
        int left = _windows.Count;
        foreach (var window in _windows)
        {
            var fade = new DoubleAnimation(0, length) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) =>
            {
                window.Close();
                if (--left == 0) _dispatcher?.InvokeShutdown();
            };
            // A slight zoom towards you while it fades: like stepping into the desktop.
            if (window.Content is FrameworkElement content && content.RenderTransform is ScaleTransform zoom)
            {
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.04, length));
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.04, length));
            }
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    // ---------- Building it ----------

    private Window Build(RECT bounds, bool primary)
    {
        var look = _look;
        var text = new SolidColorBrush(TaskbarView.ParseColor(look.TextColor, Colors.White));
        text.Freeze();
        FontFamily font;
        try { font = new FontFamily(string.IsNullOrWhiteSpace(look.Font) ? "Segoe UI Variable Display, Segoe UI" : look.Font); }
        catch (ArgumentException) { font = new FontFamily("Segoe UI"); }

        var root = new Grid
        {
            Background = new SolidColorBrush(TaskbarView.ParseColor(look.Color, System.Windows.Media.Color.FromRgb(0x10, 0x14, 0x18))),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };

        // The background picture (the wallpaper, or your own), blurred; slightly bigger, so the blur has no soft edges.
        string? file = look.Background switch
        {
            WelcomeBackground.Wallpaper => Wallpaper.Current().File,
            WelcomeBackground.Picture when !string.IsNullOrWhiteSpace(look.Picture) => Environment.ExpandEnvironmentVariables(look.Picture.Trim().Trim('"')),
            _ => null,
        };
        if (file != null)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
            double blur = Math.Clamp(look.Blur, 0, 120);
            if (blur > 0)
            {
                image.Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                image.RenderTransform = new ScaleTransform(1 + blur / 400, 1 + blur / 400);
            }
            root.Children.Add(image);
            // Read in the background (the wallpaper only once for everyone), then faded in.
            var load = look.Background == WelcomeBackground.Wallpaper ? Wallpaper.LoadShared(file) : Task.Run(() => Wallpaper.Load(file));
            load.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result is not BitmapSource picture) return;
                image.Source = picture;
                image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (look.Dim > 0)
            root.Children.Add(new Border { Background = Brushes.Black, Opacity = Math.Clamp(look.Dim, 0, 95) / 100 });

        if (primary)
        {
            var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextBlock Line(string value, double size, FontWeight weight, double gapBelow) => new()
            {
                Text = value, FontSize = size, FontWeight = weight, Foreground = text, FontFamily = font,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, gapBelow),
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45 },
            };

            // Your name and picture can take a moment to look up right after signing in: in the background, so the
            // screen shows at once; they slide in above the clock when they're there.
            bool needsName = look.ShowUser || (look.ShowGreeting && string.IsNullOrWhiteSpace(look.Greeting));
            if (look.ShowGreeting && !needsName) middle.Children.Add(Line(Greeting(look.Greeting, ""), 30, FontWeights.Normal, 26));
            if (needsName)
            {
                var top = new StackPanel { Opacity = 0 };
                middle.Children.Add(top);
                Task.Run(() => (Name: StartMenuWindow.DisplayName(), Picture: look.ShowUser ? StartMenuWindow.UserPicture(null) : null))
                    .ContinueWith(t =>
                    {
                        if (t.Status != TaskStatus.RanToCompletion) return;
                        var (name, picture) = t.Result;
                        if (look.ShowUser)
                        {
                            if (picture != null)
                                top.Children.Add(new System.Windows.Shapes.Ellipse
                                {
                                    Width = 96, Height = 96, Margin = new Thickness(0, 0, 0, 14),
                                    Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
                                    Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.4 },
                                });
                            top.Children.Add(Line(name, 22, FontWeights.SemiBold, 18));
                        }
                        if (look.ShowGreeting) top.Children.Add(Line(Greeting(look.Greeting, name), 30, FontWeights.Normal, 26));
                        top.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
                    }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            TextBlock? clock = null, date = null;
            if (look.ShowClock) middle.Children.Add(clock = Line("", 84, FontWeights.Light, 0));
            if (look.ShowDate) middle.Children.Add(date = Line("", 20, FontWeights.Normal, 0));
            void Tick()
            {
                var now = DateTime.Now;
                if (clock != null) clock.Text = now.ToString("t");
                if (date != null) date.Text = now.ToString("D");
            }
            Tick();
            if (clock != null || date != null)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) => Tick();
                timer.Start();
            }
            // A soft rise into place.
            middle.RenderTransform = new TranslateTransform(0, 16);
            middle.Opacity = 0;
            middle.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)));
            middle.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            root.Children.Add(middle);

            if (look.ShowSpinner) root.Children.Add(Spinner(text));
        }

        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, Background = Brushes.Black, Content = root, Title = "Welcome", AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.Manual, Width = 100, Height = 100,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)); // not in Alt+Tab, no focus
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SWP_NOACTIVATE);
        };
        return window;
    }

    /// <summary>Your greeting, or one for the time of day with your first name.</summary>
    private static string Greeting(string? own, string name)
    {
        if (!string.IsNullOrWhiteSpace(own)) return own.Trim();
        int hour = DateTime.Now.Hour;
        string greeting = Loc.T(hour is >= 5 and < 12 ? "Good morning" : hour is >= 12 and < 18 ? "Good afternoon" : "Good evening");
        string first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length > 0 ? $"{greeting}, {first}" : greeting;
    }

    /// <summary>The spinning ring at the bottom middle.</summary>
    private static FrameworkElement Spinner(Brush color)
    {
        var rotate = new RotateTransform();
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 16,1.5 A 14.5,14.5 0 1 1 1.5,16"), Stroke = color, StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 32, Height = 32, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotate,
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        var ring = new Grid
        {
            Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 120), Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 },
        };
        ring.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = color, StrokeThickness = 3, Opacity = 0.25,
        });
        ring.Children.Add(arc);
        return ring;
    }
}

/// <summary>"Welcome screen...": what shows right after signing in as the shell, with a preview as "Try it".</summary>
internal sealed class WelcomeSettingsWindow : SettingsForm
{
    public WelcomeSettingsWindow(WelcomeLook look, Action save)
        : base("Welcome screen", save, () => WelcomeScreen.Preview(look))
    {
        Header("Welcome screen");
        Check("Show a welcome screen while CHROVIQ starts", look.Enabled, v => look.Enabled = v);
        Hint("As your shell: right after you sign in, until the taskbar is up, instead of a black screen. Off: only a small spinner. \"Try it\" shows it for a few seconds.");
        Check("On every monitor", look.AllMonitors, v => look.AllMonitors = v);

        Header("Background");
        Combo("Background", new[] { "The wallpaper", "A color", "A picture" }, (int)look.Background, i => look.Background = (WelcomeBackground)i);
        Text("Picture (image file)", look.Picture ?? "", v => look.Picture = string.IsNullOrWhiteSpace(v) ? null : v.Trim().Trim('"'),
            "For \"A picture\": a full path, like C:\\Users\\You\\Pictures\\welcome.jpg.");
        ColorField("Color", look.Color, v => look.Color = string.IsNullOrWhiteSpace(v) ? "#101418" : v, "Also under the picture while it loads.");
        Slider("Blur (pixels)", look.Blur, 0, 120, v => look.Blur = Math.Round(v));
        Slider("Darken (%)", look.Dim, 0, 95, v => look.Dim = Math.Round(v));

        Header("What it shows");
        Check("Your picture and name", look.ShowUser, v => look.ShowUser = v);
        Check("Greeting", look.ShowGreeting, v => look.ShowGreeting = v);
        Text("Greeting text", look.Greeting ?? "", v => look.Greeting = string.IsNullOrWhiteSpace(v) ? null : v,
            "Empty: Good morning / afternoon / evening, with your first name.");
        Check("Clock", look.ShowClock, v => look.ShowClock = v);
        Check("Date", look.ShowDate, v => look.ShowDate = v);
        Check("Spinner", look.ShowSpinner, v => look.ShowSpinner = v);

        Header("Text");
        ColorField("Text color", look.TextColor, v => look.TextColor = string.IsNullOrWhiteSpace(v) ? "#FFFFFF" : v);
        Font("Font", look.Font ?? "Segoe UI Variable Display", v => look.Font = v == "Segoe UI Variable Display" ? null : v);

        Header("Timing");
        Slider("Show at least (ms)", look.MinimumMs, 0, 5000, v => look.MinimumMs = Math.Round(v / 100) * 100);
        Slider("Fade out (ms)", look.FadeMs, 0, 2000, v => look.FadeMs = Math.Round(v / 50) * 50);
    }
}
