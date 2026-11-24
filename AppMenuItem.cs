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
    public static AppMenuItem Separator { get; } = new("-");

    public bool IsSeparator => Text == "-" && OnClick == null;
}
