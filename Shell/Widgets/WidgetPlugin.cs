using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsCustomizer;

/// <summary>A file a widget asks to read; you choose which file (it can't read anything else).</summary>
public sealed class WidgetFileRequest
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>A command a widget wants to run, with its output passed to the widget. Only runs once you approve it.</summary>
public sealed class WidgetCommand
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>The command line, as cmd.exe runs it, in the widget's folder.</summary>
    public string Run { get; set; } = "";
    /// <summary>How often it runs again (2–3600 seconds); 0 = only when the widget asks.</summary>
    public double EverySeconds { get; set; } = 10;
}

/// <summary>widget.json: what a web widget is, what it shows and what it may use.</summary>
public sealed class WidgetManifest
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The page to show, inside the widget's folder.</summary>
    public string Entry { get; set; } = "index.html";
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 160;
    /// <summary>The widget's own background color by default (#AARRGGBB); empty: CHROVIQ's usual one.</summary>
    public string? Background { get; set; }
    /// <summary>Data from CHROVIQ: "stats" (CPU, memory, GPU), "media" (what's playing, with controls), "weather".</summary>
    public List<string> Data { get; set; } = new();
    /// <summary>Addresses it may connect to (http, https, ws, wss), like "http://localhost:4455". Nothing else.</summary>
    public List<string> Network { get; set; } = new();
    public List<WidgetFileRequest> Files { get; set; } = new();
    public List<WidgetCommand> Commands { get; set; } = new();
}

/// <summary>Where a widget comes from, shown before you give it anything (commands most of all).</summary>
public enum WidgetOrigin { BuiltIn, Imported, InYourFolder }

/// <summary>A web widget: its folder and its widget.json.</summary>
public sealed class WidgetPlugin
{
    public required string Key { get; init; }          // "builtin:<folder>" or "user:<folder>"
    public required string Folder { get; init; }
    public required WidgetManifest Manifest { get; init; }
    public WidgetOrigin Origin { get; init; }
    /// <summary>For an imported widget: the file it was imported from, and when.</summary>
    public string? ImportedFrom { get; init; }

    public string Name => string.IsNullOrWhiteSpace(Manifest.Name) ? Path.GetFileName(Folder) : Manifest.Name.Trim();

