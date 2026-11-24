using System.ComponentModel;

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
public class Profile : INotifyPropertyChanged
{
    private string _name = "New profile";

    /// <summary>Raises PropertyChanged so the profile list updates while the name is typed.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public bool DarkMode { get; set; }

    public WallpaperKind Kind { get; set; } = WallpaperKind.Image;

    /// <summary>Image path, project.json path, or Wallpaper Engine profile name, depending on <see cref="Kind"/>.</summary>
    public string? WallpaperValue { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}
