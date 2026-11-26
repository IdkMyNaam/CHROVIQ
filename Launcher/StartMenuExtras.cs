using System.IO;
using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>A folder button next to the power button: one of Windows' own folders, or any folder by its path.</summary>
internal sealed record StartFolder(string Name, string Glyph, string Target)
{
    /// <summary>The folders you can switch on by name, in the order they are shown.</summary>
    public static readonly string[] KnownNames =
        { "File Explorer", "Documents", "Downloads", "Pictures", "Music", "Videos", "Network", "Personal folder" };

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>A known name ("Downloads") or a folder path; null when it's neither (or the folder is gone).</summary>
    public static StartFolder? From(string entry)
    {
        string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        switch (entry)
        {
            case "File Explorer": return new(entry, "", "");
            case "Documents": return new(entry, "", Special(Environment.SpecialFolder.MyDocuments));
            case "Downloads": return new(entry, "", DownloadsFolder());
            case "Pictures": return new(entry, "", Special(Environment.SpecialFolder.MyPictures));
            case "Music": return new(entry, "", Special(Environment.SpecialFolder.MyMusic));
            case "Videos": return new(entry, "", Special(Environment.SpecialFolder.MyVideos));
            case "Network": return new(entry, "", "shell:NetworkPlacesFolder");
            case "Personal folder": return new(entry, "", Special(Environment.SpecialFolder.UserProfile));
        }
        string path = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        if (path.Length == 0 || !Directory.Exists(path)) return null;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return new(name.Length > 0 ? name : path, "", path);
    }

    /// <summary>Is this one of the names in <see cref="KnownNames"/> (and not a path)?</summary>
    public static bool IsKnown(string entry) => KnownNames.Contains(entry);

    private static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
                try { return Marshal.PtrToStringUni(p) ?? ""; } finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception) { /* fall back below */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Opens it in File Explorer (just File Explorer for "File Explorer").</summary>
    public void Open()
    {
        if (Target.Length == 0) ShellLaunch.Start("explorer.exe");
        else ShellLaunch.OpenFolder(Target);
    }
}

/// <summary>A file you opened lately, from Windows' own list (the shortcuts in the Recent folder).</summary>
internal sealed record RecentFile(string Name, string Path, DateTime OpenedAt)
{
    /// <summary>
    /// Whether Windows keeps the list ("Show recently opened items in Start, Jump Lists and File Explorer",
    /// on unless turned off). When it's off, Windows adds nothing new, so the section stays hidden.
    /// </summary>
    public static bool WindowsKeepsList()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("Start_TrackDocs") is not int value || value != 0;
        }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// The <paramref name="count"/> files opened most recently that still exist; folders are left out.
    /// Reads shortcut files, so call it off the UI thread.
    /// </summary>
    public static List<RecentFile> Load(int count)
    {
        var result = new List<RecentFile>();
        if (count <= 0 || !WindowsKeepsList()) return result;
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(folder)) return result;
            // Newest first; a few extra, since some point at folders or files that are gone.
            var links = new DirectoryInfo(folder).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(count * 4 + 10).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ShortcutReader.ForEach(links.Select(l => l.FullName), (link, target) =>
            {
                if (result.Count >= count) return false;
                if (target != null && seen.Add(target) && File.Exists(target))
                    result.Add(new RecentFile(System.IO.Path.GetFileName(target), target, File.GetLastWriteTime(link)));
                return true;
            });
        }
        catch (Exception ex) { AppTrace.Log($"Recent files: {ex.Message}"); }
        return result;
    }
}

/// <summary>Reads many shortcuts' targets with one Windows Script Host shell object (part of Windows).</summary>
internal static class ShortcutReader
{
    /// <summary>Calls <paramref name="each"/> with every shortcut and its target (null if it has none); stops when it returns false.</summary>
    public static void ForEach(IEnumerable<string> shortcuts, Func<string, string?, bool> each)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            foreach (var path in shortcuts)
            {
                string? target = null;
                try
                {
                    dynamic shortcut = shell.CreateShortcut(path);
                    string t = shortcut.TargetPath;
                    if (!string.IsNullOrWhiteSpace(t)) target = Environment.ExpandEnvironmentVariables(t);
                }
                catch (Exception) { /* unreadable shortcut: no target */ }
                if (!each(path, target)) break;
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
