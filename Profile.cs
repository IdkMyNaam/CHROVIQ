namespace WindowsCustomizer;

/// <summary>Where a profile's wallpaper comes from.</summary>
public enum WallpaperKind
{
    /// <summary>A normal image file, set through Windows.</summary>
    Image,
    /// <summary>A Wallpaper Engine wallpaper (path to its project.json, or a video file).</summary>
    WallpaperEngineFile,
    /// <summary>A profile saved inside Wallpaper Engine, by name.</summary>
    WallpaperEngineProfile,
}

/// <summary>One saved look: what Windows should look like when this profile is applied.</summary>
public class Profile
{
    public string Name { get; set; } = "New profile";
    public bool DarkMode { get; set; }

    public WallpaperKind Kind { get; set; } = WallpaperKind.Image;

    /// <summary>Image path, project.json path, or Wallpaper Engine profile name, depending on <see cref="Kind"/>.</summary>
    public string? WallpaperValue { get; set; }

    // The ListBox shows this text.
    public override string ToString() => Name;
}
