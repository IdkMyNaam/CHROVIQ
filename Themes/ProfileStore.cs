using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCustomizer;

/// <summary>Loads and saves the profile list in %AppData%\WindowsCustomizer\profiles.json.</summary>
public static class ProfileStore
{
    public static readonly string FilePath = Path.Combine(AppSettings.Folder, "profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Write enums as words ("WallpaperEngineFile") so the file is readable and hand-editable.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The profiles a first-time user starts with.</summary>
    public static List<Profile> Defaults() => new()
    {
        new Profile { Name = "Dark", DarkMode = true },
        new Profile { Name = "Light", DarkMode = false },
    };

    /// <summary>
    /// Ready-made profiles to start from (Profiles page, "Add an example..."). They only use built-in things:
    /// Windows' own wallpapers (when this Windows has them) and the built-in taskbar designs.
    /// </summary>
    public static List<Profile> Examples()
    {
        string web = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows");
        string? Picture(string file) => File.Exists(Path.Combine(web, file)) ? Path.Combine(web, file) : null;
        return new()
        {
            // Dark with a red accent and a floating dock: the bar stays out of the way of the game.
            new Profile { Name = "Gaming", DarkMode = true, AccentColor = "#E81123", TaskbarDesign = "Dock", WallpaperValue = Picture("img19.jpg") },
            // Light and calm, the classic bar with everything in reach.
            new Profile { Name = "School", DarkMode = false, AccentColor = "#0078D4", TaskbarDesign = "Classic", WallpaperValue = Picture("img0.jpg") },
            // Dark with a warm accent and a minimal bar: less light and less to look at in the evening.
            new Profile { Name = "Night", DarkMode = true, AccentColor = "#F7630C", TaskbarDesign = "Minimal", WallpaperValue = Picture("img19.jpg") },
        };
    }

    /// <summary>
    /// Loads the saved profiles, or the defaults if there are none.
    /// If the file is broken it is renamed to profiles.json.bad (never overwritten) and
    /// <paramref name="warning"/> explains what happened.
    /// </summary>
    public static List<Profile> Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath)) return Defaults();

        try
        {
            var loaded = JsonSerializer.Deserialize<List<Profile?>>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new JsonException("The file is empty.");

            var profiles = loaded.OfType<Profile>().ToList();
            foreach (var p in profiles)
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Unnamed profile";

            return profiles.Count > 0 ? profiles : Defaults();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var badPath = FilePath + ".bad";
            File.Move(FilePath, badPath, overwrite: true);
            warning = $"profiles.json could not be read ({ex.Message}). It was renamed to profiles.json.bad; starting with the default profiles.";
            return Defaults();
        }
    }

    public static void Save(IEnumerable<Profile> profiles)
    {
        Directory.CreateDirectory(AppSettings.Folder);

        // Write to a temp file first, so a crash mid-write cannot leave a half-written profiles.json.
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(profiles, JsonOptions));
        File.Move(tempPath, FilePath, overwrite: true);
    }
}
