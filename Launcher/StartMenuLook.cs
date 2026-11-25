namespace WindowsCustomizer;

/// <summary>What the Windows key does when pressed on its own.</summary>
public enum WinKeyAction
{
    /// <summary>The normal Windows Start menu (the app doesn't touch the key).</summary>
    WindowsStart,
    /// <summary>Windows Customizer's search window.</summary>
    Search,
    /// <summary>Windows Customizer's start menu.</summary>
    StartMenu,
}

/// <summary>Look of the start menu. Saved in settings.json; every value has a default.</summary>
public class StartMenuLook
{
    /// <summary>Size in pixels at 100% scaling.</summary>
    public double Width { get; set; } = 620;
    public double Height { get; set; } = 680;

    /// <summary>Almost solid by default: text behind a see-through menu makes the list harder to read.</summary>
    public string Background { get; set; } = "#FC202020";
    public string Foreground { get; set; } = "#FFFFFF";

    /// <summary>Selected / hovered entries. Empty: the Windows accent color.</summary>
    public string? Highlight { get; set; }

    public double CornerRadius { get; set; } = 10;

    /// <summary>A material (shader) drawn as the background, behind the content; null for none.</summary>
    public string? Material { get; set; }

    /// <summary>The material's settings by name; missing ones use its defaults.</summary>
    public Dictionary<string, string>? MaterialValues { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;

    /// <summary>Pinned apps per row.</summary>
    public int Columns { get; set; } = 6;

    public bool ShowPinned { get; set; } = true;
    public bool ShowAllApps { get; set; } = true;
}

/// <summary>An app pinned to the start menu.</summary>
public class StartPin
{
    public string Name { get; set; } = "";

    /// <summary>Shortcut path, or the Apps-folder id for Store apps (see <see cref="IsShellApp"/>).</summary>
    public string LaunchPath { get; set; } = "";

    public bool IsShellApp { get; set; }

    public AppEntry ToEntry() => new(Name, LaunchPath, IsShellApp);

    public static StartPin From(AppEntry app) => new() { Name = app.Name, LaunchPath = app.LaunchPath, IsShellApp = app.IsShellApp };
}
