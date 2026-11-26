using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// The file types of Windows' "New" menu (Text Document, Bitmap image, Compressed folder, Word document, ...):
/// the ones that registered a "ShellNew" entry, the way Explorer finds them, and creating one of them.
/// </summary>
internal static class ShellNew
{
    public sealed record FileType(string Extension, string Name, string? TemplateFile, byte[]? Data);

    private static List<FileType>? _types;
    private static Task<List<FileType>>? _loading;

    /// <summary>Starts reading the list in the background (it reads the registry, which takes a moment).</summary>
    public static void Preload() => _loading ??= Task.Run(Read);

    /// <summary>The list, sorted by name; empty if it isn't read yet (it then is next time).</summary>
    public static IReadOnlyList<FileType> Types()
    {
        if (_types != null) return _types;
        Preload();
        if (_loading!.IsCompleted) _types = _loading.Result;
        return _types ?? (IReadOnlyList<FileType>)Array.Empty<FileType>();
    }

    private static List<FileType> Read()
    {
        var result = new List<FileType>();
        try
        {
            // Explorer keeps the list of extensions it found; use it when it's there, else look at every type.
            IEnumerable<string> extensions;
            using (var cache = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew"))
                extensions = cache?.GetValue("Classes") is string[] { Length: > 0 } cached
                    ? cached.Where(c => c.StartsWith('.')).ToArray()
                    : Registry.ClassesRoot.GetSubKeyNames().Where(n => n.StartsWith('.')).ToArray();

            foreach (var extension in extensions)
            {
                if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue; // "Shortcut" has its own entry
                if (Find(extension) is { } type) result.Add(type);
            }
        }
        catch (Exception) { /* no list: the New menu only has Folder and Shortcut */ }

        // "Text Document" is always offered: Windows only lists it while a text editor registers it, and that's
        // gone on some PCs (Notepad removed, or the Store version). An empty .txt, named the way Windows names it.
        if (!result.Any(t => t.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            try { result.Add(Find(".txt") ?? new FileType(".txt", TypeName(".txt", null), null, Array.Empty<byte>())); }
            catch (Exception) { result.Add(new FileType(".txt", "Text Document", null, Array.Empty<byte>())); }
        }
        return result.GroupBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())
                     .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static FileType? Find(string extension)
    {
        using var extKey = Registry.ClassesRoot.OpenSubKey(extension);
        if (extKey == null) return null;
        string? progId = extKey.GetValue("") as string;

        // ShellNew is either under the extension's ProgID ("…\.docx\Word.Document.12\ShellNew") or under the extension.
        RegistryKey? shellNew = progId != null ? extKey.OpenSubKey($@"{progId}\ShellNew") : null;
        shellNew ??= extKey.OpenSubKey("ShellNew");
        if (shellNew == null) return null;
        using (shellNew)
        {
            if (shellNew.GetValue("Command") != null && shellNew.GetValue("NullFile") == null &&
                shellNew.GetValue("FileName") == null && shellNew.GetValue("Data") == null) return null; // needs a program to run

            byte[]? data = shellNew.GetValue("Data") switch
            {
                byte[] bytes => bytes,
                string text => Encoding.Default.GetBytes(text),
                _ => null,
            };
            string? template = shellNew.GetValue("FileName") as string;
            if (template != null) template = FindTemplate(template);
            if (data == null && template == null && shellNew.GetValue("NullFile") == null) return null;
            return new FileType(extension, TypeName(extension, progId), template, data);
        }
    }

    /// <summary>A template file name: as given, or in the user's / the shared Templates folder, or Windows\ShellNew.</summary>
    private static string? FindTemplate(string name)
    {
        name = Environment.ExpandEnvironmentVariables(name);
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
        })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    /// <summary>The name Windows shows for the type ("Text Document"), in the user's language.</summary>
    private static string TypeName(string extension, string? progId)
    {
        const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        var info = new SHFILEINFO();
        SHGetFileInfo(extension, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        if (!string.IsNullOrWhiteSpace(info.szTypeName)) return info.szTypeName;
        return progId ?? extension.TrimStart('.').ToUpperInvariant() + " file";
    }

    /// <summary>Makes "New &lt;type&gt;.ext" (or "(2)", "(3)", ...) in the folder; returns its path.</summary>
    public static string Create(FileType type, string folder)
    {
        string path = FreeName(folder, $"New {type.Name}", type.Extension);
        if (type.TemplateFile != null) File.Copy(type.TemplateFile, path);
        else File.WriteAllBytes(path, type.Data ?? Array.Empty<byte>());
        return path;
    }

    public static string FreeName(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{baseName} ({n}){extension}");
        return path;
    }
}
