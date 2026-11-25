namespace WindowsCustomizer;

/// <summary>Edits <see cref="SearchLook"/>: how the search window looks and behaves. Saves after every change.</summary>
public sealed class SearchSettingsWindow : SettingsForm
{
    public SearchSettingsWindow(SearchLook look, Action save)
        : base("Search settings", save, () => SearchWindow.Toggle(look))
    {
        Combo("Position (when opened by the Windows key)", new[] { "Center of the screen", "Top of the screen" },
            (int)look.Position, i => look.Position = (SearchPosition)i);
        Number("Width (pixels)", look.Width, 240, 1600, v => look.Width = v);
        ColorField("Background", look.Background, v => look.Background = v, "#RRGGBB, or #AARRGGBB to make it see-through.");
        ColorField("Text", look.Foreground, v => look.Foreground = v);
        ColorField("Selected result", look.Highlight ?? "", v => look.Highlight = string.IsNullOrWhiteSpace(v) ? null : v, "Empty = the Windows accent color.");
        Number("Corner radius", look.CornerRadius, 0, 40, v => look.CornerRadius = v);
        Font("Font", look.FontFamily, v => look.FontFamily = v);
        Number("Font size", look.FontSize, 8, 40, v => look.FontSize = v);
        Number("Number of app results", look.MaxResults, 1, 30, v => look.MaxResults = (int)v);
        Check("Offer \"Search the web for ...\"", look.ShowWebSearch, v => look.ShowWebSearch = v);
        Text("Web search address", look.SearchUrl ?? "", v => look.SearchUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            "{0} is replaced by what you typed. Empty = Google. Example: https://duckduckgo.com/?q={0}");
        Text("Placeholder text", look.Placeholder, v => look.Placeholder = v);

        Header("Results");
        Number("Result icon size (pixels)", look.ResultIconSize, 12, 64, v => look.ResultIconSize = v);
        Check("Show where each app is, under its name", look.ShowPaths, v => look.ShowPaths = v);
        Number("Highest the result list gets (pixels)", look.MaxResultsHeight, 100, 1400, v => look.MaxResultsHeight = v);
        Combo("Scroll bar", new[] { "Thin", "Hidden (scroll with the wheel)", "Windows" }, (int)look.Scrollbar, i => look.Scrollbar = (ScrollbarStyle)i);

        Header("Opening");
        AnimationFields(look.Opening ??= new PopupAnimation());

        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the search box and results. The same materials as for the taskbar.",
            (look.LiveGlass, v => look.LiveGlass = v));
    }
}

/// <summary>Edits <see cref="StartMenuLook"/>. Saves after every change.</summary>
public sealed class StartMenuSettingsWindow : SettingsForm
{
    public StartMenuSettingsWindow(StartMenuLook look, Action save, Action tryIt)
        : base("Start menu settings", save, tryIt)
    {
        Header("Size");
        Number("Width (pixels)", look.Width, 360, 1600, v => look.Width = v);
        Number("Height (pixels)", look.Height, 300, 1400, v => look.Height = v);
        Number("Pinned apps per row", look.Columns, 2, 12, v => look.Columns = (int)v);
        Number("Pinned icon size (pixels)", look.PinnedIconSize, 16, 96, v => look.PinnedIconSize = v);

        Header("Colors and text");
        ColorField("Background", look.Background, v => look.Background = v, "#RRGGBB, or #AARRGGBB to make it see-through.");
        ColorField("Text", look.Foreground, v => look.Foreground = v);
        ColorField("Highlight (selected app)", look.Highlight ?? "", v => look.Highlight = string.IsNullOrWhiteSpace(v) ? null : v, "Empty = the Windows accent color.");
        Number("Corner radius", look.CornerRadius, 0, 40, v => look.CornerRadius = v);
        Font("Font", look.FontFamily, v => look.FontFamily = v);
        Number("Font size", look.FontSize, 8, 30, v => look.FontSize = v);

        Header("Layout");
        Check("Show pinned apps", look.ShowPinned, v => look.ShowPinned = v);
        Check("Show all apps", look.ShowAllApps, v => look.ShowAllApps = v);
        Check("Search box at the bottom", look.SearchAtBottom, v => look.SearchAtBottom = v);
        Combo("Scroll bar", new[] { "Thin", "Hidden (scroll with the wheel)", "Windows" }, (int)look.Scrollbar, i => look.Scrollbar = (ScrollbarStyle)i);
        Hint("Pin apps by right-clicking them in the start menu. Web search and the number of search results come from Search settings.");

        Header("User");
        Check("Show your name and picture", look.ShowUser, v => look.ShowUser = v);
        Text("Name", look.UserName ?? "", v => look.UserName = string.IsNullOrWhiteSpace(v) ? null : v,
            "Empty = your Windows account's name.");
        Text("Picture (image file)", look.UserPicture ?? "", v => look.UserPicture = string.IsNullOrWhiteSpace(v) ? null : v.Trim().Trim('"'),
            "Empty = your Windows account picture. A full path, like C:\\Users\\You\\Pictures\\me.png.");

        Header("Opening");
        AnimationFields(look.Opening ??= new PopupAnimation());

        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the start menu's apps and buttons. The same materials as for the taskbar.",
            (look.LiveGlass, v => look.LiveGlass = v));
    }
}
