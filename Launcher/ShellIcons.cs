using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsCustomizer;

/// <summary>
/// Icons the way Explorer gets them, for anything with a shell name: Store apps
/// ("shell:AppsFolder\..."), files and folders. Store apps have no icon file, so this is the only way
/// to get their real icon. Results are cached. Call on an STA thread (the UI thread is fine).
/// </summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr bitmap, int size, out BITMAP info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    /// <summary>The icon of a shell item, <paramref name="size"/> pixels (or bigger); null if Windows has none.</summary>
    public static ImageSource? Get(string shellName, int size = 48)
    {
        string key = shellName + "|" + size;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? result = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(shellName, IntPtr.Zero, ref iid, out var factory) == 0)
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out bitmap) == 0)
                    result = ToImage(bitmap);
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            // Unknown name or no icon: the caller shows a symbol instead.
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }

        Cache[key] = result;
        return result;
    }

    /// <summary>
    /// Copies the 32-bit bitmap's pixels (premultiplied alpha) into a WPF image. WPF's own
    /// CreateBitmapSourceFromHBitmap drops the transparency, which would give icons a black box.
    /// </summary>
    private static ImageSource? ToImage(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmBits == IntPtr.Zero || info.bmBitsPixel != 32)
            return null;

        int stride = info.bmWidthBytes;
        var pixels = new byte[stride * info.bmHeight];
        Marshal.Copy(info.bmBits, pixels, 0, pixels.Length);

        // Shell bitmaps are stored bottom-up: flip the rows.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < info.bmHeight; y++)
            Buffer.BlockCopy(pixels, y * stride, flipped, (info.bmHeight - 1 - y) * stride, stride);

        var image = BitmapSource.Create(info.bmWidth, info.bmHeight, 96, 96, PixelFormats.Pbgra32, null, flipped, stride);
        image.Freeze();
        return image;
    }
}
