namespace WindowsCustomizer;

/// <summary>Look of Windows Customizer's Quick Settings panel. Saved in settings.json; every value has a default.</summary>
public sealed class QuickSettingsLook
{
    public double Width { get; set; } = 360;

    public string Background { get; set; } = "#F2202020";
    public string Foreground { get; set; } = "#FFFFFF";

    /// <summary>Color of tiles that are on, and of the sliders. Empty: the Windows accent color.</summary>
    public string? Accent { get; set; }

    public double CornerRadius { get; set; } = 10;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;

    // ---------- What it shows ----------

    public bool ShowWifi { get; set; } = true;
    public bool ShowBluetooth { get; set; } = true;
    public bool ShowNightLight { get; set; } = true;
    public bool ShowMute { get; set; } = true;
    public bool ShowBrightness { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowBattery { get; set; } = true;

    // ---------- Material and opening ----------

    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }

    /// <summary>Live glass: a glass material shows what's really behind it (this panel then doesn't show in screen capture).</summary>
    public bool LiveGlass { get; set; }

    public PopupAnimation Opening { get; set; } = PopupAnimation.Default(AnimationStyle.Slide, 200);
}
