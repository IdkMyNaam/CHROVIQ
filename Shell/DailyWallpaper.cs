using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WindowsCustomizer;

public enum DailyWallpaperSource
{
    /// <summary>Bing's picture of the day (downloaded once a day from bing.com).</summary>
    Bing,
    /// <summary>The Windows Spotlight photos Windows already keeps on this PC (no download).</summary>
    Spotlight,
    /// <summary>A picture from a folder you choose.</summary>
    Folder,
}

/// <summary>Daily wallpaper settings (saved in settings.json).</summary>
public sealed class DailyWallpaperSettings
{
    public bool Enabled { get; set; }
    public DailyWallpaperSource Source { get; set; } = DailyWallpaperSource.Bing;
    public string? Folder { get; set; }
    /// <summary>When the wallpaper was last changed (the next change is on a later day).</summary>
    public DateTime? LastChanged { get; set; }
    /// <summary>The picture in use now, and what it shows (Bing gives a title and copyright).</summary>
    public string? CurrentFile { get; set; }
    public string? CurrentTitle { get; set; }
}

/// <summary>
/// A new wallpaper every day: Bing's picture of the day, a Windows Spotlight photo, or a picture from your folder.
/// Checks at start, every hour and when the PC wakes up; changes once per day. Pictures are kept in
/// %AppData%\WindowsCustomizer\Wallpapers (the last 10). Skips changing while Wallpaper Engine shows a wallpaper
/// (it would cover it anyway).
/// </summary>
internal sealed class DailyWallpaper
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public static string Folder => Path.Combine(AppSettings.Folder, "Wallpapers");

    private readonly DailyWallpaperSettings _settings;
    private readonly Action _save;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private bool _busy;

    /// <summary>A line for the status bar and the card (what's showing, or why nothing changed).</summary>
    public event Action<string>? Status;

    public DailyWallpaper(DailyWallpaperSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        _timer.Tick += async (_, _) => await CheckAsync();
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) _timer.Dispatcher.InvokeAsync(async () => await CheckAsync());
        };
    }

    /// <summary>Starts or stops to match the setting, and changes the wallpaper now if today's isn't set yet.</summary>
    public async Task UpdateAsync()
    {
        if (!_settings.Enabled) { _timer.Stop(); return; }
        _timer.Start();
        await CheckAsync();
    }

    public void Stop() => _timer.Stop();

    private async Task CheckAsync()
    {
        if (!_settings.Enabled || _settings.LastChanged?.Date == DateTime.Today) return;
        await NextAsync();
    }

    /// <summary>A new picture now (also from "Next picture"). Returns a status line.</summary>
    public async Task<string> NextAsync()
    {
        if (_busy) return "Already looking for a picture...";
        _busy = true;
        string status;
        try
        {
            if (WallpaperEngine.IsRunning)
            {
                status = "Daily wallpaper: Wallpaper Engine is showing a wallpaper, so the daily one wasn't set (it would be hidden behind it).";
            }
            else
            {
                Directory.CreateDirectory(Folder);
                var (file, title) = _settings.Source switch
                {
                    DailyWallpaperSource.Bing => await BingAsync(),
                    DailyWallpaperSource.Spotlight => Spotlight(),
                    _ => FromFolder(),
                };
                if (file == null)
                {
                    status = $"Daily wallpaper: {title}";
                }
                else
                {
                    ThemeService.SetWallpaper(file);
                    _settings.LastChanged = DateTime.Now;
                    _settings.CurrentFile = file;
                    _settings.CurrentTitle = title;
                    _save();
                    CleanUp();
                    status = "Today's wallpaper: " + (title ?? Path.GetFileNameWithoutExtension(file));
                    AppTrace.Log($"Daily wallpaper: {file}");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            status = $"Daily wallpaper: couldn't get a picture ({ex.Message}). Trying again in an hour.";
            AppTrace.Log(status);
        }
        finally { _busy = false; }
        Status?.Invoke(status);
        return status;
    }

    // ---------- Sources ----------

    /// <summary>Bing's picture of the day, in the highest size Bing has, for this PC's language and region.</summary>
    private static async Task<(string? File, string? Title)> BingAsync()
    {
        string market = CultureInfo.CurrentUICulture.Name is { Length: 5 } m ? m : "en-US";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt={market}"));
        var image = doc.RootElement.GetProperty("images")[0];
        string date = image.GetProperty("startdate").GetString() ?? DateTime.Today.ToString("yyyyMMdd");
        string title = image.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } tt ? tt
                     : image.GetProperty("copyright").GetString() ?? "Bing";
        if (image.TryGetProperty("copyright", out var c) && c.GetString() is { Length: > 0 } copyright && !copyright.StartsWith(title))
            title += " · " + copyright;

        string file = Path.Combine(Folder, $"bing-{date}.jpg");
        if (!File.Exists(file))
        {
            string urlBase = image.GetProperty("urlbase").GetString() ?? throw new JsonException("No picture address.");
            byte[] bytes;
            try { bytes = await Http.GetByteArrayAsync($"https://www.bing.com{urlBase}_UHD.jpg"); }
            catch (HttpRequestException) { bytes = await Http.GetByteArrayAsync("https://www.bing.com" + image.GetProperty("url").GetString()); }
            await File.WriteAllBytesAsync(file + ".tmp", bytes);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        return (file, title);
    }

    /// <summary>A Windows Spotlight photo that's on this PC already (landscape, big enough), one not used recently.</summary>
    private (string? File, string? Title) Spotlight()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var places = new[]
        {
            Path.Combine(local, @"Packages\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\LocalCache\Microsoft\IrisService"),
            Path.Combine(local, @"Packages\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\LocalState\Assets"),
        };
        var pictures = places.Where(Directory.Exists)
            .SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
            .Where(f => new FileInfo(f).Length > 150_000 && IsLandscape(f))
            .ToList();
        if (pictures.Count == 0) return (null, "Windows hasn't downloaded any Spotlight photos on this PC (turn on Spotlight for the lock screen or desktop in Windows' Personalization settings).");

        // Copied with a name from its content, so a photo already used once is recognized.
        var fresh = pictures.Select(f => (Source: f, Copy: Path.Combine(Folder, $"spotlight-{Hash(f)}.jpg")))
                            .Where(p => !File.Exists(p.Copy)).ToList();
        var pick = fresh.Count > 0 ? fresh[Random.Shared.Next(fresh.Count)]
                 : pictures.Select(f => (Source: f, Copy: Path.Combine(Folder, $"spotlight-{Hash(f)}.jpg")))
                           .Where(p => p.Copy != _settings.CurrentFile).DefaultIfEmpty().ToList()[0];
        if (pick.Source == null) return (null, "There's only one Spotlight photo, and it's showing already.");
        File.Copy(pick.Source, pick.Copy, overwrite: true);
        File.SetLastWriteTime(pick.Copy, DateTime.Now); // "recently used" for the clean-up
        return (pick.Copy, "Windows Spotlight");
    }

    /// <summary>A random picture from your folder (not the one showing now).</summary>
    private (string? File, string? Title) FromFolder()
    {
        if (string.IsNullOrWhiteSpace(_settings.Folder) || !Directory.Exists(_settings.Folder))
            return (null, "choose a folder with pictures first.");
        var pictures = Directory.EnumerateFiles(_settings.Folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp")
            .Where(f => !string.Equals(f, _settings.CurrentFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pictures.Count == 0) return (null, "no other pictures (.jpg, .png, .bmp) in that folder.");
        var file = pictures[Random.Shared.Next(pictures.Count)];
        return (file, Path.GetFileNameWithoutExtension(file));
    }

    private static bool IsLandscape(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth >= 1280 && frame.PixelWidth > frame.PixelHeight;
        }
        catch (Exception) { return false; } // not a picture
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream))[..12].ToLowerInvariant();
    }

    /// <summary>Keeps the 10 newest downloaded/copied pictures (never the one showing).</summary>
    private void CleanUp()
    {
        try
        {
            foreach (var old in new DirectoryInfo(Folder).GetFiles("*.jpg").OrderByDescending(f => f.LastWriteTime).Skip(10))
                if (!string.Equals(old.FullName, _settings.CurrentFile, StringComparison.OrdinalIgnoreCase)) old.Delete();
        }
        catch (IOException) { /* in use: next time */ }
    }
}
