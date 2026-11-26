namespace WindowsCustomizer;

/// <summary>
/// One entry of the app menu. The same list is shown by the tray icon (WinForms menu)
/// and by the menu button on the custom taskbar (WPF menu), so they can't drift apart.
/// </summary>
public sealed record AppMenuItem(
    string Text,
    Action? OnClick = null,
    bool IsChecked = false,
    bool IsEnabled = true,
    string? Shortcut = null)
{
    /// <summary>The entry's text, in the chosen language.</summary>
    public string Text { get; init; } = Loc.T(Text);

    public static AppMenuItem Separator { get; } = new("-");

    public bool IsSeparator => Text == "-" && OnClick == null;
}

/// <summary>The app menu as a modern menu (the look and material from Menu settings).</summary>
internal static class AppMenuView
{
    public static System.Windows.Controls.ContextMenu Create(IEnumerable<AppMenuItem> entries)
    {
        var menu = ModernMenu.Create();
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) { ModernMenu.Separator(menu); continue; }
            var onClick = entry.OnClick;
            if (entry.IsChecked) ModernMenu.Check(menu, entry.Text, true, () => onClick?.Invoke()).IsEnabled = entry.IsEnabled;
            else ModernMenu.Item(menu, "", entry.Text, onClick, entry.Shortcut, entry.IsEnabled);
        }
        return menu;
    }
}
