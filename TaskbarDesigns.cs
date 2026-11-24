using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCustomizer;

/// <summary>
/// Stores taskbar designs as JSON files in %AppData%\WindowsCustomizer\Taskbars, one file per design.
/// The files can be edited by hand; the app reloads them when they change.
/// </summary>
public static class TaskbarDesigns
{
    public static readonly string Folder = Path.Combine(AppSettings.Folder, "Taskbars");

    /// <summary>Used when the chosen design doesn't exist (any more).</summary>
    public const string DefaultName = "Classic";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // keep files short: unused options left out
        Converters = { new JsonStringEnumConverter() },                // "Clock" instead of 4
        ReadCommentHandling = JsonCommentHandling.Skip,                 // allow // comments in hand-edited files
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads every design. A built-in design whose file is missing is written first, so it can be
    /// edited (delete its file to get the original back). Broken files are reported, never overwritten.
    /// </summary>
    public static (List<TaskbarDesign> designs, List<string> errors) LoadAll()
    {
        Directory.CreateDirectory(Folder);

        foreach (var builtIn in TaskbarDesign.BuiltIn())
        {
            var path = PathFor(builtIn.Name);
            if (!File.Exists(path))
                Save(builtIn);
        }

        var designs = new List<TaskbarDesign>();
        var errors = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var design = JsonSerializer.Deserialize<TaskbarDesign>(File.ReadAllText(file), JsonOptions)
                             ?? throw new JsonException("The file is empty.");
                if (string.IsNullOrWhiteSpace(design.Name))
                    design.Name = Path.GetFileNameWithoutExtension(file);

                // JSON "null" values would crash the bar later.
                design.Look ??= new();
                design.Left ??= new();
                design.Center ??= new();
                design.Right ??= new();
                foreach (var list in new[] { design.Left, design.Center, design.Right })
                    list.RemoveAll(m => m == null);

                if (designs.Any(d => d.Name.Equals(design.Name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    errors.Add($"{Path.GetFileName(file)}: another file already has the name \"{design.Name}\".");
                    continue;
                }
                designs.Add(design);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Built-ins first, in their usual order, then the user's own designs alphabetically.
        var builtInOrder = TaskbarDesign.BuiltIn().Select(d => d.Name).ToList();
        designs = designs
            .OrderBy(d => builtInOrder.IndexOf(d.Name) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (designs, errors);
    }

    public static void Save(TaskbarDesign design)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathFor(design.Name), JsonSerializer.Serialize(design, JsonOptions));
    }

    /// <summary>Removes a design's file. A built-in design comes back in its original form at the next load.</summary>
    public static void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Writes a design to any file (to share it). Same format as the files in the designs folder.</summary>
    public static void Export(TaskbarDesign design, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(design, JsonOptions));

    /// <summary>
    /// Reads a shared design file. Throws (JsonException, IOException) if it isn't a readable design.
    /// The caller picks a free name and saves it into the designs folder.
    /// </summary>
    public static TaskbarDesign Import(string path)
    {
        var design = JsonSerializer.Deserialize<TaskbarDesign>(File.ReadAllText(path), JsonOptions)
                     ?? throw new JsonException("The file is empty.");
        if (string.IsNullOrWhiteSpace(design.Name)) design.Name = Path.GetFileNameWithoutExtension(path);
        design.Look ??= new();
        design.Left ??= new();
        design.Center ??= new();
        design.Right ??= new();
        foreach (var list in new[] { design.Left, design.Center, design.Right })
            list.RemoveAll(m => m == null);
        return design;
    }

    /// <summary>A deep copy (through JSON), for "Duplicate".</summary>
    public static TaskbarDesign Clone(TaskbarDesign design) =>
        JsonSerializer.Deserialize<TaskbarDesign>(JsonSerializer.Serialize(design, JsonOptions), JsonOptions)!;

    public static bool IsBuiltIn(string name) =>
        TaskbarDesign.BuiltIn().Any(d => d.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>File path for a design name; characters not allowed in file names become "_".</summary>
    public static string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(Folder, (safe.Length == 0 ? "Design" : safe) + ".json");
    }
}
