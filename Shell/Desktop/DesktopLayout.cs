using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCustomizer;

public enum DesktopIconSize { Small, Medium, Large }

public enum DesktopSort { Name, Size, ItemType, DateModified }

/// <summary>Where an icon sits on the desktop, in device-independent pixels from the top-left of the icon area.</summary>
public sealed record IconPosition(double X, double Y);

/// <summary>
/// The desktop's "View" and "Sort by" choices and where every icon is, like Windows keeps them.
/// Saved in %AppData%\WindowsCustomizer\desktop.json (shared by all desktop windows).
/// </summary>
public sealed class DesktopLayout
{
    public DesktopIconSize IconSize { get; set; } = DesktopIconSize.Medium;

    /// <summary>Icons always packed in columns from the top-left, in the sort order (no free positions).</summary>
    public bool AutoArrange { get; set; }

    /// <summary>Icons snap to an invisible grid.</summary>
    public bool AlignToGrid { get; set; } = true;

    public bool ShowIcons { get; set; } = true;

    /// <summary>The last "Sort by" choice; also the order for auto-arrange and for icons without a position yet.</summary>
    public DesktopSort SortBy { get; set; } = DesktopSort.Name;

    /// <summary>Icon positions by full path (case-insensitive).</summary>
    public Dictionary<string, IconPosition> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---------- Grid ----------

    /// <summary>Icon picture size in pixels.</summary>
    [JsonIgnore]
    public int IconPixels => IconSize switch { DesktopIconSize.Small => 32, DesktopIconSize.Large => 96, _ => 48 };

    /// <summary>One grid cell (icon + label + spacing).</summary>
    [JsonIgnore]
    public (double Width, double Height) Cell => IconSize switch
    {
        DesktopIconSize.Small => (76, 72),
        DesktopIconSize.Large => (124, 146),
        _ => (84, 96),
    };

    // ---------- Load / save ----------

    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "desktop.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static DesktopLayout? _current;
    private static System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>The layout all desktop windows share; read from disk the first time.</summary>
    public static DesktopLayout Current => _current ??= Load();

    /// <summary>Raised when a View option changes, so every desktop window redraws.</summary>
    public static event Action? Changed;

    public static void NotifyChanged()
    {
        Changed?.Invoke();
        SaveSoon();
    }

    private static DesktopLayout Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var layout = JsonSerializer.Deserialize<DesktopLayout>(File.ReadAllText(FilePath), JsonOptions);
                if (layout != null)
                {
                    // The dictionary from JSON compares case-sensitively; paths on Windows don't.
                    layout.Positions = new Dictionary<string, IconPosition>(layout.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
                    return layout;
                }
            }
        }
        catch (Exception)
        {
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch (Exception) { /* keep going with defaults */ }
        }
        return new DesktopLayout();
    }

    /// <summary>Saves half a second after the last change (moving many icons gives one write).</summary>
    public static void SaveSoon()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception) { /* not saving the icon positions is not worth an error message */ }
    }
}
