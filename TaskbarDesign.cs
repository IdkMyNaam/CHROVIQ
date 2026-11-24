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
    /// <summary>Minimizes all windows (click again to bring them back).</summary>
    ShowDesktop,
    /// <summary>Opens Windows search (Win+S).</summary>
    WindowsSearch,
    /// <summary>A search box on the bar: finds installed apps, or searches the web (<see cref="TaskbarModule.SearchUrl"/>).</summary>
    SearchBox,
    /// <summary>Speaker icon: click to mute, mouse wheel to change the volume.</summary>
    Volume,
    /// <summary>Live CPU and memory use.</summary>
    SystemStats,
    /// <summary>What's playing (Spotify, browsers, ...) with previous / play-pause / next.</summary>
    Media,
    /// <summary>The notification-area (tray) icons of all programs.</summary>
    Tray,
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

    /// <summary>Spacer: width in pixels (at 100% scaling), default 16. SearchBox: box width, default 220. Media: maximum width, default 240.</summary>
    public double? Width { get; set; }

    /// <summary>SearchBox: web search address, with {0} where the search text goes. Default: Google.</summary>
    public string? SearchUrl { get; set; }

    /// <summary>Volume: also show the volume as a number.</summary>
    public bool? ShowValue { get; set; }

    /// <summary>Tray: show only the pinned icons on the bar (system icons at first; Ctrl+click to change), the rest behind ^.</summary>
    public bool? TrayOverflow { get; set; }

    /// <summary>Tray: icon size in pixels, default 16.</summary>
    public double? IconSize { get; set; }

    /// <summary>Tray: space on each side of an icon, default 5.</summary>
    public double? IconSpacing { get; set; }

    /// <summary>Tray: icons per row in the ^ flyout, default 5.</summary>
    public int? FlyoutColumns { get; set; }

    /// <summary>Tray: background of the ^ flyout ("#AARRGGBB"). Empty: dark gray.</summary>
    public string? FlyoutBackground { get; set; }

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

    /// <summary>
    /// PinnedApps, dock style: a dot shows which pinned apps are running, clicking a running one switches
    /// to it, and its windows don't get a second button in the window list. Empty counts as on.
    /// </summary>
    public bool? ShowRunning { get; set; }

    /// <summary>Short description for lists in the taskbar designer, like "Clock (HH:mm)".</summary>
    public override string ToString() => Type switch
    {
        ModuleType.Menu => "Menu button",
        ModuleType.Start => "Start button",
        ModuleType.WindowList => "Window list",
        ModuleType.PinnedApps => $"Pinned apps ({Apps?.Count ?? 0})",
        ModuleType.Clock => string.IsNullOrEmpty(TimeFormat) ? "Clock" : $"Clock ({TimeFormat})",
        ModuleType.Battery => "Battery",
        ModuleType.QuickSettings => "Quick Settings",
        ModuleType.Spacer => $"Spacer ({Width ?? 16:0} px)",
        ModuleType.Text => $"Text: {Text}",
        ModuleType.Button => $"Button: {(string.IsNullOrEmpty(Text) ? Target ?? "(no target)" : Text)}",
        ModuleType.ShowDesktop => "Show desktop",
        ModuleType.WindowsSearch => "Windows search",
        ModuleType.SearchBox => "Search box",
        ModuleType.Volume => "Volume",
        ModuleType.SystemStats => "CPU and memory",
        ModuleType.Media => "Media controls",
        ModuleType.Tray => TrayOverflow == true ? "Tray icons (with ^ flyout)" : "Tray icons",
        _ => Type.ToString(),
    };

    /// <summary>Default web search for the search box.</summary>
    public const string DefaultSearchUrl = "https://www.google.com/search?q={0}";

    /// <summary>A new module with sensible starting options for its type.</summary>
    public static TaskbarModule Create(ModuleType type) => type switch
    {
        ModuleType.PinnedApps => new() { Type = type, Apps = new() },
        ModuleType.Spacer => new() { Type = type, Width = 16 },
        ModuleType.Text => new() { Type = type, Text = "Text" },
        ModuleType.Button => new() { Type = type, Text = "Button", Icon = "E721" },
        ModuleType.SearchBox => new() { Type = type, Width = 220 },
        _ => new() { Type = type },
    };
}

/// <summary>How the bar looks. Every value has a default, so a design file only needs what it changes.</summary>
public class TaskbarLook
{
    /// <summary>Bar height in pixels at 100% scaling.</summary>
    public double Height { get; set; } = 40;

    /// <summary>Background color: "#RRGGBB", or "#AARRGGBB" with transparency (AA: 00 = invisible, FF = solid).</summary>
    public string Background { get; set; } = "#F01C1C1C";

