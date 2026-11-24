using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>One wallpaper found on disk.</summary>
public record WallpaperItem(string Title, string ProjectJsonPath)
{
    public override string ToString() => Title;
}

/// <summary>
/// Controls Wallpaper Engine by running its exe with "-control" arguments.
/// See https://help.wallpaperengine.io/en/functionality/cli.html
/// </summary>
public static class WallpaperEngine
{
    /// <summary>Wallpaper Engine's Steam app id (also the Workshop content folder name).</summary>
    public const string SteamAppId = "431960";

    private static readonly string[] ProcessNames = { "wallpaper64", "wallpaper32" };

    /// <summary>Exe path from the settings. Used first if the file exists.</summary>
    public static string? SavedExePath { get; set; }

    // ---------- Finding the install ----------

    public static string? FindExe()
    {
        if (SavedExePath is { Length: > 0 } saved && File.Exists(saved))
            return saved;

        foreach (var dir in FindInstallDirs())
        {
            var exe64 = Path.Combine(dir, "wallpaper64.exe");
            var exe32 = Path.Combine(dir, "wallpaper32.exe");
            if (Environment.Is64BitOperatingSystem && File.Exists(exe64)) return exe64;
            if (File.Exists(exe32)) return exe32;
            if (File.Exists(exe64)) return exe64;
        }
        return null;
    }

    /// <summary>Folders that contain wallpaper64.exe / wallpaper32.exe.</summary>
    private static IEnumerable<string> FindInstallDirs()
    {
        var dirs = new List<string>();

        if (SavedExePath is { Length: > 0 } saved && File.Exists(saved))
            dirs.Add(Path.GetDirectoryName(saved)!);

        foreach (var library in FindSteamLibraries())
            dirs.Add(Path.Combine(library, "steamapps", "common", "wallpaper_engine"));

        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Steam's own folder plus every extra library listed in libraryfolders.vdf.</summary>
    private static List<string> FindSteamLibraries()
    {
        var roots = new List<string>();

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (key?.GetValue("SteamPath") is string steamPath)
                roots.Add(steamPath.Replace('/', '\\'));

        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
            if (key?.GetValue("InstallPath") is string installPath)
                roots.Add(installPath);

        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            try
            {
                // Lines look like:  "path"    "D:\\SteamLibrary"
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (IOException)
            {
                // Unreadable file: just use the libraries found so far.
            }
        }

        return libraries
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Lists wallpapers from the Steam Workshop and from Wallpaper Engine's own project folders.</summary>
    public static List<WallpaperItem> FindWallpapers()
    {
        var folders = new List<string>();

        foreach (var library in FindSteamLibraries())
        {
            var workshop = Path.Combine(library, "steamapps", "workshop", "content", SteamAppId);
            if (Directory.Exists(workshop))
                folders.AddRange(Directory.GetDirectories(workshop));
        }

        foreach (var install in FindInstallDirs())
        {
            foreach (var sub in new[] { "myprojects", "defaultprojects" })
            {
                var projects = Path.Combine(install, "projects", sub);
                if (Directory.Exists(projects))
                    folders.AddRange(Directory.GetDirectories(projects));
            }
        }

        var items = new List<WallpaperItem>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var projectJson = Path.Combine(folder, "project.json");
            if (File.Exists(projectJson))
                items.Add(new WallpaperItem(ReadTitle(projectJson) ?? Path.GetFileName(folder), projectJson));
        }

        return items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? ReadTitle(string projectJsonPath)
    {
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            };
            using var doc = JsonDocument.Parse(File.ReadAllText(projectJsonPath), options);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String)
            {
                var text = title.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }
        catch (Exception)
        {
            // Broken project.json: fall back to the folder name.
        }
        return null;
    }

    // ---------- Running / sending commands ----------

    public static bool IsRunning => ProcessNames.Any(IsProcessRunning);

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Starts Wallpaper Engine if it is not running, and waits until it has finished starting.</summary>
    public static async Task EnsureRunningAsync()
    {
        if (IsRunning) return;

        var exe = FindExe() ?? throw NotFound();
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        }) ?? throw new InvalidOperationException("Could not start Wallpaper Engine.");

        // Wait until its UI thread is idle (done starting up), at most 15 seconds.
        bool idle = await Task.Run(() =>
        {
            try { return process.WaitForInputIdle(15000); }
            catch (InvalidOperationException) { return false; } // exited, or has no UI thread
        });

        if (!IsRunning)
            throw new InvalidOperationException("Wallpaper Engine closed right after starting.");

        // Being idle does not guarantee it accepts -control commands yet, so give it a moment.
        await Task.Delay(idle ? 1500 : 4000);
    }

    public static async Task OpenWallpaperAsync(string projectJsonPath)
    {
        await EnsureRunningAsync();
        await SendAsync("openWallpaper", "-file", projectJsonPath);
    }

    public static async Task OpenProfileAsync(string profileName)
    {
        await EnsureRunningAsync();
        await SendAsync("openProfile", "-profile", profileName);
    }

    // Stop / pause / play do nothing when WE is not running; no point starting it just for that.
    public static Task StopAsync() => IsRunning ? SendAsync("stop") : Task.CompletedTask;
    public static Task PauseAsync() => IsRunning ? SendAsync("pause") : Task.CompletedTask;
    public static Task PlayAsync() => IsRunning ? SendAsync("play") : Task.CompletedTask;

    /// <summary>Runs "wallpaper64.exe -control &lt;command&gt; [args]". WE must already be running.</summary>
    private static async Task SendAsync(string command, params string[] args)
    {
        var exe = FindExe() ?? throw NotFound();

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-control");
        psi.ArgumentList.Add(command);
        foreach (var arg in args) psi.ArgumentList.Add(arg); // ArgumentList quotes paths with spaces.

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not run Wallpaper Engine command \"{command}\".");

        // The command hands off to the running instance and exits quickly.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Wallpaper Engine did not respond to \"{command}\" within 10 seconds.");
        }
    }

    private static Exception NotFound() =>
        new FileNotFoundException("Wallpaper Engine was not found. Use \"Set path...\" to choose wallpaper64.exe.");
}
