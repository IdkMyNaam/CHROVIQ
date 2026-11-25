namespace WindowsCustomizer;

public enum AltTabPosition { Center, Top }

public enum AltTabMonitor { UnderMouse, ActiveWindow, Main }

/// <summary>How the Alt+Tab switcher looks and behaves (saved in the settings, edited in "Alt+Tab settings...").</summary>
public sealed class AltTabLook
{
    // ---------- Colors and shape ----------

    /// <summary>The panel's background (#AARRGGBB; see-through works).</summary>
    public string Background { get; set; } = "#EE202020";
    public string Foreground { get; set; } = "#FFFFFF";
    /// <summary>The border around the selected window. Empty: Windows' accent color.</summary>
    public string? Selection { get; set; } = "#60CDFF";
    public double SelectionThickness { get; set; } = 2;
    public double CornerRadius { get; set; } = 10;
    /// <summary>A material (shader) drawn as the panel's background, behind the window previews; null for none.</summary>
    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }

    // ---------- Tiles ----------

    public double TileWidth { get; set; } = 236;
    public double PreviewHeight { get; set; } = 140;
    public bool ShowTitles { get; set; } = true;
    public bool ShowIcons { get; set; } = true;
    /// <summary>Null: Segoe UI.</summary>
    public string? FontFamily { get; set; }
    public double FontSize { get; set; } = 12;
    /// <summary>At most this many tiles per row (0 = as many as fit).</summary>
    public int MaxColumns { get; set; }

    // ---------- Where and what ----------

    public AltTabPosition Position { get; set; } = AltTabPosition.Center;
    public AltTabMonitor Monitor { get; set; } = AltTabMonitor.UnderMouse;
    /// <summary>Only the windows on the monitor the panel opens on.</summary>
    public bool OnlyThisMonitor { get; set; }
    public bool IncludeMinimized { get; set; } = true;
    /// <summary>How long Alt+Tab is held before the panel shows (a quick Alt+Tab switches without it).</summary>
    public double ShowDelayMs { get; set; } = 120;
}