    /// <summary>Text and icon color.</summary>
    public string Foreground { get; set; } = "#FFFFFF";

    /// <summary>Color of the line under the active window. Empty: the Windows accent color.</summary>
    public string? Accent { get; set; }

    public string FontFamily { get; set; } = "Segoe UI";

    public double FontSize { get; set; } = 12;

    /// <summary>Rounded corners of the bar itself (mostly for the floating dock).</summary>
    public double CornerRadius { get; set; }

    /// <summary>Rounded corners of buttons on the bar.</summary>
    public double ButtonCornerRadius { get; set; } = 4;

    /// <summary>Window buttons show only the icon (title on hover).</summary>
    public bool WindowIconsOnly { get; set; }

    /// <summary>Maximum width of a window button with a title.</summary>
    public double WindowButtonWidth { get; set; } = 200;

    /// <summary>One button per app (with a window count) instead of one per window.</summary>
    public bool GroupWindows { get; set; }

    /// <summary>Show a live preview of the window when the mouse rests on its button.</summary>
    public bool Thumbnails { get; set; } = true;

    /// <summary>Middle-click a window button to close that window.</summary>
    public bool MiddleClickCloses { get; set; } = true;

    /// <summary>Draw window and app icons in one color (<see cref="IconTint"/>); full color on hover.</summary>
    public bool TintIcons { get; set; }

    /// <summary>Color for tinted icons. Empty: the text color.</summary>
    public string? IconTint { get; set; }

    /// <summary>Hover and active highlights of window buttons take the main color of the app's icon.</summary>
    public bool IconGlow { get; set; }

    /// <summary>Buttons fade smoothly between colors on hover instead of switching instantly.</summary>
    public bool HoverFade { get; set; }

    /// <summary>
    /// Blur what is behind the bar (use a see-through Background, like "#801C1C1C").
    /// Experimental: uses an undocumented Windows feature; if it fails, the bar is drawn without blur.
    /// Not used with a floating dock yet (the dock is drawn with its Background color instead).
    /// </summary>
    public bool Blur { get; set; }

    /// <summary>Set to float the bar as a dock instead of filling the screen edge. Empty: normal full-width bar.</summary>
    public FloatingDock? Floating { get; set; }
}

/// <summary>A bar that floats away from the screen edge and is only as wide as its contents.</summary>
public class FloatingDock
{
    /// <summary>Distance from the screen edge, in pixels at 100% scaling.</summary>
    public double Margin { get; set; } = 8;

    /// <summary>
    /// Reserve screen space so maximized windows stay clear of the dock.
    /// False: the dock floats over windows (and doesn't take space).
    /// </summary>
    public bool ReserveSpace { get; set; } = true;
}

/// <summary>A whole taskbar layout: which modules go in the left, center and right zones.</summary>
public class TaskbarDesign
{
    public string Name { get; set; } = "New design";

    /// <summary>Size, colors, font and style. Missing in a file: the default look.</summary>
    public TaskbarLook Look { get; set; } = new();

    /// <summary>Screen edge for this design. Empty: the "Screen edge" setting in the main window.</summary>
    public TaskbarEdge? Edge { get; set; }

    /// <summary>
    /// Slide the bar away when not in use; it comes back when the mouse touches the screen edge.
    /// An auto-hiding bar never reserves screen space.
    /// </summary>
    public bool AutoHide { get; set; }

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

    /// <summary>A floating, rounded dock at the bottom: launcher icons and window icons, nothing else.</summary>
    public static TaskbarDesign Dock() => new()
    {
        Name = "Dock",
        Edge = TaskbarEdge.Bottom,
        Look = new TaskbarLook
        {
            Height = 52,
            Background = "#CC202020",
            CornerRadius = 14,
            ButtonCornerRadius = 8,
            WindowIconsOnly = true,
            GroupWindows = true,
            IconGlow = true,
            HoverFade = true,
            Floating = new FloatingDock { Margin = 8, ReserveSpace = true },
        },
        Center =
        {
            M(ModuleType.Menu),
            new TaskbarModule
            {
                Type = ModuleType.PinnedApps,
                Apps = new() { @"%SystemRoot%\explorer.exe", @"%SystemRoot%\System32\notepad.exe" },
            },
            new TaskbarModule { Type = ModuleType.Spacer, Width = 8 },
            M(ModuleType.WindowList),
            new TaskbarModule { Type = ModuleType.Spacer, Width = 8 },
            new TaskbarModule { Type = ModuleType.Clock, TimeFormat = "HH:mm", DateFormat = "" },
        },
    };

    public static List<TaskbarDesign> BuiltIn() => new() { Classic(), Centered(), Minimal(), Dock() };

    private static TaskbarModule M(ModuleType type) => new() { Type = type };
}
