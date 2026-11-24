using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsCustomizer;

/// <summary>Finds the main color of an app icon (Spotify: green, Discord: blue), for the "glow" option.</summary>
internal static class IconColors
{
    // Remembered per icon image; forgotten automatically when the image is no longer used.
    private static readonly ConditionalWeakTable<ImageSource, object> Cache = new();

    /// <summary>The icon's most colorful average color, or null for gray/empty icons.</summary>
    public static Color? MainColor(ImageSource? icon)
    {
        if (icon is not BitmapSource bitmap) return null;
        if (Cache.TryGetValue(icon, out var cached)) return (Color?)cached;

        Color? result = null;
        try
        {
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);

            // Average the clearly colored pixels, weighted by how colorful they are.
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2], pa = pixels[i + 3];
                if (pa < 128) continue;
                int max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                if (max < 50) continue;                       // nearly black
                double saturation = (max - min) / (double)max;
                if (saturation < 0.25) continue;              // gray/white
                double w = saturation * saturation;
                r += pr * w; g += pg * w; b += pb * w; weight += w;
            }

            if (weight > 0)
                result = Brighten(Color.FromRgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight)));
        }
        catch (Exception)
        {
            // Unusual image format: no glow color.
        }

        Cache.AddOrUpdate(icon, result!);
        return result;
    }

    private static readonly ConditionalWeakTable<ImageSource, Dictionary<Color, ImageSource>> TintCache = new();

    /// <summary>
    /// A one-color version of an icon that keeps its details: each pixel's brightness is kept
    /// (stretched over the icon's own range) and colored with <paramref name="tint"/>, transparency unchanged.
    /// A plain silhouette would turn filled icons (a green circle, a blue square) into blank shapes.
    /// </summary>
    public static ImageSource? Tinted(ImageSource? icon, Color tint)
    {
        if (icon is not BitmapSource bitmap) return null;
        var perColor = TintCache.GetOrCreateValue(icon);
        lock (perColor)
            if (perColor.TryGetValue(tint, out var cached)) return cached;

        ImageSource? result = null;
        try
        {
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);

            // Brightness range of the visible pixels, to stretch it to full contrast.
            double min = 1, max = 0;
            var light = new double[width * height];
            for (int i = 0, p = 0; i < pixels.Length; i += 4, p++)
            {
                if (pixels[i + 3] == 0) continue;
                // Colors are premultiplied-free in Bgra32; brightness by the usual eye weights.
                light[p] = (0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]) / 255.0;
                if (pixels[i + 3] > 64) { min = Math.Min(min, light[p]); max = Math.Max(max, light[p]); }
            }
            double range = Math.Max(0.15, max - min);

            for (int i = 0, p = 0; i < pixels.Length; i += 4, p++)
            {
                if (pixels[i + 3] == 0) continue;
                double l = Math.Clamp((light[p] - min) / range, 0, 1);
                double factor = 0.35 + 0.65 * l; // darkest parts still visible, lightest parts full tint
                pixels[i] = (byte)(tint.B * factor);
                pixels[i + 1] = (byte)(tint.G * factor);
                pixels[i + 2] = (byte)(tint.R * factor);
            }

            var tinted = BitmapSource.Create(width, height, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null, pixels, width * 4);
            tinted.Freeze();
            result = tinted;
        }
        catch (Exception)
        {
            // Unusual image format: no tinted version (the normal icon shows).
        }

        lock (perColor) perColor[tint] = result!;
        return result;
    }

    /// <summary>Lifts dark averages so the glow is visible on a dark bar.</summary>
    private static Color Brighten(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max >= 200 || max == 0) return c;
        double factor = 200.0 / max;
        return Color.FromRgb((byte)Math.Min(255, c.R * factor), (byte)Math.Min(255, c.G * factor), (byte)Math.Min(255, c.B * factor));
    }
}
