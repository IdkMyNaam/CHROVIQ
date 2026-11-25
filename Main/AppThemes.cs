using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WindowsCustomizer;

public enum AppThemeMode { System, Light, Dark }

public enum AppThemeBackground { Mica, Acrylic, Tabbed, Color, Image, Material }

/// <summary>
/// How Windows Customizer's own windows look (not the taskbar, desktop or start menu: those have their designs).
/// Saved as JSON in %AppData%\WindowsCustomizer\AppThemes, shareable like taskbar designs.
/// </summary>
public sealed class AppTheme
{
    public string Name { get; set; } = "New theme";
    public AppThemeMode Mode { get; set; } = AppThemeMode.System;
    /// <summary>#RRGGBB, or null for Windows' own accent color.</summary>
    public string? Accent { get; set; }
    public AppThemeBackground Background { get; set; } = AppThemeBackground.Mica;
    /// <summary>The background color (for "Color", and under the picture for "Image"). Null: the theme's own.</summary>
    public string? BackgroundColor { get; set; }
    public string? BackgroundImage { get; set; }
    /// <summary>How strongly the picture shows (0.05–1).</summary>
    public double ImageOpacity { get; set; } = 0.35;
    /// <summary>Corner radius of buttons, boxes and cards (0 = square, WPF-UI's own is 4).</summary>
    public double CornerRadius { get; set; } = 4;
    /// <summary>Null: Windows' font.</summary>
    public string? FontFamily { get; set; }
    /// <summary>How solid the cards are (0.2–1); lower lets the background through.</summary>
    public double CardOpacity { get; set; } = 1;

    /// <summary>For the "Material" background: a material (shader) behind the window's content, and its settings.</summary>
    public string? Material { get; set; }
    public Dictionary<string, string>? MaterialValues { get; set; }

    [JsonIgnore] public bool BuiltIn { get; init; }

    /// <summary>An editable copy (never built-in).</summary>
    public AppTheme Copy(string name) =>
        new AppTheme
        {
            Name = name, Mode = Mode, Accent = Accent, Background = Background, BackgroundColor = BackgroundColor,
            BackgroundImage = BackgroundImage, ImageOpacity = ImageOpacity, CornerRadius = CornerRadius,
            FontFamily = FontFamily, CardOpacity = CardOpacity, Material = Material,
            MaterialValues = MaterialValues == null ? null : new Dictionary<string, string>(MaterialValues),
        };
}

/// <summary>The app themes: built-in ones, the user's files, and applying one to every open settings window.</summary>
internal static class AppThemes
{
    public static readonly string Folder = Path.Combine(AppSettings.Folder, "AppThemes");
    public const string DefaultName = "Windows";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<AppTheme> BuiltIns { get; } = new[]
    {
        new AppTheme { Name = DefaultName, BuiltIn = true },
        new AppTheme { Name = "Dark glass", BuiltIn = true, Mode = AppThemeMode.Dark, Accent = "#4CC2FF", Background = AppThemeBackground.Acrylic, CornerRadius = 8, CardOpacity = 0.55 },
        new AppTheme { Name = "Midnight", BuiltIn = true, Mode = AppThemeMode.Dark, Accent = "#9B7BFF", Background = AppThemeBackground.Color, BackgroundColor = "#0E1016", CornerRadius = 6 },
        new AppTheme { Name = "Light clean", BuiltIn = true, Mode = AppThemeMode.Light, Accent = "#0067C0", Background = AppThemeBackground.Color, BackgroundColor = "#F7F7F7", CornerRadius = 6 },
        new AppTheme { Name = "High contrast", BuiltIn = true, Mode = AppThemeMode.Dark, Accent = "#FFD800", Background = AppThemeBackground.Color, BackgroundColor = "#000000", CornerRadius = 0 },
    };

    public static AppTheme Current { get; private set; } = BuiltIns[0];

    // ---------- Files ----------

