namespace WindowsCustomizer;

public enum WidgetType { Clock, Stats, Weather, Media, Notes, Web }

/// <summary>
/// One desktop widget: what it shows, where it is and how it looks. Saved in settings.json (AppSettings.Widgets).
/// Only the options of its <see cref="Type"/> are used.
/// </summary>
public sealed class DesktopWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetType Type { get; set; }

    // ---------- Place and size (WPF units, like Window.Left/Top) ----------

    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 140;

    /// <summary>Can't be dragged or resized by accident.</summary>
    public bool Locked { get; set; }

    // ---------- Look ----------

    public string Background { get; set; } = "#99202020";
    public string Foreground { get; set; } = "#FFFFFF";
    /// <summary>Graphs, the play button. Empty: the Windows accent color.</summary>
    public string? Accent { get; set; }
    public double CornerRadius { get; set; } = 12;
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>The main text; the rest is sized from it.</summary>
    public double FontSize { get; set; } = 14;
    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }
    /// <summary>Live glass: a glass material shows what's really behind the widget (it's then left out of screen capture).</summary>
    public bool LiveGlass { get; set; }

    // ---------- Clock ----------

    public string TimeFormat { get; set; } = "HH:mm";
    /// <summary>Empty: no date.</summary>
    public string DateFormat { get; set; } = "dddd d MMMM";

    // ---------- Stats ----------

    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    /// <summary>A small graph of the last minute next to each number.</summary>
    public bool Graphs { get; set; } = true;

    // ---------- Weather ----------

    /// <summary>The place, as typed ("Amsterdam"). Looked up once; the result is kept below.</summary>
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>The place's full name as found ("Amsterdam, Netherlands").</summary>
    public string? Place { get; set; }
    public bool Fahrenheit { get; set; }
    /// <summary>Days ahead shown under today (0-6).</summary>
    public int Days { get; set; } = 3;

    // ---------- Notes ----------

    public string? Text { get; set; }

    // ---------- Web widgets (plugins) ----------

    /// <summary>Which web widget: "builtin:&lt;folder&gt;" or "user:&lt;folder&gt;" (see WidgetPlugins).</summary>
    public string? Plugin { get; set; }
    /// <summary>Its name when it was added (shown if its folder is ever gone).</summary>
    public string? PluginName { get; set; }
    /// <summary>The files you chose for it, by the widget's file id.</summary>
    public Dictionary<string, string>? PluginFiles { get; set; }
    /// <summary>The fingerprint of the commands you approved (WidgetPlugin.CommandsFingerprint); null = none approved.</summary>
    public string? ApprovedCommands { get; set; }
    /// <summary>F12 opens the browser's developer tools for it (for making widgets).</summary>
    public bool DeveloperTools { get; set; }

    public override string ToString() => Type switch
    {
        WidgetType.Clock => "Clock",
        WidgetType.Stats => "CPU and memory",
        WidgetType.Weather => string.IsNullOrWhiteSpace(City) ? "Weather" : $"Weather: {City}",
        WidgetType.Media => "Media",
        WidgetType.Notes => "Notes",
        WidgetType.Web => string.IsNullOrWhiteSpace(PluginName) ? "Widget" : PluginName,
        _ => Type.ToString(),
    };

    /// <summary>A new widget with a size that fits its type.</summary>
    public static DesktopWidget Create(WidgetType type) => type switch
    {
        WidgetType.Clock => new() { Type = type, Width = 300, Height = 130 },
        WidgetType.Stats => new() { Type = type, Width = 260, Height = 150 },
        WidgetType.Weather => new() { Type = type, Width = 280, Height = 200 },
        WidgetType.Media => new() { Type = type, Width = 320, Height = 110 },
        WidgetType.Notes => new() { Type = type, Width = 240, Height = 200, Background = "#CC3A3520", Text = "" },
        _ => new() { Type = type },
    };
}
