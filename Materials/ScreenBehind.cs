using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>
/// "Live" glass: what is really on screen behind a surface (other windows, a moving Wallpaper Engine wallpaper,
/// the desktop), captured at the frame-rate limit. The surface's own window is left out of screen capture
/// (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE), so the capture shows what's under it, not the bar itself.
/// Side effect: that window doesn't show in screenshots, recordings or screen sharing either.
/// Pauses while a fullscreen app or game is in front and while the surface is hidden.
/// </summary>
/// <summary>A popup (start menu, search, Alt+Tab) that can have live glass: its own setting says whether it wants it.</summary>
internal interface ILiveGlassWindow
{
    bool WantsLiveGlass { get; }
}

internal static class ScreenBehind
{
    private sealed class Entry
    {
        public required WeakReference<FrameworkElement> Surface { get; init; }
        public required WeakReference<MaterialEffect> Effect { get; init; }
        /// <summary>The window left out of capture for this surface.</summary>
        public IntPtr Window { get; init; }
        public WriteableBitmap? Bitmap;
        public IntPtr MemoryDc, Dib, Bits;
        public int Width, Height;
    }

    private static readonly List<Entry> Entries = new();
    private static DispatcherTimer? _timer;

    /// <summary>Left out of capture already (or tried): window handle -> it worked.</summary>
    private static readonly Dictionary<IntPtr, bool> Excluded = new();

    /// <summary>False when Windows won't leave the window out of capture (the caller then uses the wallpaper picture).</summary>
    public static bool Track(FrameworkElement surface, MaterialEffect effect)
    {
        var window = Window.GetWindow(surface);
        // Only the taskbar bars, and popups whose own "Live glass" setting is on: everything else (the designer's
        // preview, Windows Customizer's own windows, menus) must keep showing in screenshots and screen sharing,
        // so those use the wallpaper picture instead.
        if (window is not AppBarWindow && window is not ILiveGlassWindow { WantsLiveGlass: true } && !AppThemes.WantsLiveGlass(window)) return false;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero || !ExcludeFromCapture(hwnd)) return false;

        Entries.Add(new Entry { Surface = new WeakReference<FrameworkElement>(surface), Effect = new WeakReference<MaterialEffect>(effect), Window = hwnd });
        window.Closed += (_, _) => Excluded.Remove(hwnd);
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Interval = TimeSpan.FromSeconds(1.0 / MaterialClock.FramesPerSecond);
        _timer.Start();
        return true;
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private static bool ExcludeFromCapture(IntPtr hwnd)
    {
        if (Excluded.TryGetValue(hwnd, out var done)) return done;
        bool ok = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        if (!ok) AppTrace.Log($"Live glass: Windows won't leave the window out of capture (error {Marshal.GetLastWin32Error()}); using the wallpaper picture");
        Excluded[hwnd] = ok;
        return ok;
    }

    private const uint WDA_NONE = 0;

    /// <summary>The window shows in screenshots and screen sharing again (no live glass on it any more).</summary>
    private static void IncludeInCapture(IntPtr hwnd)
    {
        if (!Excluded.Remove(hwnd)) return;
        SetWindowDisplayAffinity(hwnd, WDA_NONE); // fails harmlessly if the window is gone
    }

    /// <summary>Live glass switched off: every window shows in capture again (the bars are rebuilt with the picture).</summary>
    public static void StopAll()
    {
        foreach (var entry in Entries) Release(entry);
        Entries.Clear();
        foreach (var hwnd in Excluded.Keys.ToList()) IncludeInCapture(hwnd);
        _timer?.Stop();
    }

    private static void Tick()
    {
        if (MaterialClock.ForegroundIsFullscreen()) return; // a game or a video: leave the GPU and CPU to it
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (!entry.Surface.TryGetTarget(out var surface) || !entry.Effect.TryGetTarget(out var effect) || !ReferenceEquals(surface.Effect, effect))
            {
                Release(entry);
                Entries.RemoveAt(i); // the surface was rebuilt or closed, or has another material now
                if (!Entries.Any(e => e.Window == entry.Window)) IncludeInCapture(entry.Window);
                continue;
            }
            if (!surface.IsVisible || surface.ActualWidth <= 0 || PresentationSource.FromVisual(surface) == null) continue;
            try { Capture(entry, surface, effect); }
            catch (Exception ex) { AppTrace.Log($"Live glass: {ex.Message}"); }
        }
        if (Entries.Count == 0) _timer?.Stop();
    }

    /// <summary>Copies the screen behind the surface into its bitmap (which the shader reads as WALLPAPER).</summary>
    private static void Capture(Entry entry, FrameworkElement surface, MaterialEffect effect)
    {
        var topLeft = surface.PointToScreen(new Point(0, 0));
        var bottomRight = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
        int x = (int)Math.Round(topLeft.X), y = (int)Math.Round(topLeft.Y);
        int screenWidth = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X)), screenHeight = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));
        // Big surfaces (the start menu, a large Alt+Tab panel) are copied at half resolution: four times less work
        // per frame, and glass is soft anyway. The brush stretches it back to full size.
        bool half = screenWidth * screenHeight > 400_000;
        int width = half ? Math.Max(1, screenWidth / 2) : screenWidth, height = half ? Math.Max(1, screenHeight / 2) : screenHeight;

        if (entry.Bitmap == null || entry.Width != width || entry.Height != height)
        {
            Release(entry);
            entry.Width = width;
            entry.Height = height;
            var info = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            entry.MemoryDc = CreateCompatibleDC(IntPtr.Zero);
            entry.Dib = CreateDIBSection(entry.MemoryDc, ref info, 0, out entry.Bits, IntPtr.Zero, 0);
            SelectObject(entry.MemoryDc, entry.Dib);
            // Bgr32: the screen copy has no alpha (it would count as fully transparent otherwise).
            entry.Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            effect.Wallpaper = new ImageBrush(entry.Bitmap) { Stretch = Stretch.Fill };
        }

        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (half)
            {
                SetStretchBltMode(entry.MemoryDc, COLORONCOLOR); // fast; the material softens it anyway
                StretchBlt(entry.MemoryDc, 0, 0, width, height, screen, x, y, screenWidth, screenHeight, SRCCOPY);
            }
            else BitBlt(entry.MemoryDc, 0, 0, width, height, screen, x, y, SRCCOPY);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        entry.Bitmap.WritePixels(new Int32Rect(0, 0, width, height), entry.Bits, width * height * 4, width * 4);
    }

    private static void Release(Entry entry)
    {
        if (entry.Dib != IntPtr.Zero) DeleteObject(entry.Dib);
        if (entry.MemoryDc != IntPtr.Zero) DeleteDC(entry.MemoryDc);
        entry.Dib = entry.MemoryDc = entry.Bits = IntPtr.Zero;
        entry.Bitmap = null;
    }

    // ---------- GDI ----------

    private const int SRCCOPY = 0x00CC0020, COLORONCOLOR = 3;
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);
}
