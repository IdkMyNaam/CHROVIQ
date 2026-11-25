namespace WindowsCustomizer;

/// <summary>"Alt+Tab settings...": the switcher's look, tiles, where it opens and what it lists. Saved after every change.</summary>
internal sealed class AltTabSettingsWindow : SettingsForm
{
    public AltTabSettingsWindow(AltTabLook look, Action save)
        : base("Alt+Tab settings", save, () => AltTabSwitcher.Preview(look))
    {
        Header("Colors");
        ColorField("Background", look.Background, v => look.Background = v, "#AARRGGBB: lower AA makes it see-through.");
        ColorField("Text", look.Foreground, v => look.Foreground = v);
        ColorField("Selected window", look.Selection ?? "", v => look.Selection = string.IsNullOrWhiteSpace(v) ? null : v,
            "The border around the window you're about to switch to. Empty: Windows' accent color.");
        Slider("Selection border thickness", look.SelectionThickness, 0, 8, v => look.SelectionThickness = Math.Round(v));

        Header("Shape");
        Slider("Corner radius", look.CornerRadius, 0, 30, v => look.CornerRadius = Math.Round(v));

        Header("Tiles");
        Slider("Tile width (pixels)", look.TileWidth, 120, 480, v => look.TileWidth = Math.Round(v));
        Slider("Preview height (pixels)", look.PreviewHeight, 60, 360, v => look.PreviewHeight = Math.Round(v));
        Slider("At most per row (0 = as many as fit)", look.MaxColumns, 0, 12, v => look.MaxColumns = (int)Math.Round(v));
        Check("Show window titles", look.ShowTitles, v => look.ShowTitles = v);
        Check("Show app icons", look.ShowIcons, v => look.ShowIcons = v);
        Font("Title font", look.FontFamily ?? "Segoe UI", v => look.FontFamily = v == "Segoe UI" ? null : v);
        Slider("Title size", look.FontSize, 8, 24, v => look.FontSize = Math.Round(v));

        Header("Where it opens");
        Combo("Monitor", new[] { "The one the mouse is on", "The one with the active window", "Always the main monitor" },
            (int)look.Monitor, i => look.Monitor = (AltTabMonitor)i);
        Combo("Position", new[] { "Middle of the screen", "Near the top" }, (int)look.Position, i => look.Position = (AltTabPosition)i);

        Header("Which windows");
        Check("Only the windows on that monitor", look.OnlyThisMonitor, v => look.OnlyThisMonitor = v);
        Check("Include minimized windows", look.IncludeMinimized, v => look.IncludeMinimized = v);

        Header("Timing");
        Slider("Show the panel after (ms)", look.ShowDelayMs, 0, 500, v => look.ShowDelayMs = Math.Round(v / 10) * 10);
        Hint("A quick Alt+Tab (let go before this) switches to the previous window without showing the panel. 0 = always show it.");

        Header("Opening");
        AnimationFields(look.Opening ??= new PopupAnimation());

        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the window previews. The same materials as for the taskbar.",
            (look.LiveGlass, v => look.LiveGlass = v));
    }
}
