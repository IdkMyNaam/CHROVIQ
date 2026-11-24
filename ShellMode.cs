using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Phase 4: Windows Customizer as the shell (instead of Explorer), for the current user only.
///
/// Turning it on writes the "Shell" value in the user's own Winlogon key (HKCU), never the
/// machine-wide one, so other accounts keep Explorer. The value starts ShellLauncher.ps1, not the
/// app directly: the launcher starts Explorer whenever the app ends, so a crash never leaves the user
/// without a shell. A .reg backup of the key is saved first. Restore-Shell.ps1 undoes it without the app.
/// </summary>
internal static class ShellMode
{
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string WinlogonRegPath = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public static readonly string BackupFolder = Path.Combine(AppSettings.Folder, "Backups");

    /// <summary>True when this run was started by the launcher as the shell (command line "--shell").</summary>
    public static bool IsShell { get; private set; }

    /// <summary>True when the startup programs should be started this run (command line "--startup", once per sign-in).</summary>
    public static bool RunStartupPrograms { get; private set; }

    public static void ReadCommandLine(string[] args)
    {
        IsShell = args.Any(a => a.Equals("--shell", StringComparison.OrdinalIgnoreCase));
        RunStartupPrograms = IsShell && args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
    }

    private static string LauncherPath => Path.Combine(AppContext.BaseDirectory, "ShellLauncher.ps1");

    /// <summary>The command Windows runs at sign-in when we are the shell.</summary>
    private static string LauncherCommand =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{LauncherPath}\"";

    /// <summary>The user's own shell setting, or null when Windows uses its default (Explorer).</summary>
    public static string? CurrentUserShell()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
        return key?.GetValue("Shell") as string;
    }

    /// <summary>True if Windows Customizer is set as this user's shell (takes effect at the next sign-in).</summary>
    public static bool IsInstalled() =>
        CurrentUserShell() is { } shell && shell.Contains("ShellLauncher.ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exports the user's Winlogon key to a .reg file in the backups folder; returns its path.</summary>
    public static string Backup(string reason)
    {
        Directory.CreateDirectory(BackupFolder);
        string file = Path.Combine(BackupFolder, $"winlogon-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
        using var reg = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{WinlogonRegPath}\" \"{file}\" /y")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not run reg.exe for the backup.");
        reg.WaitForExit(10000);
        if (reg.ExitCode != 0 || !File.Exists(file))
            throw new InvalidOperationException("The registry backup failed, so nothing was changed.");
        return file;
    }

    /// <summary>
    /// Makes Windows Customizer this user's shell from the next sign-in. Backs up first; refuses if the
    /// launcher or the restore script is missing (they are the safety net).
    /// </summary>
    public static string Install()
    {
        if (!File.Exists(LauncherPath))
            throw new FileNotFoundException("ShellLauncher.ps1 is missing next to the app, so the shell can't be switched safely.");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Restore-Shell.ps1")))
            throw new FileNotFoundException("Restore-Shell.ps1 is missing next to the app, so the shell can't be switched safely.");

        string backup = Backup("before-shell");
        using var key = Registry.CurrentUser.CreateSubKey(WinlogonKey);
        key.SetValue("Shell", LauncherCommand, RegistryValueKind.String);
        return backup;
    }

    /// <summary>Explorer is this user's shell again from the next sign-in (removes our Shell value).</summary>
    public static string Uninstall()
    {
        string backup = Backup("before-explorer");
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey, writable: true);
        key?.DeleteValue("Shell", throwOnMissingValue: false);
        return backup;
    }
}
