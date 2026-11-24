using System.Globalization;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Sets the Windows accent color by writing the same registry values the Settings app writes.
/// These values are not officially documented. Title bars and apps usually update right away;
/// the taskbar and Start may only update after Explorer restarts or the user signs out.
/// </summary>
public static class AccentService
{
    /// <summary>Parses "#RRGGBB" or "RRGGBB".</summary>
    public static bool TryParse(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        r = (byte)(rgb >> 16);
        g = (byte)(rgb >> 8);
        b = (byte)rgb;
        return true;
    }

    public static void Apply(string hexColor)
    {
        if (!TryParse(hexColor, out var r, out var g, out var b))
            throw new FormatException($"\"{hexColor}\" is not a color. Use the form #RRGGBB, for example #0078D4.");

        // Stop Windows from picking the accent from the wallpaper; it would override ours.
        using (var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
        {
            // Keep the existing value type (normally DWORD) so we don't change its format.
            var kind = desktop.GetValueNames().Contains("AutoColorization")
                ? desktop.GetValueKind("AutoColorization")
                : RegistryValueKind.DWord;
            desktop.SetValue("AutoColorization", kind == RegistryValueKind.DWord ? 0 : (object)"0", kind);
        }

        // Shades from light to dark. Index 3 is the accent itself; Windows uses the others for hover/pressed states.
        var palette = new[]
        {
            Mix(r, g, b, 255, 0.60), Mix(r, g, b, 255, 0.40), Mix(r, g, b, 255, 0.20),
            (r, g, b),
            Mix(r, g, b, 0, 0.20), Mix(r, g, b, 0, 0.40), Mix(r, g, b, 0, 0.60),
            Mix(r, g, b, 0, 0.80),
        };

        using (var accent = Registry.CurrentUser.CreateSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
        {
            var bytes = new byte[32];
            for (int i = 0; i < palette.Length; i++)
            {
                bytes[i * 4 + 0] = palette[i].r;
                bytes[i * 4 + 1] = palette[i].g;
                bytes[i * 4 + 2] = palette[i].b;
                bytes[i * 4 + 3] = 0x00;
            }
            accent.SetValue("AccentPalette", bytes, RegistryValueKind.Binary);
            accent.SetValue("AccentColorMenu", Abgr(palette[3]), RegistryValueKind.DWord);
            accent.SetValue("StartColorMenu", Abgr(palette[4]), RegistryValueKind.DWord);
        }

        using (var dwm = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM"))
        {
            dwm.SetValue("AccentColor", Abgr((r, g, b)), RegistryValueKind.DWord);

            // ColorizationColor is ARGB with a fixed alpha, as Windows writes it.
            int argb = unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | b));
            dwm.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
            dwm.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
        }

        ThemeService.BroadcastColorChange();
    }

    /// <summary>Registry DWORD in 0xAABBGGRR order with full alpha.</summary>
    private static int Abgr((byte r, byte g, byte b) c) =>
        unchecked((int)(0xFF000000u | ((uint)c.b << 16) | ((uint)c.g << 8) | c.r));

    /// <summary>Moves a color toward <paramref name="target"/> (0 = black, 255 = white) by <paramref name="amount"/>.</summary>
    private static (byte r, byte g, byte b) Mix(byte r, byte g, byte b, byte target, double amount)
    {
        byte Blend(byte v) => (byte)Math.Round(v + (target - v) * amount);
        return (Blend(r), Blend(g), Blend(b));
    }
}
