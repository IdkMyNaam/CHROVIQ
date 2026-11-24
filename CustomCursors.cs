using System.IO;
using System.Text.RegularExpressions;

namespace WindowsCustomizer;

/// <summary>One of the 17 cursors Windows lets you replace.</summary>
/// <param name="Name">Registry value name under HKCU\Control Panel\Cursors.</param>
/// <param name="Label">Name shown in Windows' Mouse settings.</param>
public record CursorRole(string Name, string Label);

/// <summary>
/// Per-profile custom cursor sets. Files are copied into
/// %AppData%\WindowsCustomizer\Cursors\&lt;profile id&gt;\ so the profile keeps working
/// even if the downloaded cursor pack is moved or deleted.
/// </summary>
public static class CustomCursors
{
    /// <summary>All roles, in the order Windows cursor schemes list them.</summary>
    public static readonly IReadOnlyList<CursorRole> Roles = new CursorRole[]
    {
        new("Arrow", "Normal select"),
        new("Help", "Help select"),
        new("AppStarting", "Working in background"),
        new("Wait", "Busy"),
        new("Crosshair", "Precision select"),
        new("IBeam", "Text select"),
        new("NWPen", "Handwriting"),
        new("No", "Unavailable"),
        new("SizeNS", "Vertical resize"),
        new("SizeWE", "Horizontal resize"),
        new("SizeNWSE", "Diagonal resize 1"),
        new("SizeNESW", "Diagonal resize 2"),
        new("SizeAll", "Move"),
        new("UpArrow", "Alternate select"),
        new("Hand", "Link select"),
        new("Pin", "Location select"),
        new("Person", "Person select"),
    };

    private static readonly string[] CursorExtensions = { ".cur", ".ani" };

    public static string FolderFor(Profile profile) =>
        Path.Combine(AppSettings.Folder, "Cursors", profile.Id);

    // ---------- Changing a profile's set ----------

    /// <summary>Copies <paramref name="sourceFile"/> in as the cursor for <paramref name="role"/>.</summary>
    public static void SetRole(Profile profile, string role, string sourceFile)
    {
        var folder = FolderFor(profile);
        Directory.CreateDirectory(folder);

        var ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        var target = Path.Combine(folder, role + ext);
        File.Copy(sourceFile, target, overwrite: true);

        // Remove the other extension's file for this role (Arrow.cur replaced by Arrow.ani).
        foreach (var other in CursorExtensions.Where(e => e != ext))
            File.Delete(Path.Combine(folder, role + other)); // no error if missing

        profile.CustomCursors[role] = target;
    }

    public static void ClearRole(Profile profile, string role)
    {
        if (profile.CustomCursors.Remove(role, out var file))
            File.Delete(file);
    }

    public static void ClearAll(Profile profile)
    {
        profile.CustomCursors.Clear();
        DeleteFolder(profile);
    }

    /// <summary>Removes the profile's copied cursor files (when the profile is deleted or cleared).</summary>
    public static void DeleteFolder(Profile profile)
    {
        var folder = FolderFor(profile);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// Replaces the profile's set with the cursors found in a cursor pack folder.
    /// Returns which roles were found and how ("install.inf" or "file names").
    /// </summary>
    public static (List<string> found, string method) ImportFolder(Profile profile, string packFolder)
    {
        var ownFolder = Path.GetFullPath(FolderFor(profile)).TrimEnd('\\') + "\\";
        if ((Path.GetFullPath(packFolder).TrimEnd('\\') + "\\").StartsWith(ownFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is this profile's own cursor folder. Choose the original cursor pack instead.");

        var (mapping, method) = FindInPack(packFolder);
        if (mapping.Count == 0)
            throw new FileNotFoundException("No .cur or .ani files for any cursor role were found in that folder.");

        ClearAll(profile);
        foreach (var (role, file) in mapping)
            SetRole(profile, role, file);

        return (Roles.Select(r => r.Name).Where(mapping.ContainsKey).ToList(), method);
    }

    // ---------- Finding cursors in a pack ----------

    /// <summary>Role to file. Uses the pack's install.inf when it has one, else guesses from file names.</summary>
    public static (Dictionary<string, string> mapping, string method) FindInPack(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => CursorExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories)
                     .OrderBy(f => Path.GetFileName(f).Equals("install.inf", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var fromInf = FromInf(inf, files);
            if (fromInf.Count > 0) return (fromInf, Path.GetFileName(inf));
        }

        return (FromFileNames(files), "file names");
    }

    /// <summary>
    /// Reads a cursor pack's install.inf. The scheme line looks like
    /// HKCU,"Control Panel\Cursors\Schemes","%SCHEME_NAME%",,"%10%\%CUR_DIR%\%pointer%,%10%\%CUR_DIR%\%help%,..."
    /// with the %names% defined in [Strings]. Entries are in the same order as <see cref="Roles"/>.
    /// </summary>
    private static Dictionary<string, string> FromInf(string infPath, List<string> files)
    {
        var result = new Dictionary<string, string>();
        string[] lines;
        try { lines = File.ReadAllLines(infPath); }
        catch (IOException) { return result; }

        // [Strings] section: name = "value"
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        string? schemeLine = null;
        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }

            if (string.Equals(section, "Strings", StringComparison.OrdinalIgnoreCase))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) strings[line[..eq].Trim()] = Unquote(line[(eq + 1)..].Trim());
            }
            else if (schemeLine == null && line.Contains(@"Cursors\Schemes", StringComparison.OrdinalIgnoreCase))
            {
                schemeLine = line;
            }
        }

