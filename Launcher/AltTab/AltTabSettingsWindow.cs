namespace WindowsCustomizer;

/// <summary>"Alt+Tab settings...": the switcher's colors, corners and material. Saved after every change.</summary>
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

        Header("Shape");
        Slider("Corner radius", look.CornerRadius, 0, 30, v => look.CornerRadius = Math.Round(v));

        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the window previews. The same materials as for the taskbar.");
    }
}
