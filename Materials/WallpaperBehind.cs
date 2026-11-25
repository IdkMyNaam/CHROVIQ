using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// For materials with "UsesWallpaper": a brush with exactly the part of the wallpaper that is behind a surface
/// (worked out from the wallpaper picture, its placement and the surface's place on screen, so it costs nothing
/// per frame: no screen capture). Updated when the surface moves or changes size. A static picture only:
/// animated wallpapers (Wallpaper Engine) show their still picture, if Windows has one.
/// </summary>
internal static class WallpaperBehind
{
    private static (string File, DateTime Written, BitmapSource? Image)? _cache;

    /// <summary>
    /// Black, for when there's no wallpaper picture (yet). A shader's picture input has to be an image (or a visual),
    /// never a plain color brush, so this is a 1×1 black image.
    /// </summary>
    private static readonly ImageBrush NoWallpaper = MakeNoWallpaper();

    private static ImageBrush MakeNoWallpaper()
    {
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        pixel.Freeze();
        var brush = new ImageBrush(pixel);
        brush.Freeze();
        return brush;
    }

    private static BitmapSource? Picture(string file)
    {
        var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (_cache is { } c && c.File == file && c.Written == written) return c.Image;
        var image = Wallpaper.Load(file);
        _cache = (file, written, image);
        return image;
    }

    /// <summary>
    /// What glass shows: false = the wallpaper picture (default), true = what's really on screen behind it
    /// (see <see cref="ScreenBehind"/>; leaves the surface's window out of screenshots).
    /// </summary>
    public static bool Live { get; set; }

    /// <summary>Gives the effect the wallpaper behind this surface, now and whenever the surface moves.</summary>
    public static void Track(FrameworkElement surface, MaterialEffect effect)
    {
        effect.Wallpaper = NoWallpaper;
        if (Live)
        {
            // Live: once the surface is in its window (the window is left out of capture then).
            if (surface.IsLoaded && ScreenBehind.Track(surface, effect)) return;
            if (!surface.IsLoaded)
            {
                surface.Loaded += (_, _) => { if (!ScreenBehind.Track(surface, effect)) TrackPicture(surface, effect); };
                return;
            }
        }
        TrackPicture(surface, effect);
    }

    private static void TrackPicture(FrameworkElement surface, MaterialEffect effect)
    {
        void Update() { if (ReferenceEquals(surface.Effect, effect)) effect.Wallpaper = BrushBehind(surface) ?? NoWallpaper; }
        surface.SizeChanged += (_, _) => Update();
        surface.Loaded += (_, _) =>
        {
            Update();
            if (Window.GetWindow(surface) is { } window) window.LocationChanged += (_, _) => Update(); // the bar slides, a window moves
        };
        if (surface.IsLoaded) Update();
    }

    /// <summary>The wallpaper part behind the surface, mapped onto it (null when there's no picture or no place yet).</summary>
    private static Brush? BrushBehind(FrameworkElement surface)
    {
        if (!surface.IsLoaded || surface.ActualWidth <= 0 || PresentationSource.FromVisual(surface) == null) return null;
        var (file, placement, _) = Wallpaper.Current();
        if (file == null || Picture(file) is not { } image) return null;

        // The surface on screen (physical pixels) and its monitor.
        var topLeft = surface.PointToScreen(new Point(0, 0));
        var bottomRight = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var center = new POINT { X = (int)((topLeft.X + bottomRight.X) / 2), Y = (int)((topLeft.Y + bottomRight.Y) / 2) };
        if (!GetMonitorInfo(MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST), ref info)) return null;
        double mx = info.rcMonitor.Left, my = info.rcMonitor.Top;
        double mw = info.rcMonitor.Right - mx, mh = info.rcMonitor.Bottom - my;

        // How the picture is laid over the monitor (pixels), like Windows does for each placement.
        double iw = image.PixelWidth, ih = image.PixelHeight;
        double sx, sy, dx, dy;
        switch (placement)
        {
            case Wallpaper.Placement.Stretch: sx = mw / iw; sy = mh / ih; dx = dy = 0; break;
            case Wallpaper.Placement.Fit: sx = sy = Math.Min(mw / iw, mh / ih); dx = (mw - iw * sx) / 2; dy = (mh - ih * sy) / 2; break;
            case Wallpaper.Placement.Center: sx = sy = 1; dx = (mw - iw) / 2; dy = (mh - ih) / 2; break;
            case Wallpaper.Placement.Tile: sx = sy = 1; dx = dy = 0; break;
            default: sx = sy = Math.Max(mw / iw, mh / ih); dx = (mw - iw * sx) / 2; dy = (mh - ih * sy) / 2; break; // Fill, Span
        }

        // The surface's rectangle in the picture's own units (the brush's Viewbox).
        double toUnits = image.Width / iw; // picture pixels -> its WPF units
        var box = new Rect(
            (topLeft.X - mx - dx) / sx * toUnits, (topLeft.Y - my - dy) / sy * toUnits,
            (bottomRight.X - topLeft.X) / sx * toUnits, (bottomRight.Y - topLeft.Y) / sy * toUnits);
        var brush = new ImageBrush(image)
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box, Stretch = Stretch.Fill,
            TileMode = placement == Wallpaper.Placement.Tile ? TileMode.Tile : TileMode.None,
        };
        brush.Freeze();
        return brush;
    }
}
