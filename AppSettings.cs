using System.IO;
using System.Text.Json;

namespace WindowsCustomizer;

/// <summary>App-wide settings, stored in %AppData%\WindowsCustomizer\settings.json.</summary>
public class AppSettings
{
    /// <summary>Process names (without .exe) that count as "a game is running".</summary>
    public List<string> GameProcesses { get; set; } = new();

    /// <summary>Pause Wallpaper Engine while one of <see cref="GameProcesses"/> runs.</summary>
    public bool PauseForGames { get; set; } = true;

    /// <summary>Path to wallpaper64.exe / wallpaper32.exe chosen by the user. Wins over auto-detection if it exists.</summary>
    public string? WallpaperEngineExe { get; set; }

    /// <summary>Name of the last applied profile, so the hotkey continues cycling from there after a restart.</summary>
    public string? LastAppliedProfile { get; set; }

    /// <summary>True once the "still running in the tray" message was shown.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Show the custom taskbar (Phase 2).</summary>
    public bool ShowTaskbar { get; set; }

    /// <summary>Hide the real Windows taskbar while the custom taskbar shows.</summary>
    public bool HideWindowsTaskbar { get; set; }

    /// <summary>
    /// The user's own taskbar auto-hide setting from before we hid the taskbar; null when not hiding.
    /// Kept on disk so it can still be put back correctly after a crash.
    /// </summary>
    public bool? TaskbarAutoHideBefore { get; set; }

    /// <summary>Which bar shows a window's button when the custom taskbar is on several monitors.</summary>
    public TaskbarButtons TaskbarButtons { get; set; } = TaskbarButtons.AllBars;

    /// <summary>Screen edge of the custom taskbar.</summary>
    public TaskbarEdge TaskbarEdge { get; set; } = TaskbarEdge.Top;

    /// <summary>%AppData%\WindowsCustomizer: where the app keeps all its files.</summary>
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsCustomizer");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, // "Top" instead of 0
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A broken settings file should not stop the app from starting; fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
