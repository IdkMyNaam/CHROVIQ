using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowsCustomizer;

/// <summary>
/// Starts programs, files and folders. Normally the same as double-clicking them. When Windows Customizer is
/// the shell, explorer.exe is never started directly: a plain "explorer.exe" (no arguments) takes over as a
/// second shell (its own desktop and taskbar). Folders are opened through the Windows shell instead, which
/// only opens a File Explorer window.
/// </summary>
internal static class ShellLaunch
{
    private const string Home = "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";   // File Explorer "Home" (Windows 11)
    private const string ThisPc = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"; // "This PC"

    /// <summary>Opens a program, file, folder, shortcut or address. Throws like Process.Start when it can't.</summary>
    public static void Start(string target, string? arguments = null, string? verb = null)
    {
        if (ShellMode.IsShell)
        {
            if (IsExplorer(target)) { OpenInExplorer(arguments); return; }
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ReadShortcut(target) is { } link && IsExplorer(link.Target))
            {
                OpenInExplorer(string.IsNullOrWhiteSpace(arguments) ? link.Arguments : arguments);
                return;
            }
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "", Verb = verb ?? "" })?.Dispose();
    }

    /// <summary>Opens a folder in File Explorer.</summary>
    public static void OpenFolder(string folder) => Start("explorer.exe", $"\"{folder}\"");

    /// <summary>Opens the folder of a file with the file selected.</summary>
    public static void ShowInFolder(string path) => Start("explorer.exe", $"/select,\"{path}\"");

    private static bool IsExplorer(string target)
    {
        string name = Path.GetFileName(Environment.ExpandEnvironmentVariables(target.Trim().Trim('"')));
        return name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What "explorer.exe &lt;arguments&gt;" would show, through the shell instead of a new explorer.exe.</summary>
    private static void OpenInExplorer(string? arguments)
    {
        string args = arguments?.Trim() ?? "";
        ShellMode.Log($"Opening in File Explorer (not starting explorer.exe): {(args.Length == 0 ? "(no arguments)" : args)}");

        if (args.StartsWith("/select,", StringComparison.OrdinalIgnoreCase))
        {
            string item = args["/select,".Length..].Trim().Trim('"');
            if (SelectInFolder(item)) return;
            args = Path.GetDirectoryName(item) ?? "";
        }

        string what = args.Trim('"');
        if (what.Length == 0)
        {
            try { ShellExecute(Home); }
            catch (Exception) { ShellExecute(ThisPc); }
            return;
        }
        ShellExecute(what); // a folder, a "shell:" place, or shell:AppsFolder\<app id> (starts that app)
    }

    private static void ShellExecute(string what) =>
        Process.Start(new ProcessStartInfo(what) { UseShellExecute = true })?.Dispose();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr items, uint flags);

    /// <summary>"Open file location": its folder with the item selected (with no items given, Windows selects the item itself).</summary>
    private static bool SelectInFolder(string path)
    {
        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
        try { return SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0; }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    /// <summary>A shortcut's target and arguments, through the Windows Script Host shell object (part of Windows).</summary>
    private static (string Target, string Arguments)? ReadShortcut(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                string target = shortcut.TargetPath;
                string arguments = shortcut.Arguments;
                return string.IsNullOrWhiteSpace(target) ? null : (Environment.ExpandEnvironmentVariables(target), arguments ?? "");
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception) { return null; }
    }
}
