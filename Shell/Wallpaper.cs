using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Puts the user's wallpaper on the screen when Windows Customizer is the shell. Normally Explorer does this
/// at sign-in (and for Windows' default themes it is the only one that does); without it the desktop stays black.
/// Only shows it for this sign-in: the user's wallpaper setting itself is not changed.
/// </summary>
internal static class Wallpaper
{
    private const int SPI_SETDESKWALLPAPER = 0x0014;
    private const int SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, string value, int flags);

    public enum Placement { Fill, Fit, Stretch, Tile, Center, Span }

    /// <summary>The wallpaper as Settings > Personalization has it: picture (null if none), placement, background color.</summary>
    public static (string? File, Placement Placement, System.Windows.Media.Color Color) Current()
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        string? file = desktop?.GetValue("Wallpaper") as string;
        if (!string.IsNullOrWhiteSpace(file)) file = Environment.ExpandEnvironmentVariables(file);
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                             @"Microsoft\Windows\Themes\TranscodedWallpaper");
            file = File.Exists(transcoded) ? transcoded : null;
        }

        var placement = (desktop?.GetValue("WallpaperStyle") as string, desktop?.GetValue("TileWallpaper") as string) switch
        {
            ("6", _) => Placement.Fit,
            ("2", _) => Placement.Stretch,
            ("22", _) => Placement.Span,
            ("0", "1") => Placement.Tile,
            ("0", _) => Placement.Center,
            _ => Placement.Fill, // "10", and Windows' default
        };

        var color = System.Windows.Media.Colors.Black;
        using (var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
            if (colors?.GetValue("Background") is string rgb && rgb.Split(' ') is { Length: 3 } parts &&
                byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                color = System.Windows.Media.Color.FromRgb(r, g, b);

        return (file, placement, color);
    }

    /// <summary>Loads a picture for drawing on any thread (frozen); null if it can't be read.</summary>
    private static readonly Dictionary<string, Task<System.Windows.Media.Imaging.BitmapSource?>> Loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture of a wallpaper file, read once in the background and shared by every monitor's desktop.</summary>
    public static Task<System.Windows.Media.Imaging.BitmapSource?> LoadShared(string file)
    {
        string key = file + "|" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0); // the same path can hold a new picture
        lock (Loading)
        {
            if (!Loading.TryGetValue(key, out var task))
            {
                // Only the latest picture is kept (an old one would only take memory).
                foreach (var old in Loading.Keys.Where(k => !k.StartsWith(file + "|", StringComparison.OrdinalIgnoreCase)).ToList()) Loading.Remove(old);
                Loading[key] = task = Task.Run(() => Load(file));
            }
            return task;
        }
    }

    /// <summary>Starts reading the current wallpaper right away (at sign-in as the shell, before the desktops need it).</summary>
    public static void Preload()
    {
        try { if (Current().File is { } file) LoadShared(file); }
        catch (Exception) { /* the desktop reads it later anyway */ }
    }

    public static System.Windows.Media.Imaging.BitmapSource? Load(string file)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // read now, don't keep the file open
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            ShellMode.Log($"Wallpaper: could not load '{file}': {ex.Message}");
            return null;
        }
    }

    public static void Apply()
    {
        try
        {
            string? setting;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                setting = key?.GetValue("Wallpaper") as string;

            // The setting, else the copy Windows keeps of the current wallpaper, else Windows' default picture.
            string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                             @"Microsoft\Windows\Themes\TranscodedWallpaper");
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows\img0.jpg");
            string? file = new[] { setting, transcoded, fallback }
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && File.Exists(Environment.ExpandEnvironmentVariables(f)));

            if (file == null)
            {
                ShellMode.Log($"Wallpaper: none found (setting: '{setting}')");
                return;
            }
            file = Environment.ExpandEnvironmentVariables(file);
            bool ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, file, SPIF_SENDCHANGE);
            ShellMode.Log($"Wallpaper: '{file}' (setting: '{setting}') -> {(ok ? "shown" : $"failed, error {Marshal.GetLastWin32Error()}")}");
        }
        catch (Exception ex) { ShellMode.Log($"Wallpaper: {ex.Message}"); }
    }
}
