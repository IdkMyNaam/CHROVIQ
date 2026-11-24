using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.IO;

namespace WindowsCustomizer;

/// <summary>Applies a Profile to Windows. Only touches the current user's settings (HKCU), so it is easy to undo.</summary>
public static class ThemeService
{
    private const int SPI_SETDESKWALLPAPER = 0x0014;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    public static async Task ApplyAsync(Profile profile)
    {
        SetDarkMode(profile.DarkMode);

        var value = profile.WallpaperValue?.Trim();
        if (string.IsNullOrEmpty(value)) return; // no wallpaper set: leave the current one

        switch (profile.Kind)
        {
            case WallpaperKind.Image:
                if (!File.Exists(value))
                    throw new FileNotFoundException($"Wallpaper image not found: {value}");

                // Wallpaper Engine draws over the normal wallpaper, so stop it first.
                if (WallpaperEngine.IsRunning)
                {
                    await WallpaperEngine.StopAsync();
                    await Task.Delay(700);
                }
                SetWallpaper(value);
                break;

            case WallpaperKind.WallpaperEngineFile:
                if (!File.Exists(value))
                    throw new FileNotFoundException($"Wallpaper Engine file not found: {value}");
                await WallpaperEngine.OpenWallpaperAsync(value);
                break;

            case WallpaperKind.WallpaperEngineProfile:
                await WallpaperEngine.OpenProfileAsync(value);
                break;
        }
    }

    public static void SetWallpaper(string path)
    {
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    public static void SetDarkMode(bool dark)
    {
        // 0 = dark, 1 = light
        int value = dark ? 0 : 1;

        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);

        // Tell running apps and Explorer that the theme changed.
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
            "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
    }
}
