namespace WindowsCustomizer;

/// <summary>How the Alt+Tab switcher looks (saved in the settings, edited in "Alt+Tab settings...").</summary>
public sealed class AltTabLook
{
    /// <summary>The panel's background (#AARRGGBB; see-through works).</summary>
    public string Background { get; set; } = "#EE202020";
    public string Foreground { get; set; } = "#FFFFFF";
    /// <summary>The border around the selected window. Empty: Windows' accent color.</summary>
    public string? Selection { get; set; } = "#60CDFF";
    public double CornerRadius { get; set; } = 10;
    /// <summary>A material (shader) drawn as the panel's background, behind the window previews; null for none.</summary>
    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }
}
