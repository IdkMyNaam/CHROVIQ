namespace WindowsCustomizer;

/// <summary>"Quick Settings settings...": what the panel shows and how it looks. Saves after every change.</summary>
public sealed class QuickSettingsSettingsWindow : SettingsForm
{
    public QuickSettingsSettingsWindow(QuickSettingsLook look, Action save, Action tryIt)
        : base("Quick Settings settings", save, tryIt)
    {
        Header("What it shows");
        Check("Wi-Fi", look.ShowWifi, v => look.ShowWifi = v);
        Check("Bluetooth", look.ShowBluetooth, v => look.ShowBluetooth = v);
        Check("Night light", look.ShowNightLight, v => look.ShowNightLight = v);
        Check("Mute", look.ShowMute, v => look.ShowMute = v);
        Check("Brightness (monitors that support it)", look.ShowBrightness, v => look.ShowBrightness = v);
        Check("Volume", look.ShowVolume, v => look.ShowVolume = v);
        Check("Battery (on PCs with one)", look.ShowBattery, v => look.ShowBattery = v);
        Hint("Click a tile to switch it, right-click it for its Windows settings. Brightness works with monitors that allow it " +
             "over the cable (DDC/CI, often an option in the monitor's own menu); it isn't shown when none does.");

        Header("Colors and text");
        ColorField("Background", look.Background, v => look.Background = v, "#RRGGBB, or #AARRGGBB to make it see-through.");
        ColorField("Text", look.Foreground, v => look.Foreground = v);
        ColorField("Tiles that are on, and sliders", look.Accent ?? "", v => look.Accent = string.IsNullOrWhiteSpace(v) ? null : v, "Empty = the Windows accent color.");
        Font("Font", look.FontFamily, v => look.FontFamily = v);
        Number("Font size", look.FontSize, 8, 24, v => look.FontSize = v);

        Header("Shape");
        Number("Width (pixels)", look.Width, 280, 800, v => look.Width = v);
        Number("Corner radius", look.CornerRadius, 0, 40, v => look.CornerRadius = v);

        Header("Opening");
        AnimationFields(look.Opening ??= new PopupAnimation());

        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the tiles and sliders. The same materials as for the taskbar.",
            (look.LiveGlass, v => look.LiveGlass = v));
    }
}
