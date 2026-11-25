namespace WindowsCustomizer;

/// <summary>The look of the desktop's right-click menus (the Windows 11-style ones). Saved after every change.</summary>
public sealed class MenuLook
{
    /// <summary>A material (shader) behind the menu items; null for the plain background.</summary>
    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }

    /// <summary>Hands the look to the menus (they read it each time they open).</summary>
    public void Apply()
    {
        ModernMenu.Material = Material;
        ModernMenu.MaterialValues = MaterialValues;
    }
}

/// <summary>"Menu settings...": a material for the desktop's right-click menus, with a sample menu as "Try it".</summary>
internal sealed class MenuSettingsWindow : SettingsForm
{
    public MenuSettingsWindow(MenuLook look, Action save)
        : base("Menu settings", () => { look.Apply(); save(); }, () => { look.Apply(); ShowSample(); })
    {
        Header("Desktop right-click menus");
        Hint("The menus you get by right-clicking the desktop or an icon (as your shell, and in the Desktop preview). " +
             "\"Show more options\" opens Windows' own menu, which keeps Windows' look.");
        MaterialFields(look.Material, name => look.Material = name, () => look.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the menu entries; the light or dark menu colors stay.");
    }

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
