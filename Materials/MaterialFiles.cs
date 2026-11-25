using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Sharing materials: a material folder as a .zip (material.hlsl, material.json, a texture). Importing puts it in
/// your materials folder; a name that's already taken gets " (2)" and so on, so nothing is ever replaced.
/// </summary>
internal static class MaterialFiles
{
    /// <summary>Asks for a .zip and adds the material in it. Returns its name, or null (cancelled or failed; the reason is shown).</summary>
    public static string? Import(Window owner)
    {
        var dialog = new OpenFileDialog { Title = "Import a material", Filter = "Material (*.zip)|*.zip" };
        if (dialog.ShowDialog(owner) != true) return null;
        string temp = Path.Combine(Path.GetTempPath(), "WindowsCustomizer-material-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(dialog.FileName, temp);
            // material.json at the top of the .zip, or in its only folder.
            string? folder = File.Exists(Path.Combine(temp, "material.json")) ? temp
                : Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, "material.json")));
            if (folder == null || !File.Exists(Path.Combine(folder, "material.hlsl")))
                throw new InvalidDataException("There is no material.json and material.hlsl in this .zip.");

            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "material.json")))!.AsObject();
            string wanted = json["Name"]?.GetValue<string>() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(dialog.FileName);
            var taken = Materials.LoadAll().Select(m => m.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            string name = wanted;
            for (int i = 2; taken.Contains(name) || Directory.Exists(Path.Combine(Materials.UserFolder, Safe(name))); i++) name = $"{wanted} ({i})";
            json["Name"] = name;

            string target = Path.Combine(Materials.UserFolder, Safe(name));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(folder))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(target, "material.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Could not import the material:\n{ex.Message}", "Windows Customizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (Exception) { /* temp cleanup is best effort */ }
        }
    }

    /// <summary>Saves a material's folder as a .zip to share.</summary>
    public static void Export(Window owner, Material material)
    {
        var dialog = new SaveFileDialog { Title = "Export the material", Filter = "Material (*.zip)|*.zip", FileName = Safe(material.Name) + ".zip" };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName); // the dialog already asked about replacing it
            ZipFile.CreateFromDirectory(material.Folder, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Could not export the material:\n{ex.Message}", "Windows Customizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
