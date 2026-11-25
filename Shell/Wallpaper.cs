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
