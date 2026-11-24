namespace WindowsCustomizer;

/// <summary>One saved look: what Windows should look like when this profile is applied.</summary>
public class Profile
{
    public string Name { get; set; } = "New profile";
    public string? WallpaperPath { get; set; }
    public bool DarkMode { get; set; }

    // The ListBox shows this text.
    public override string ToString() => Name;
}
