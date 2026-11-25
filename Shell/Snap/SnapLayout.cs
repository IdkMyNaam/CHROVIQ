using System.Windows;

namespace WindowsCustomizer;

/// <summary>A column of a layout: its width (share of the screen) and how many zones it's split into from top to bottom.</summary>
public sealed class SnapColumn
{
    /// <summary>Relative width: columns 1, 2, 1 = 25 %, 50 %, 25 %.</summary>
    public double Width { get; set; } = 1;

    /// <summary>Zones stacked in this column (1 = the full height).</summary>
    public int Rows { get; set; } = 1;
}

/// <summary>
/// A snap layout: the screen split into columns, each split into rows. That covers halves, thirds, grids and
/// "a big main area with smaller ones beside it". Zones are numbered left to right, top to bottom.
/// </summary>
public sealed class SnapLayout
{
    public string Name { get; set; } = "New layout";
    public List<SnapColumn> Columns { get; set; } = new() { new(), new() };

    /// <summary>Built-in layouts can't be changed or removed (copy one to change it).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool BuiltIn { get; init; }

    public override string ToString() => Name;

    /// <summary>The zones as parts of the screen (0-1), left to right, top to bottom.</summary>
    public List<Rect> Zones()
    {
        var zones = new List<Rect>();
        var columns = Columns.Where(c => c.Width > 0).ToList();
        if (columns.Count == 0) return new List<Rect> { new(0, 0, 1, 1) };
        double total = columns.Sum(c => c.Width), x = 0;
        foreach (var column in columns)
        {
            double width = column.Width / total;
            int rows = Math.Clamp(column.Rows, 1, 6);
            for (int r = 0; r < rows; r++) zones.Add(new Rect(x, (double)r / rows, width, 1.0 / rows));
            x += width;
        }
        return zones;
    }

    public SnapLayout Copy(string name) => new()
    {
        Name = name,
        Columns = Columns.Select(c => new SnapColumn { Width = c.Width, Rows = c.Rows }).ToList(),
    };

    private static SnapLayout Make(string name, params (double width, int rows)[] columns) => new()
    {
        Name = name, BuiltIn = true,
        Columns = columns.Select(c => new SnapColumn { Width = c.width, Rows = c.rows }).ToList(),
    };

    public static IReadOnlyList<SnapLayout> BuiltIns { get; } = new[]
    {
        Make("Halves", (1, 1), (1, 1)),
        Make("Thirds", (1, 1), (1, 1), (1, 1)),
        Make("Main and side", (2, 1), (1, 1)),
        Make("Main and two", (2, 1), (1, 2)),
        Make("Grid", (1, 2), (1, 2)),
        Make("Center focus", (1, 1), (2, 1), (1, 1)),
        Make("Wide center, stacked sides", (1, 2), (2, 1), (1, 2)),
    };
}

/// <summary>Snap settings (saved in settings.json).</summary>
public sealed class SnapSettings
{
    public bool Enabled { get; set; }

    /// <summary>Drag a window to the top of a screen to get the layout picker.</summary>
    public bool PickerOnDragToTop { get; set; } = true;

    /// <summary>Hold Shift while dragging a window to see the zones of the screen's layout and drop it in one.</summary>
    public bool ZonesOnShiftDrag { get; set; } = true;

    /// <summary>Win+Alt+Left/Right moves the active window to the previous/next zone; Win+Alt+Z opens the picker for it.</summary>
    public bool Hotkeys { get; set; } = true;

    /// <summary>Space between zones and around the edge, in pixels.</summary>
    public double Gap { get; set; } = 8;

    /// <summary>Your own layouts (the built-in ones are always there too).</summary>
    public List<SnapLayout> Layouts { get; set; } = new();

    /// <summary>The layout used for Shift-drag and the hotkeys, per monitor device (like \\.\DISPLAY1). Missing: "Halves".</summary>
    public Dictionary<string, string> MonitorLayouts { get; set; } = new();

    public IEnumerable<SnapLayout> AllLayouts() => SnapLayout.BuiltIns.Concat(Layouts);

    public SnapLayout LayoutFor(string? monitorDevice) =>
        (monitorDevice != null && MonitorLayouts.TryGetValue(monitorDevice, out var name)
            ? AllLayouts().FirstOrDefault(l => l.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
            : null) ?? SnapLayout.BuiltIns[0];
}