    /// <summary>Built-in themes first, then the user's (a file can't replace a built-in name).</summary>
    public static List<AppTheme> LoadAll()
    {
        var all = BuiltIns.ToList();
        if (!Directory.Exists(Folder)) return all;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<AppTheme>(File.ReadAllText(file), JsonOptions) is { } theme &&
                    !all.Any(t => t.Name.Equals(theme.Name, StringComparison.CurrentCultureIgnoreCase)))
                    all.Add(theme);
            }
            catch (Exception) { /* a broken file is skipped */ }
        }
        return all;
    }

    public static AppTheme Find(string? name) =>
        LoadAll().FirstOrDefault(t => t.Name.Equals(name ?? DefaultName, StringComparison.CurrentCultureIgnoreCase)) ?? BuiltIns[0];

    private static string FileOf(string name) =>
        Path.Combine(Folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    public static void Save(AppTheme theme)
    {
        if (theme.BuiltIn) return;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileOf(theme.Name), JsonSerializer.Serialize(theme, JsonOptions));
    }

    public static void Delete(AppTheme theme)
    {
        if (!theme.BuiltIn && File.Exists(FileOf(theme.Name))) File.Delete(FileOf(theme.Name));
    }

    public static void Rename(AppTheme theme, string newName)
    {
        Delete(theme);
        theme.Name = newName;
        Save(theme);
    }

    public static void Export(AppTheme theme, string file) => File.WriteAllText(file, JsonSerializer.Serialize(theme, JsonOptions));

    /// <summary>Reads a shared theme file; renames it if the name is taken. Returns the added theme.</summary>
    public static AppTheme Import(string file)
    {
        var theme = JsonSerializer.Deserialize<AppTheme>(File.ReadAllText(file), JsonOptions)
                    ?? throw new InvalidDataException("This is not a theme file.");
        theme.Name = FreeName(string.IsNullOrWhiteSpace(theme.Name) ? "Imported theme" : theme.Name);
        Save(theme);
        return theme;
    }

    public static string FreeName(string wanted)
    {
        var taken = LoadAll().Select(t => t.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        string name = wanted;
        for (int n = 2; taken.Contains(name); n++) name = $"{wanted} ({n})";
        return name;
    }

    // ---------- Applying ----------

    private static readonly List<FluentWindow> Windows = new();

    /// <summary>Makes a settings window follow the app theme (now and when it changes).</summary>
    public static void Register(FluentWindow window)
    {
        if (Windows.Contains(window)) return;
        Windows.Add(window);
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            try { SystemThemeWatcher.UnWatch(window); } catch (Exception) { /* never watched */ }
        };
        ApplyTo(window);
        if (window.IsLoaded) Watch(window);
        else window.Loaded += (_, _) => Watch(window);
    }

    private static bool WindowsIsDark()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static ApplicationTheme BaseTheme(AppTheme theme) => theme.Mode switch
    {
        AppThemeMode.Light => ApplicationTheme.Light,
        AppThemeMode.Dark => ApplicationTheme.Dark,
        _ => WindowsIsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light,
    };

    private static WindowBackdropType Backdrop(AppTheme theme) => theme.Background switch
    {
        AppThemeBackground.Mica => WindowBackdropType.Mica,
        AppThemeBackground.Acrylic => WindowBackdropType.Acrylic,
        AppThemeBackground.Tabbed => WindowBackdropType.Tabbed,
        _ => WindowBackdropType.None, // our own color or picture
    };

    /// <summary>Switches every settings window to this theme (also used for the live preview while editing).</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var baseTheme = BaseTheme(theme);
        try
        {
            ApplicationThemeManager.Apply(baseTheme, Backdrop(theme), updateAccent: theme.Accent == null);
            if (theme.Accent != null && TryColor(theme.Accent) is { } accent)
                ApplicationAccentColorManager.Apply(accent, baseTheme, false, false);
            else
                ApplicationAccentColorManager.ApplySystemAccent();
        }
        catch (Exception ex) { AppTrace.Log($"App theme: {ex.Message}"); }
        foreach (var window in Windows.ToList()) { ApplyTo(window); Watch(window); }
    }

    /// <summary>Following Windows' light/dark only for "System" themes (otherwise the theme's own mode stays).</summary>
    private static void Watch(FluentWindow window)
    {
        // WPF-UI can only (un)watch a loaded window; Register watches again once it has loaded.
        if (!window.IsLoaded) return;
        try
        {
            SystemThemeWatcher.UnWatch(window);
            if (Current.Mode == AppThemeMode.System)
                SystemThemeWatcher.Watch(window, Backdrop(Current), updateAccents: Current.Accent == null);
        }
        catch (Exception ex) { AppTrace.Log($"App theme: watching Windows' light/dark: {ex.Message}"); }
    }

    private static void ApplyTo(FluentWindow window)
    {
        var theme = Current;
        window.WindowBackdropType = Backdrop(theme);

        // Background: Windows' material, or our color / picture.
        if (theme.Background is AppThemeBackground.Color or AppThemeBackground.Image or AppThemeBackground.Material)
            window.Background = BackgroundBrush(theme);
        else
            window.ClearValue(Window.BackgroundProperty);

        // Corners of controls, cards and popups.
        double radius = Math.Clamp(theme.CornerRadius, 0, 20);
        window.Resources["ControlCornerRadius"] = new CornerRadius(radius);
        window.Resources["OverlayCornerRadius"] = new CornerRadius(radius * 2);

        // How solid the cards are.
        foreach (var key in new[] { "CardBackgroundFillColorDefault", "CardBackgroundFillColorSecondary" })
        {
            if (theme.CardOpacity >= 0.999) { window.Resources.Remove(key + "Brush"); continue; }
            if (Application.Current.TryFindResource(key) is Color color)
            {
                var brush = new SolidColorBrush(Color.FromArgb((byte)(color.A < 40 ? 255 * theme.CardOpacity : color.A * theme.CardOpacity), color.R, color.G, color.B));
                brush.Freeze();
                window.Resources[key + "Brush"] = brush;
            }
        }

        if (string.IsNullOrWhiteSpace(theme.FontFamily)) window.ClearValue(Window.FontFamilyProperty);
        else window.FontFamily = new FontFamily(theme.FontFamily);

        // A material background: drawn on a layer behind the window's content.
        var layer = MaterialLayerOf(window);
        if (theme.Background == AppThemeBackground.Material && theme.Material != null)
        {
            layer.Background = BackgroundBrush(theme);
            var accent = TryColor(theme.Accent) ?? SystemParameters.WindowGlassColor;
            if (MaterialHost.Attach(layer, window, theme.Material, theme.MaterialValues, accent) is { } error)
                AppTrace.Log($"App theme material: {error}");
        }
        else
        {
            layer.Effect = null;
            layer.Background = null;
        }
    }

    /// <summary>
    /// The window's material layer: its content is put in a grid once, with an (empty, see-through) border behind
    /// it. The content itself is unchanged (names, title bar and all).
    /// </summary>
    private static System.Windows.Controls.Border MaterialLayerOf(FluentWindow window)
    {
        if (window.Content is System.Windows.Controls.Grid { Name: "ThemeLayers" } grid && grid.Children[0] is System.Windows.Controls.Border existing)
            return existing;
        var content = window.Content as UIElement;
        window.Content = null;
        var layers = new System.Windows.Controls.Grid { Name = "ThemeLayers" };
        var layer = new System.Windows.Controls.Border { IsHitTestVisible = false };
        layers.Children.Add(layer);
        if (content != null) layers.Children.Add(content);
        window.Content = layers;
        return layer;
    }

    private static Brush BackgroundBrush(AppTheme theme)
    {
        var baseColor = TryColor(theme.BackgroundColor) ??
                        (BaseTheme(theme) == ApplicationTheme.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        if (theme.Background != AppThemeBackground.Image || string.IsNullOrWhiteSpace(theme.BackgroundImage) || !File.Exists(theme.BackgroundImage))
            return Frozen(new SolidColorBrush(baseColor));

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(theme.BackgroundImage);
            image.DecodePixelWidth = 1920; // enough for a window; keeps memory low
            image.EndInit();
            image.Freeze();
            // The color first, the picture over it at the chosen strength.
            var drawing = new DrawingGroup();
            var area = new Rect(0, 0, image.Width, image.Height);
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(area)));
            drawing.Children.Add(new DrawingGroup { Opacity = Math.Clamp(theme.ImageOpacity, 0.05, 1), Children = { new ImageDrawing(image, area) } });
            return Frozen(new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill });
        }
        catch (Exception) { return Frozen(new SolidColorBrush(baseColor)); }
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    public static Color? TryColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return (Color)ColorConverter.ConvertFromString(text.Trim()); }
        catch (FormatException) { return null; }
    }
}
