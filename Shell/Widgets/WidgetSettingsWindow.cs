namespace WindowsCustomizer;

/// <summary>A widget's settings: what it shows, its size and place, colors and text, and a material. Changes show right away.</summary>
internal sealed partial class WidgetSettingsWindow : SettingsForm
{
    public WidgetSettingsWindow(DesktopWidget w, Action save, Action tryIt)
        : base($"{w} widget", save, tryIt)
    {
        Header("What it shows");
        switch (w.Type)
        {
            case WidgetType.Clock:
                Text("Time format", w.TimeFormat, v => w.TimeFormat = v, "Examples: HH:mm, HH:mm:ss, h:mm tt");
                Text("Date format", w.DateFormat, v => w.DateFormat = v, "Empty = no date. Examples: dddd d MMMM, ddd d MMM yyyy, d-M-yyyy");
                break;
            case WidgetType.Stats:
                Check("CPU", w.ShowCpu, v => w.ShowCpu = v);
                Check("Memory", w.ShowMemory, v => w.ShowMemory = v);
                Check("GPU", w.ShowGpu, v => w.ShowGpu = v);
                Check("Graphs of the last minute", w.Graphs, v => w.Graphs = v);
                break;
            case WidgetType.Weather:
                Text("Place", w.City ?? "", v =>
                {
                    if (string.Equals(v.Trim(), w.City, StringComparison.CurrentCultureIgnoreCase)) return;
                    w.City = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    w.Latitude = w.Longitude = null; // looked up again
                    w.Place = null;
                }, "A city or town, like Amsterdam or New York. The weather comes from Open-Meteo (free, no account); only the place is sent.");
                Check("Fahrenheit (and mph)", w.Fahrenheit, v => w.Fahrenheit = v);
                Slider("Days ahead", w.Days, 0, 6, v => w.Days = (int)Math.Round(v));
                break;
            case WidgetType.Media:
                Hint("Shows what's playing in any app that tells Windows (Spotify, browsers, media players), with its cover and controls.");
                break;
            case WidgetType.Notes:
                Hint("Type right in the note on the desktop; it's saved by itself.");
                break;
            case WidgetType.Web:
                WebFields(w, save);
                break;
        }

        Header("Size and place");
        Number("Width (pixels)", w.Width, 80, 2000, v => w.Width = v);
        Number("Height (pixels)", w.Height, 50, 2000, v => w.Height = v);
        Check("Lock position and size", w.Locked, v => w.Locked = v);
        Hint("Unlocked: drag the widget to move it, and its bottom-right corner to resize it.");

        Header("Colors and text");
        ColorField("Background", w.Background, v => w.Background = v, "#AARRGGBB: lower AA makes it see-through (00 = only the text).");
        ColorField("Text", w.Foreground, v => w.Foreground = v);
        ColorField("Accent (graphs)", w.Accent ?? "", v => w.Accent = string.IsNullOrWhiteSpace(v) ? null : v, "Empty = the Windows accent color.");
        Font("Font", w.FontFamily, v => w.FontFamily = v);
        Number("Text size", w.FontSize, 8, 72, v => w.FontSize = v);
        Number("Corner radius", w.CornerRadius, 0, 100, v => w.CornerRadius = v);

        MaterialFields(w.Material, name => w.Material = name, () => w.MaterialValues ??= new Dictionary<string, string>(),
            "Drawn behind the widget. Glass materials show the wallpaper behind it.",
            (w.LiveGlass, v => w.LiveGlass = v));
    }
}
