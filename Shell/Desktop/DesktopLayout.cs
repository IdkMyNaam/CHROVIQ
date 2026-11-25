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

    // ---------- Look (Desktop settings) ----------

    /// <summary>Your own icon size in pixels (24–128); null = the View menu's small / medium / large.</summary>
    public int? CustomIconPixels { get; set; }

    /// <summary>Extra space between icons (pixels), across and down.</summary>
    public double SpacingX { get; set; }
    public double SpacingY { get; set; }

    public bool ShowLabels { get; set; } = true;
    /// <summary>Null: Segoe UI.</summary>
    public string? LabelFont { get; set; }
    public double LabelSize { get; set; } = 12;
    public string LabelColor { get; set; } = "#FFFFFF";
    /// <summary>A soft shadow under the names, so they're readable on any wallpaper.</summary>
    public bool LabelShadow { get; set; } = true;

    /// <summary>The monitor that shows the icons (its device name); null = the main monitor.</summary>
    public string? IconMonitor { get; set; }

    // ---------- Grid ----------

    /// <summary>Icon picture size in pixels.</summary>
    [JsonIgnore]
    public int IconPixels => CustomIconPixels is { } custom ? Math.Clamp(custom, 24, 128)
        : IconSize switch { DesktopIconSize.Small => 32, DesktopIconSize.Large => 96, _ => 48 };

    /// <summary>One grid cell (icon + label + spacing).</summary>
    [JsonIgnore]
    public (double Width, double Height) Cell
    {
        get
        {
            int icon = IconPixels;
            double label = ShowLabels ? Math.Clamp(LabelSize, 8, 24) * 2.8 + 14 : 12; // two lines of the name
            return (Math.Max(icon + 36, 76) + Math.Max(0, SpacingX), icon + label + Math.Max(0, SpacingY));
        }
    }

    /// <summary>Changes something that changes the grid, keeping every icon in its grid cell (column and row).</summary>
    public void ChangeKeepingCells(Action change)
    {
        var (oldWidth, oldHeight) = Cell;
        change();
        var (newWidth, newHeight) = Cell;
        if (oldWidth == newWidth && oldHeight == newHeight) return;
        foreach (var (path, position) in Positions.ToList())
            Positions[path] = new IconPosition(Math.Round(position.X / oldWidth) * newWidth, Math.Round(position.Y / oldHeight) * newHeight);
    }

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
