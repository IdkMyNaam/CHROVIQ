using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Pictures of windows as they last looked, for the previews of minimized windows (Windows doesn't draw a minimized
/// window, so a live preview of it is empty). A window is pictured when it's about to be minimized and when it stops
/// being the active window. Kept only in memory (never saved), at most the 40 most recent, smaller than full size.
/// </summary>
internal static class WindowSnapshots
{
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint PW_RENDERFULLCONTENT = 0x2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int MaxPictures = 40, MaxWidth = 480;

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, BitmapSource> Pictures = new();
    private static readonly LinkedList<IntPtr> Order = new(); // oldest first
    private static readonly List<IntPtr> Hooks = new();
    private static WinEventProc? _callback;
    private static IntPtr _lastForeground;

    public static bool Enabled { get; private set; }

    /// <summary>Starts or stops taking pictures (Taskbar page: "Previews of minimized windows").</summary>
    public static void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        if (on)
        {
            _callback = OnEvent;
            foreach (var e in new[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZESTART })
            {
                var hook = SetWinEventHook(e, e, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                if (hook != IntPtr.Zero) Hooks.Add(hook);
            }
            _lastForeground = GetForegroundWindow();
        }
        else
        {
            foreach (var hook in Hooks) UnhookWinEvent(hook);
            Hooks.Clear();
            lock (Gate) { Pictures.Clear(); Order.Clear(); }
        }
    }

    /// <summary>The last picture of a window, or null.</summary>
    public static BitmapSource? Get(IntPtr hwnd)
    {
        if (!Enabled) return null;
        lock (Gate) return Pictures.GetValueOrDefault(hwnd);
    }

    private static void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // the window itself, not a part of it
        if (eventType == EVENT_SYSTEM_MINIMIZESTART)
        {
            // Still drawn at its full size at this moment.
            Take(hwnd);
            return;
        }
        // The previous active window, as it looked when you left it.
        var previous = _lastForeground;
        _lastForeground = hwnd;
        if (previous != IntPtr.Zero && previous != hwnd) Take(previous);
    }

    private static void Take(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;
        // Off the UI thread: drawing a big window into a picture can take a moment.
        Task.Run(() =>
        {
            try
            {
                if (Capture(hwnd) is not { } picture) return;
                lock (Gate)
                {
                    if (Pictures.ContainsKey(hwnd)) Order.Remove(hwnd);
                    Pictures[hwnd] = picture;
                    Order.AddLast(hwnd);
                    while (Order.Count > MaxPictures)
                    {
                        Pictures.Remove(Order.First!.Value);
                        Order.RemoveFirst();
                    }
                }
            }
            catch (Exception) { /* a window that can't be pictured keeps its "Minimized" text */ }
        });
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    /// <summary>The window drawn into a picture (with what DirectX and browsers draw too), cut to its visible frame.</summary>
    private static BitmapSource? Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var whole)) return null;
        int width = whole.Right - whole.Left, height = whole.Bottom - whole.Top;
        if (width < 20 || height < 20 || width > 10000 || height > 10000) return null;

        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            if (bitmap == IntPtr.Zero || !PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)) return null;
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha at 0
            BitmapSource picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);

            // Without the invisible resize borders around the window.
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0)
            {
                var crop = new Int32Rect(Math.Max(0, frame.Left - whole.Left), Math.Max(0, frame.Top - whole.Top),
                    Math.Min(width, frame.Right - frame.Left), Math.Min(height, frame.Bottom - frame.Top));
                if (crop.Width > 0 && crop.Height > 0 && crop.X + crop.Width <= width && crop.Y + crop.Height <= height)
                    picture = new CroppedBitmap(picture, crop);
            }
            if (picture.PixelWidth > MaxWidth)
            {
                double scale = (double)MaxWidth / picture.PixelWidth;
                picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
            }
            // A frozen copy: usable from any thread, and the big original can go.
            var small = new WriteableBitmap(picture);
            small.Freeze();
            return small;
        }
        finally
        {
            SelectObject(dc, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
