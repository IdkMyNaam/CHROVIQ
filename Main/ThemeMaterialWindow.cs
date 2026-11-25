namespace WindowsCustomizer;

/// <summary>The material of an app theme's "Material" background, with its settings. Every change shows at once.</summary>
internal sealed class ThemeMaterialWindow : SettingsForm
{
    public ThemeMaterialWindow(AppTheme theme, Action changed)
        : base($"Material for \"{theme.Name}\"", changed, changed)
    {
        Hint("Drawn behind the content of Windows Customizer's own windows (main window, designer, settings). " +
             "Lower the card transparency on the Appearance page to let more of it show through the cards.");
        MaterialFields(theme.Material, name => theme.Material = name, () => theme.MaterialValues ??= new Dictionary<string, string>(),
            "Animated materials redraw at the frame-rate limit (Taskbar page).");
    }
}
