using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>Ready-made opening animations, or <see cref="Custom"/> to use your own values.</summary>
public enum AnimationStyle { None, Fade, Slide, Zoom, Pop, Custom }

/// <summary>Which way a sliding popup moves.</summary>
public enum AnimationDirection { FromTaskbar, Up, Down, Left, Right }

/// <summary>How the animation speeds up and slows down.</summary>
public enum AnimationEasing { Linear, Smooth, Strong, Overshoot, Bounce, Elastic }

/// <summary>
/// How a popup (start menu, search, Alt+Tab) appears. The styles are presets; <see cref="AnimationStyle.Custom"/>
/// uses <see cref="Fade"/>, <see cref="Distance"/>, <see cref="Direction"/> and <see cref="StartSize"/> as you set them.
/// </summary>
public sealed class PopupAnimation
{
    public AnimationStyle Style { get; set; } = AnimationStyle.Slide;

    /// <summary>Length in milliseconds.</summary>
    public double DurationMs { get; set; } = 220;

    public AnimationEasing Easing { get; set; } = AnimationEasing.Smooth;

    // ---------- Custom ----------

    /// <summary>Fade in from invisible.</summary>
    public bool Fade { get; set; } = true;

    /// <summary>How far it slides, in pixels (0 = no slide).</summary>
    public double Distance { get; set; } = 60;

    public AnimationDirection Direction { get; set; } = AnimationDirection.FromTaskbar;

    /// <summary>Size at the start in percent (100 = no zoom; below 100 grows, above 100 shrinks into place).</summary>
    public double StartSize { get; set; } = 100;

    public static PopupAnimation Default(AnimationStyle style, double ms) => new() { Style = style, DurationMs = ms };

    /// <summary>The values to animate with: the preset's, or (Custom) the ones set here.</summary>
    internal (bool fade, double distance, AnimationDirection direction, double startScale, AnimationEasing easing) Resolve() => Style switch
    {
        AnimationStyle.Fade => (true, 0, AnimationDirection.FromTaskbar, 1, Easing),
        AnimationStyle.Slide => (true, 60, AnimationDirection.FromTaskbar, 1, Easing),
        AnimationStyle.Zoom => (true, 0, AnimationDirection.FromTaskbar, 0.88, Easing),
        AnimationStyle.Pop => (true, 20, AnimationDirection.FromTaskbar, 0.8, AnimationEasing.Overshoot),
        _ => (Fade, Math.Clamp(Distance, 0, 2000), Direction, Math.Clamp(StartSize, 10, 300) / 100, Easing),
    };

    /// <summary>
    /// Plays the animation on a window's content (the window must allow transparency). Call it right before the
    /// window is shown: the start state is applied at once, so the first frame never flashes fully visible.
    /// The clock starts with the first frame actually drawn (after <paramref name="startAfter"/>, at most 400 ms),
    /// so building the window's contents doesn't eat into the animation.
    /// </summary>
    /// <param name="fromBelow">The taskbar is below the popup (the popup slides up from it).</param>
    /// <param name="onFrame">Called after every frame (Alt+Tab moves its window previews with it).</param>
    /// <returns>Stops the animation and puts the content in its final state.</returns>
    public Action Play(FrameworkElement content, bool fromBelow, Action? onFrame = null, Action? done = null, System.Threading.Tasks.Task? startAfter = null)
    {
        var (fade, distance, direction, startScale, easing) = Resolve();
        double duration = Math.Clamp(DurationMs, 0, 3000);
        if (Style == AnimationStyle.None || duration <= 0 || (!fade && distance <= 0 && Math.Abs(startScale - 1) < 0.001))
        {
            content.Opacity = 1;
            content.RenderTransform = Transform.Identity;
            done?.Invoke();
            return () => { };
        }

        // Where it starts, as a direction to move away from.
        var (dx, dy) = direction switch
        {
            AnimationDirection.Up => (0.0, 1.0),
            AnimationDirection.Down => (0.0, -1.0),
            AnimationDirection.Left => (1.0, 0.0),
            AnimationDirection.Right => (-1.0, 0.0),
            _ => fromBelow ? (0.0, 1.0) : (0.0, -1.0),
        };
        var move = new TranslateTransform();
        var size = new ScaleTransform();
        content.RenderTransformOrigin = new Point(0.5, fromBelow ? 1 : 0);
        content.RenderTransform = new TransformGroup { Children = { size, move } };
        // Drawn once into a bitmap and moved/faded as a picture while animating: smooth even for a big menu.
        content.CacheMode = new BitmapCache { SnapsToDevicePixels = true };

        void Apply(double t)
        {
            double e = Ease(easing, t);
            content.Opacity = fade ? Math.Clamp(e, 0, 1) : 1;
            move.X = dx * distance * (1 - e);
            move.Y = dy * distance * (1 - e);
            double s = startScale + (1 - startScale) * e;
            size.ScaleX = size.ScaleY = Math.Max(0.01, s);
        }
        Apply(0);

        var clock = new Stopwatch();
        var waitedSince = Stopwatch.StartNew();
        bool finished = false;
        EventHandler? frame = null;
        void Finish()
        {
            if (finished) return;
            finished = true;
            CompositionTarget.Rendering -= frame;
            Apply(1);
            content.CacheMode = null; // live again (animated materials, hover effects)
            onFrame?.Invoke();
            done?.Invoke();
        }
        frame = (_, _) =>
        {
            if (!clock.IsRunning)
            {
                if (startAfter is { IsCompleted: false } && waitedSince.ElapsedMilliseconds < 400) return;
                clock.Start();
                return; // the first frame after the wait shows the start state
            }
            double t = clock.Elapsed.TotalMilliseconds / duration;
            if (t >= 1) { Finish(); return; }
            Apply(t);
            onFrame?.Invoke();
        };
        CompositionTarget.Rendering += frame;
        return Finish;
    }

    /// <summary>0..1 → eased 0..1 (Overshoot and Elastic briefly go past 1).</summary>
    internal static double Ease(AnimationEasing easing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        switch (easing)
        {
            case AnimationEasing.Linear: return t;
            case AnimationEasing.Strong: return 1 - Math.Pow(1 - t, 5);
            case AnimationEasing.Overshoot:
                const double c1 = 1.70158, c3 = c1 + 1;
                return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
            case AnimationEasing.Bounce:
                const double n = 7.5625, d = 2.75;
                if (t < 1 / d) return n * t * t;
                if (t < 2 / d) { t -= 1.5 / d; return n * t * t + 0.75; }
                if (t < 2.5 / d) { t -= 2.25 / d; return n * t * t + 0.9375; }
                t -= 2.625 / d; return n * t * t + 0.984375;
            case AnimationEasing.Elastic:
                if (t <= 0 || t >= 1) return t;
                return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1;
            default: return 1 - Math.Pow(1 - t, 3); // Smooth
        }
    }
}
