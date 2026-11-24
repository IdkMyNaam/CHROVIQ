namespace WindowsCustomizer;

/// <summary>The building blocks a taskbar design can contain.</summary>
public enum ModuleType
{
    /// <summary>Opens the Windows Customizer menu (profiles, taskbar options, exit).</summary>
    Menu,
    /// <summary>Opens the Windows Start menu.</summary>
    Start,
    /// <summary>One button per open window.</summary>
    WindowList,
    /// <summary>Buttons that launch the programs or shortcuts listed in <see cref="TaskbarModule.Apps"/>.</summary>
    PinnedApps,
    /// <summary>Time and date; formats in <see cref="TaskbarModule.TimeFormat"/> / <see cref="TaskbarModule.DateFormat"/>.</summary>
    Clock,
    /// <summary>Battery level (hidden on PCs without a battery).</summary>
    Battery,
    /// <summary>Opens Windows Quick Settings.</summary>
    QuickSettings,
    /// <summary>Empty space, <see cref="TaskbarModule.Width"/> wide.</summary>
    Spacer,
    /// <summary>A text label (<see cref="TaskbarModule.Text"/>).</summary>
    Text,
    /// <summary>A button that opens <see cref="TaskbarModule.Target"/> (program, file, folder or website).</summary>
    Button,
}

/// <summary>
/// One module in a taskbar zone. Only the options that belong to its <see cref="Type"/> are used;
/// the rest stay empty (and are left out of the JSON file).
/// </summary>
public class TaskbarModule
{
    public ModuleType Type { get; set; }

    /// <summary>Clock: .NET time format, for example "t" (short time, the default), "HH:mm" or "h:mm tt".</summary>
    public string? TimeFormat { get; set; }

    /// <summary>Clock: .NET date format, for example "d" (short date, the default) or "ddd d MMM". Empty hides the date.</summary>
    public string? DateFormat { get; set; }

    /// <summary>Spacer: width in pixels (at 100% scaling). Default 16.</summary>
    public double? Width { get; set; }

    /// <summary>Text: the label. Button: text shown next to (or instead of) the icon.</summary>
    public string? Text { get; set; }

    /// <summary>
    /// Button: the icon. Either a Segoe icon code like "E721" (search) or "E8B7" (folder),
    /// or a path to a .ico/.exe/.lnk file. Empty: use the target's own icon.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>Button: what to open. A program, file, folder or web address.</summary>
    public string? Target { get; set; }

    /// <summary>Button: command-line arguments for <see cref="Target"/>.</summary>
    public string? Arguments { get; set; }

    /// <summary>PinnedApps: paths to programs (.exe) or shortcuts (.lnk). Variables like %SystemRoot% work.</summary>
    public List<string>? Apps { get; set; }

    /// <summary>Button and PinnedApps: tooltip text. Default: the file or target name.</summary>
    public string? ToolTip { get; set; }
}

/// <summary>A whole taskbar layout: which modules go in the left, center and right zones.</summary>
public class TaskbarDesign
{
    public string Name { get; set; } = "New design";

    /// <summary>Modules aligned to the left edge of the bar.</summary>
    public List<TaskbarModule> Left { get; set; } = new();

    /// <summary>Modules centered on the bar (truly centered, whatever the left and right zones contain).</summary>
    public List<TaskbarModule> Center { get; set; } = new();

    /// <summary>Modules aligned to the right edge of the bar.</summary>
    public List<TaskbarModule> Right { get; set; } = new();

    /// <summary>Every module in all zones (not saved; the zones are).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<TaskbarModule> AllModules => Left.Concat(Center).Concat(Right);

    public override string ToString() => Name;

    // ---------- Built-in designs ----------

    /// <summary>The fixed bar from Phase 2: menu and windows on the left, status on the right.</summary>
    public static TaskbarDesign Classic() => new()
    {
        Name = "Classic",
        Left = { M(ModuleType.Menu), M(ModuleType.WindowList) },
        Right = { M(ModuleType.Battery), M(ModuleType.QuickSettings), M(ModuleType.Clock) },
    };

    /// <summary>Start button, pinned apps and windows in the middle.</summary>
    public static TaskbarDesign Centered() => new()
    {
        Name = "Centered",
        Left = { M(ModuleType.Menu) },
        Center =
        {
            M(ModuleType.Start),
            new TaskbarModule
            {
                Type = ModuleType.PinnedApps,
                Apps = new() { @"%SystemRoot%\explorer.exe", @"%SystemRoot%\System32\notepad.exe" },
            },
            M(ModuleType.WindowList),
        },
        Right = { M(ModuleType.Battery), M(ModuleType.QuickSettings), M(ModuleType.Clock) },
    };

    /// <summary>Just the essentials: menu, windows and a time-only clock.</summary>
    public static TaskbarDesign Minimal() => new()
    {
        Name = "Minimal",
        Left = { M(ModuleType.Menu), M(ModuleType.WindowList) },
        Right = { new TaskbarModule { Type = ModuleType.Clock, TimeFormat = "HH:mm", DateFormat = "" } },
    };

    public static List<TaskbarDesign> BuiltIn() => new() { Classic(), Centered(), Minimal() };

    private static TaskbarModule M(ModuleType type) => new() { Type = type };
}
