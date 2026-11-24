namespace WindowsCustomizer;

/// <summary>Where the search window opens when it isn't opened from a search box on the bar.</summary>
public enum SearchPosition
{
    /// <summary>In the middle of the screen (a bit above center).</summary>
    Center,
    /// <summary>At the top of the screen, like a command bar.</summary>
    Top,
}

/// <summary>
/// Look and behavior of the search window (opened by the Windows key, or by a search box on the bar).
/// Saved in settings.json; every value has a default.
/// </summary>
public class SearchLook
{
    /// <summary>Window width in pixels at 100% scaling.</summary>
    public double Width { get; set; } = 560;

    public SearchPosition Position { get; set; } = SearchPosition.Center;

    /// <summary>"#RRGGBB" or "#AARRGGBB".</summary>
    public string Background { get; set; } = "#F2202020";

    public string Foreground { get; set; } = "#FFFFFF";

    /// <summary>Color of the selected result. Empty: the Windows accent color.</summary>
    public string? Highlight { get; set; }

    public double CornerRadius { get; set; } = 10;

    public double FontSize { get; set; } = 15;

    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>How many apps to list (the web search entry comes on top of that).</summary>
    public int MaxResults { get; set; } = 8;

    /// <summary>Offer "Search the web for ..." as the last result.</summary>
    public bool ShowWebSearch { get; set; } = true;

    /// <summary>Web search address, {0} = the typed text. Empty: Google.</summary>
    public string? SearchUrl { get; set; }

    public string Placeholder { get; set; } = "Search apps and the web";
}