    /// <summary>The network addresses it may use, as origins ("http://localhost:4455"); anything that isn't one is dropped.</summary>
    public List<string> NetworkOrigins => Manifest.Network
        .Select(n => Uri.TryCreate(n?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ws" or "wss" && !string.IsNullOrEmpty(uri.Host)
            ? $"{uri.Scheme}://{uri.Authority}" : null)
        .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool Wants(string data) => Manifest.Data.Any(d => d.Equals(data, StringComparison.OrdinalIgnoreCase));

    /// <summary>A fingerprint of its commands: an approval is for exactly these commands, so any change needs approving again.</summary>
    public string CommandsFingerprint
    {
        get
        {
            var text = string.Join("\n", Manifest.Commands.Select(c => $"{c.Id}\u0001{c.Run}\u0001{c.EverySeconds}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key + "\n" + text)));
        }
    }

    /// <summary>Where it comes from, in words.</summary>
    public string OriginText => Origin switch
    {
        WidgetOrigin.BuiltIn => Loc.T("Comes with CHROVIQ."),
        WidgetOrigin.Imported => string.Format(Loc.T("Imported from {0}. Made by someone else: only give it more than it needs if you trust whoever made it."), ImportedFrom ?? "?"),
        _ => Loc.T("In your widgets folder (put there by hand, not imported through CHROVIQ)."),
    };
}

/// <summary>Finds, imports and exports web widgets.</summary>
internal static class WidgetPlugins
{
    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "Widgets");
    public static string UserFolder => Path.Combine(AppSettings.Folder, "Widgets");
    private const string OriginFile = ".chroviq-origin.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Every widget there is: the ones that come with CHROVIQ, then your own.</summary>
    public static List<WidgetPlugin> All()
    {
        var list = new List<WidgetPlugin>();
        foreach (var (root, prefix) in new[] { (BuiltInFolder, "builtin"), (UserFolder, "user") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var folder in Directory.GetDirectories(root).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                if (Load($"{prefix}:{Path.GetFileName(folder)}", folder, prefix == "builtin", out _) is { } plugin) list.Add(plugin);
        }
        return list;
    }

    public static WidgetPlugin? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        int colon = key.IndexOf(':');
        if (colon < 0) return null;
        string folderName = key[(colon + 1)..];
        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName is "." or "..") return null;
        bool builtIn = key.StartsWith("builtin:", StringComparison.Ordinal);
        string folder = Path.Combine(builtIn ? BuiltInFolder : UserFolder, folderName);
        return Load(key, folder, builtIn, out _);
    }

    private static WidgetPlugin? Load(string key, string folder, bool builtIn, out string? problem)
    {
        problem = null;
        string manifestFile = Path.Combine(folder, "widget.json");
        if (!File.Exists(manifestFile)) { problem = Loc.T("There's no widget.json in it."); return null; }
        try
        {
            var manifest = JsonSerializer.Deserialize<WidgetManifest>(File.ReadAllText(manifestFile), Json) ?? new WidgetManifest();
            manifest.Data ??= new(); manifest.Network ??= new(); manifest.Files ??= new(); manifest.Commands ??= new();
            WidgetOrigin origin = builtIn ? WidgetOrigin.BuiltIn : WidgetOrigin.InYourFolder;
            string? from = null;
            string originFile = Path.Combine(folder, OriginFile);
            if (!builtIn && File.Exists(originFile))
            {
                origin = WidgetOrigin.Imported;
                try { from = JsonDocument.Parse(File.ReadAllText(originFile)).RootElement.GetProperty("importedFrom").GetString(); }
                catch (Exception) { from = "?"; }
            }
            return new WidgetPlugin { Key = key, Folder = folder, Manifest = manifest, Origin = origin, ImportedFrom = from };
        }
        catch (Exception ex)
        {
            problem = string.Format(Loc.T("Its widget.json can't be read: {0}"), ex.Message);
            AppTrace.Log($"Widget {key}: {problem}");
            return null;
        }
    }

    /// <summary>
    /// Imports a widget from a .zip (widget.json at the top, or in one folder inside) into your widgets folder, marked as
    /// imported. Returns the widget, or throws with a reason.
    /// </summary>
    public static WidgetPlugin Import(string zipFile)
    {
        const long maxBytes = 50L * 1024 * 1024;
        using (var zip = ZipFile.OpenRead(zipFile))
            if (zip.Entries.Sum(e => e.Length) > maxBytes) throw new InvalidDataException(Loc.T("It's bigger than 50 MB unpacked."));

        string temp = Path.Combine(Path.GetTempPath(), "chroviq-widget-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipFile, temp); // refuses entries that would land outside the folder
            string root = File.Exists(Path.Combine(temp, "widget.json")) ? temp
                : Directory.GetDirectories(temp) is [var only] && File.Exists(Path.Combine(only, "widget.json")) ? only
                : throw new InvalidDataException(Loc.T("There's no widget.json in it."));
            var manifest = Load("check", root, false, out var problem)?.Manifest ?? throw new InvalidDataException(problem);

            string name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(zipFile) : manifest.Name.Trim();
            string folderName = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
            if (folderName.Length == 0) folderName = "Widget";
            string target = Path.Combine(UserFolder, folderName);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // the same widget again: replaced
            Directory.CreateDirectory(UserFolder);
            Directory.Move(root, target);
            File.WriteAllText(Path.Combine(target, OriginFile),
                JsonSerializer.Serialize(new { importedFrom = Path.GetFileName(zipFile), importedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") }));
            return Find("user:" + folderName) ?? throw new InvalidDataException(Loc.T("It couldn't be read after importing."));
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (Exception) { /* leftover temp files */ }
        }
    }

    /// <summary>Packs a widget's folder into a .zip to share (without CHROVIQ's own note of where it came from).</summary>
    public static void Export(WidgetPlugin plugin, string zipFile)
    {
        if (File.Exists(zipFile)) File.Delete(zipFile);
        ZipFile.CreateFromDirectory(plugin.Folder, zipFile);
        using var zip = ZipFile.Open(zipFile, ZipArchiveMode.Update);
        zip.GetEntry(OriginFile)?.Delete();
    }
}
