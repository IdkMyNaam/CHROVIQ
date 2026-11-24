using System.IO;

namespace WindowsCustomizer;

/// <summary>Which program a pinned entry starts, so running windows can be matched to their pin.</summary>
internal static class PinnedTargets
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The .exe behind a pinned entry: the entry itself for a program, the target for a shortcut (.lnk).
    /// Null when it isn't a program (a document, a folder, a missing file).
    /// </summary>
    public static string? ProgramOf(string pinned)
    {
        var path = Environment.ExpandEnvironmentVariables(pinned.Trim());
        if (Cache.TryGetValue(path, out var cached)) return cached;

        string? program = null;
        try
        {
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                path = ShortcutTarget(path) ?? path;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                program = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // Unreadable shortcut: treat as "not a program", the pin still launches it.
        }

        Cache[pinned.Trim()] = program;
        Cache[path] = program;
        return program;
    }

    /// <summary>Reads a shortcut's target through the Windows Script Host shell object (part of Windows).</summary>
    private static string? ShortcutTarget(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
