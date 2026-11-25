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

    /// <summary>Older setting: Windows key opens our search. Replaced by <see cref="WinKey"/>, still read for old files.</summary>
    public bool WinKeyOpensSearch { get; set; }

    /// <summary>Alt+Tab shows our switcher instead of Windows' own (always, when Windows Customizer is the shell).</summary>
    public bool OwnAltTab { get; set; }

    /// <summary>What the Windows key opens. Empty in older files: taken from <see cref="WinKeyOpensSearch"/>.</summary>
    public WinKeyAction? WinKey { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public WinKeyAction EffectiveWinKey => WinKey ?? (WinKeyOpensSearch ? WinKeyAction.Search : WinKeyAction.WindowsStart);

    /// <summary>Look of the start menu.</summary>
    public StartMenuLook StartMenu { get; set; } = new();

    /// <summary>Apps pinned to the start menu, in order.</summary>
    public List<StartPin> StartPinned { get; set; } = new();

    /// <summary>Look of the search window.</summary>
    public SearchLook Search { get; set; } = new();

    /// <summary>Tray icons kept on the bar (the rest go behind ^), by ManagedShell identifier. Empty: the system icons.</summary>
    public List<string>? TrayPinned { get; set; }

    /// <summary>Name of the taskbar design to use (a file in %AppData%\WindowsCustomizer\Taskbars).</summary>
    public string TaskbarDesign { get; set; } = TaskbarDesigns.DefaultName;

    /// <summary>
    /// A different design for other monitors: monitor device name (like \\.\DISPLAY2) to design name.
    /// Monitors not listed (and the main monitor) use <see cref="TaskbarDesign"/>.
    /// </summary>
    public Dictionary<string, string> MonitorDesigns { get; set; } = new();

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

    /// <summary>Set by <see cref="Load"/> when settings.json could not be read; shown in the status line.</summary>
    public static string? LoadWarning { get; private set; }

    public static AppSettings Load()
    {
        LoadWarning = null;
        if (!File.Exists(FilePath)) return new AppSettings();

        try
        {
            // Same options as Save, so values written as words ("Top", "AllBars") read back.
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Don't silently overwrite the user's file with defaults: keep it next to the new one.
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); }
            catch (IOException) { /* still start; the warning says what happened */ }
            LoadWarning = $"settings.json could not be read ({ex.Message}). It was kept as settings.json.bad; using default settings.";
        }
        catch (IOException ex)
        {
            LoadWarning = $"settings.json could not be read ({ex.Message}); using default settings for now.";
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
