using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>How an auto-hiding taskbar comes in and goes away.</summary>
public enum AutoHideStyle { Slide, SlideAndFade, Fade, None }

/// <summary>
/// The taskbars' animations: the ^ tray flyout, the window previews, and auto-hide. The same for every taskbar
/// and design. Saved in settings.json; every value has a default.
/// </summary>
public sealed class TaskbarAnimations
{
    /// <summary>The settings in use (set when the settings load).</summary>
    public static TaskbarAnimations Current { get; set; } = new();

    public PopupAnimation TrayFlyout { get; set; } = new()
    {
        Style = AnimationStyle.Custom, DurationMs = 180, Easing = AnimationEasing.Smooth, Fade = true, Distance = 16,
    };

    /// <summary>The window previews over a taskbar button. Zoom doesn't apply to them (Windows draws the previews).</summary>
    public PopupAnimation Previews { get; set; } = new()
    {
        Style = AnimationStyle.Custom, DurationMs = 160, Easing = AnimationEasing.Smooth, Fade = true, Distance = 10,
    };

    public AutoHideStyle AutoHide { get; set; } = AutoHideStyle.Slide;

    /// <summary>Length of coming in or going away, in milliseconds.</summary>
    public double AutoHideMs { get; set; } = 200;

    public AnimationEasing AutoHideEasing { get; set; } = AnimationEasing.Smooth;

    /// <summary>How long the mouse is off the bar before it goes away.</summary>
    public double HideDelayMs { get; set; } = 700;
}

/// <summary>"Taskbar animations...": the tray flyout, previews and auto-hide, with a sample as "Try it".</summary>
internal sealed class TaskbarAnimationsWindow : SettingsForm
{
    public TaskbarAnimationsWindow(TaskbarAnimations animations, Action save)
        : base("Taskbar animations", () => { TaskbarAnimations.Current = animations; save(); }, () => ShowSample(animations))
    {
        Header("Tray flyout");
        Hint("The ^ panel with the hidden tray icons.");
        AnimationFields(animations.TrayFlyout ??= new TaskbarAnimations().TrayFlyout);

        Header("Window previews");
        Hint("The live previews over a taskbar button. Zoom doesn't apply to them: Windows draws the previews themselves.");
        AnimationFields(animations.Previews ??= new TaskbarAnimations().Previews);

        Header("Auto-hide");
        Hint("For designs with auto-hide: how the bar comes in when the mouse reaches the screen edge, and goes away again.");
        Combo("Animation", new[] { "Slide", "Slide and fade", "Fade", "None" }, (int)animations.AutoHide,
            i => animations.AutoHide = (AutoHideStyle)i);
        Slider("Length (ms)", animations.AutoHideMs, 0, 1000, v => animations.AutoHideMs = Math.Round(v / 10) * 10);
        Combo("Speed curve", new[] { "Even", "Smooth (slows at the end)", "Strong (fast start)", "Overshoot", "Bounce", "Elastic" },
            (int)animations.AutoHideEasing, i => animations.AutoHideEasing = (AnimationEasing)i);
        Slider("Hide after (ms)", animations.HideDelayMs, 0, 3000, v => animations.HideDelayMs = Math.Round(v / 50) * 50);
    }

    /// <summary>A sample flyout and a sample preview next to the mouse, each with its animation; gone after a few seconds or a click.</summary>
    private static void ShowSample(TaskbarAnimations animations)
    {
        TextBlock Label(string text) => new() { Text = Loc.T(text), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
        Border Box(string text, double width, double height)
        {
            var inside = new StackPanel();
            inside.Children.Add(Label(text));
            inside.Children.Add(new Border { Height = height, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
            return new Border
            {
                Width = width, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Margin = new Thickness(8),
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x24, 0x24, 0x24)), VerticalAlignment = VerticalAlignment.Bottom,
                Child = inside,
            };
        }
        var flyout = Box("Tray flyout", 170, 60);
        var preview = Box("Window previews", 220, 110);
        flyout.Opacity = preview.Opacity = 0;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 80, 0, 0) };
        row.Children.Add(flyout);
        row.Children.Add(preview);

        var mouse = System.Windows.Forms.Control.MousePosition;
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, Content = row, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.SourceInitialized += (_, _) =>
        {
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            window.Left = mouse.X / scale + 20;
            window.Top = mouse.Y / scale - 120;
        };
        window.MouseLeftButtonUp += (_, _) => window.Close();
        window.Show();
        // The flyout first, the preview a moment later, as if from a taskbar below.
        animations.TrayFlyout.Play(flyout, fromBelow: true);
        var next = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        next.Tick += (_, _) => { next.Stop(); animations.Previews.Play(preview, fromBelow: true); };
        next.Start();
        var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        close.Tick += (_, _) => { close.Stop(); if (window.IsLoaded) window.Close(); };
        close.Start();
    }
}