        // Main path: the scheme line lists every role in order.
        if (schemeLine != null)
        {
            var entries = Expand(SplitInfLine(schemeLine).Last(), strings).Split(',');
            for (int i = 0; i < Roles.Count && i < entries.Length; i++)
            {
                if (FindFile(files, Path.GetFileName(entries[i].Trim().Trim('"'))) is { } file)
                    result[Roles[i].Name] = file;
            }
            if (result.Count > 0) return result;
        }

        // Fallback: an INF without a scheme line, but with the usual [Strings] names.
        foreach (var (key, value) in strings)
        {
            if (InfKeyRoles.TryGetValue(key, out var role) && FindFile(files, Path.GetFileName(value)) is { } file)
                result[role] = file;
        }
        return result;
    }

    /// <summary>The [Strings] names cursor packs commonly use. Note: "hand" means handwriting here, not the link hand.</summary>
    private static readonly Dictionary<string, string> InfKeyRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pointer"] = "Arrow", ["help"] = "Help", ["work"] = "AppStarting", ["working"] = "AppStarting",
        ["busy"] = "Wait", ["cross"] = "Crosshair", ["precision"] = "Crosshair", ["text"] = "IBeam",
        ["hand"] = "NWPen", ["handwriting"] = "NWPen", ["unavailable"] = "No", ["unavailiable"] = "No",
        ["vert"] = "SizeNS", ["horz"] = "SizeWE", ["dgn1"] = "SizeNWSE", ["dgn2"] = "SizeNESW",
        ["move"] = "SizeAll", ["alternate"] = "UpArrow", ["link"] = "Hand", ["pin"] = "Pin", ["person"] = "Person",
    };

    /// <summary>Words in file names that hint at a role. Longer matches win, so "handwriting" beats "hand".</summary>
    private static readonly (string role, string[] words)[] NameHints =
    {
        ("Arrow", new[] { "arrow", "normal", "pointer", "default", "normalselect" }),
        ("Help", new[] { "help", "helpselect", "question" }),
        ("AppStarting", new[] { "working", "appstarting", "background", "workinginbackground", "work" }),
        ("Wait", new[] { "busy", "wait", "loading" }),
        ("Crosshair", new[] { "cross", "crosshair", "precision", "precisionselect" }),
        ("IBeam", new[] { "text", "ibeam", "beam", "textselect" }),
        ("NWPen", new[] { "handwriting", "pen", "nwpen" }),
        ("No", new[] { "unavailable", "unavail", "notallowed", "forbidden", "no" }),
        ("SizeNS", new[] { "vert", "vertical", "sizens", "verticalresize", "ns" }),
        ("SizeWE", new[] { "horz", "horiz", "horizontal", "sizewe", "horizontalresize", "ew", "we" }),
        ("SizeNWSE", new[] { "dgn1", "diag1", "diagonal1", "nwse", "sizenwse", "diagonalresize1" }),
        ("SizeNESW", new[] { "dgn2", "diag2", "diagonal2", "nesw", "sizenesw", "diagonalresize2" }),
        ("SizeAll", new[] { "move", "sizeall" }),
        ("UpArrow", new[] { "alternate", "uparrow", "alternateselect", "up", "alt" }),
        ("Hand", new[] { "link", "linkselect", "hand" }),
        ("Pin", new[] { "pin", "location", "locationselect" }),
        ("Person", new[] { "person", "personselect" }),
    };

    /// <summary>Guesses roles from file names like "Busy.ani" or "07-Text Select.cur".</summary>
    private static Dictionary<string, string> FromFileNames(List<string> files)
    {
        var result = new Dictionary<string, string>();
        var bestScore = new Dictionary<string, int>();

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var squashed = Regex.Replace(name, "[^a-z0-9]", "");             // "07-Text Select" -> "07textselect"
            var tokens = Regex.Split(name, "[^a-z0-9]+").Where(t => t.Length > 0).ToHashSet();

            string? bestRole = null;
            int best = 0;
            foreach (var (role, words) in NameHints)
            {
                foreach (var word in words)
                {
                    // Short words ("no", "up", "ns") only count as a whole word, so "normal" isn't "no".
                    bool match = word.Length <= 3
                        ? tokens.Contains(word) || squashed == word
                        : squashed.Contains(word);
                    int score = squashed == word ? 1000 : word.Length; // exact name beats partial match
                    if (match && score > best) { best = score; bestRole = role; }
                }
            }

            if (bestRole != null && best > bestScore.GetValueOrDefault(bestRole))
            {
                bestScore[bestRole] = best;
                result[bestRole] = file;
            }
        }
        return result;
    }

    // ---------- Small INF helpers ----------

    private static string? FindFile(List<string> files, string fileName) =>
        fileName.Length == 0
            ? null
            : files.FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces %name% with its [Strings] value (a few rounds, since values can contain %names% too).</summary>
    private static string Expand(string text, Dictionary<string, string> strings)
    {
        for (int round = 0; round < 5 && text.Contains('%'); round++)
            text = Regex.Replace(text, "%([^%]+)%", m => strings.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        return text;
    }

    /// <summary>Splits an INF line on commas that are not inside quotes, and removes the quotes.</summary>
    private static List<string> SplitInfLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { parts.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        parts.Add(current.ToString().Trim());
        return parts;
    }

    /// <summary>Drops a ";" comment, unless the ";" is inside quotes.</summary>
    private static string StripComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
}
