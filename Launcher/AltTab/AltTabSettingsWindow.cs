using System.Globalization;
using System.Windows.Controls;

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

        Header("Material (shader)");
        var materials = Materials.LoadAll();
        var names = new[] { "(none: plain background)" }.Concat(materials.Select(m => m.Name)).ToArray();
        var current = materials.FindIndex(m => m.Name.Equals(look.Material, StringComparison.CurrentCultureIgnoreCase));
        StackPanel? details = null; // made right after the dropdown; refilled when it changes
        Combo("Material", names, current + 1, index =>
        {
            look.Material = index <= 0 ? null : materials[index - 1].Name;
            if (details != null) Dispatcher.BeginInvoke(() => FillMaterial(details, look, index <= 0 ? null : materials[index - 1]));
        });
        details = Section();
        FillMaterial(details, look, current < 0 ? null : materials[current]);
        Hint("Drawn behind the window previews. The same materials as for the taskbar; your own go in %AppData%\\WindowsCustomizer\\Materials.");
    }

    /// <summary>The chosen material's description, compile error and settings.</summary>
    private void FillMaterial(StackPanel panel, AltTabLook look, Material? material)
    {
        panel.Children.Clear();
        if (material == null) return;
        Target = panel;
        try
        {
            Hint((material.Description ?? "") + (material.Animated ? " Animated." : ""));
            Materials.Compile(material, out var error);
            if (error != null) Hint("This material doesn't compile, so the plain background shows:\n" + error);
            var values = look.MaterialValues ??= new Dictionary<string, string>();
            foreach (var parameter in material.Parameters)
            {
                string value = values.TryGetValue(parameter.Name, out var v) ? v : parameter.Default;
                if (parameter.Type == MaterialParameterType.Color)
                    ColorField(parameter.Name, value, text => values[parameter.Name] = text, parameter.Hint);
                else
                {
                    double number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : parameter.Min;
                    Slider(parameter.Name, number, parameter.Min, parameter.Max,
                        n => values[parameter.Name] = n.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
        }
        finally { Target = null; }
    }
}
