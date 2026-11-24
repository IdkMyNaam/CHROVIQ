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

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsCustomizer");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
