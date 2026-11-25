using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace WindowsCustomizer;

/// <summary>
/// Update check against the project's GitHub Releases: the latest release's version, notes and setup file.
/// Only asks GitHub for that public release (no personal data is sent). Installing = downloading the setup and
/// running it (it closes this app, replaces the files and keeps your settings).
/// </summary>
internal static class Updates
{
    /// <summary>The GitHub repository ("owner/name") the releases come from. Empty: updates aren't set up yet.</summary>
    public const string Repository = "IdkMyNaam/CHROVIQ";

    public static bool IsSetUp => Repository.Length > 0;

    public static Version Current => typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public sealed record Release(Version Version, string Notes, string? SetupUrl, string PageUrl);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub asks every program for a name; a plain one, nothing about you.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsCustomizer-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The latest release, or null when there's none (or no internet: <paramref name="error"/> says why).</summary>
    public static async Task<(Release? Release, string? Error)> LatestAsync()
    {
        if (!IsSetUp) return (null, "Updates aren't set up yet.");
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return (null, $"The latest release has an unknown version ({tag}).");
            string? setup = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if ((name.StartsWith("CHROVIQ-Setup", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WindowsCustomizer-Setup", StringComparison.OrdinalIgnoreCase))
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    setup = asset.GetProperty("browser_download_url").GetString();
            }
            return (new Release(version, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", setup,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return (null, $"Couldn't check for updates ({ex.Message}).");
        }
    }

    /// <summary>True when <paramref name="release"/> is newer than this version.</summary>
    public static bool IsNewer(Release release) => release.Version > Current;

    /// <summary>Asks, downloads the setup and starts it (it closes the app itself). Returns a status line.</summary>
    public static async Task<string> OfferAsync(Window owner, Release release)
    {
        string notes = release.Notes.Length > 800 ? release.Notes[..800] + "..." : release.Notes;
        var answer = MessageBox.Show(owner,
            $"CHROVIQ {release.Version} is out (you have {Current.ToString(3)}).\n\n{notes}\n\nDownload and install it now? Your settings are kept.",
            "Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return "";
        if (release.SetupUrl == null)
        {
            ShellLaunch.Start(release.PageUrl, null);
            return "This release has no setup file; its page is open in your browser.";
        }
        try
        {
            string file = Path.Combine(Path.GetTempPath(), $"CHROVIQ-Setup-{release.Version}.exe");
            await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(release.SetupUrl));
            AppTrace.Log($"Update: starting {file}");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return "Installing the update...";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.ComponentModel.Win32Exception)
        {
            return $"The update couldn't be downloaded ({ex.Message}).";
        }
    }
}
