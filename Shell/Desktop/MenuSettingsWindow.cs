namespace WindowsCustomizer;

/// <summary>The look of the desktop's right-click menus (the Windows 11-style ones). Saved after every change.</summary>
public sealed class MenuLook
{
    /// <summary>A material (shader) behind the menu items; null for the plain background.</summary>
    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }

    // Colors, corners and font; null / empty = the light or dark default (like Windows' app mode).
    public string? Background { get; set; }
    public string? Foreground { get; set; }
    public string? Hover { get; set; }
    public string? Border { get; set; }
    public double CornerRadius { get; set; } = 8;
    public string? FontFamily { get; set; }
    public double FontSize { get; set; } = 13;

    /// <summary>Hands the look to the menus (they read it each time they open).</summary>
    public void Apply()
    {
        ModernMenu.Material = Material;
        ModernMenu.MaterialValues = MaterialValues;
        ModernMenu.Look = this;
    }
}

/// <summary>"Menu settings...": the look of CHROVIQ's menus (desktop, tray icon, taskbar menu button), with a sample as "Try it".</summary>
internal sealed class MenuSettingsWindow : SettingsForm
{
    public MenuSettingsWindow(MenuLook look, Action save)
        : base("Menu settings", () => { look.Apply(); save(); }, () => { look.Apply(); ShowSample(); })
    {
        Header("Which menus");
        Hint("CHROVIQ's own menus: the tray icon's menu, the taskbar's menu button, and right-clicking the desktop or an icon " +
             "(as your shell, and in the Desktop preview). \"Show more options\" opens Windows' own menu, which keeps Windows' look.");

        Header("Colors");
        Hint("Empty: the light or dark default, following Windows' app mode.");
        ColorField("Background", look.Background ?? "", v => look.Background = Empty(v), "#AARRGGBB: lower AA makes it see-through.");
        ColorField("Text and symbols", look.Foreground ?? "", v => look.Foreground = Empty(v));
        ColorField("Highlight (entry under the mouse)", look.Hover ?? "", v => look.Hover = Empty(v), "Tip: a faint color, like #20FFFFFF.");
        ColorField("Border", look.Border ?? "", v => look.Border = Empty(v));

        Header("Shape and text");
        Slider("Corner radius", look.CornerRadius, 0, 20, v => look.CornerRadius = Math.Round(v));
        Font("Font", look.FontFamily ?? "Segoe UI Variable Text", v => look.FontFamily = v == "Segoe UI Variable Text" ? null : v);
        Slider("Font size", look.FontSize, 9, 20, v => look.FontSize = Math.Round(v));
        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the menu entries; the light or dark menu colors stay.");
    }

    private static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A menu like the desktop's, at the mouse, to see the material.</summary>
    private static void ShowSample()
    {
        var menu = ModernMenu.Create();
        var view = ModernMenu.Submenu(menu, ModernMenu.View, "View");
        ModernMenu.Check(view, "Medium icons", true, () => { });
        ModernMenu.Check(view, "Auto arrange icons", false, () => { });
        ModernMenu.Item(menu, ModernMenu.Refresh, "Refresh", null);
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.Paste, "Paste", null, "Ctrl+V");
        ModernMenu.Item(menu, ModernMenu.New, "New", null);
        ModernMenu.Separator(menu);
        ModernMenu.Item(menu, ModernMenu.Display, "Display settings", null);
        ModernMenu.Item(menu, ModernMenu.Personalize, "Personalize", null);
        menu.IsOpen = true;
    }
}
