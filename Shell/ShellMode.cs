using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Phase 4: Windows Customizer as the shell (instead of Explorer), for the current user only.
///
/// Turning it on writes the "Shell" value in the user's own Winlogon key (HKCU), never the
/// machine-wide one, so other accounts keep Explorer. The value starts the launcher ("--launcher", see
/// ShellLauncher.cs), not the app directly: the launcher starts Explorer whenever the app ends, so a crash never
/// leaves the user without a shell. (Older versions used ShellLauncher.ps1; that value still works.) A .reg backup of the key is saved first. Restore-Shell.ps1 undoes it without the app.
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

    /// <summary>True when "--shell" was asked for but refused, because Explorer is the shell right now.</summary>
    public static bool RefusedBecauseExplorer { get; private set; }

    public static void ReadCommandLine(string[] args)
    {
        IsShell = args.Any(a => a.Equals("--shell", StringComparison.OrdinalIgnoreCase));
        RunStartupPrograms = IsShell && args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        // Safety: never run as the shell next to a live Explorer shell. Fighting it over the shell window and the
        // work area is what left Explorer hung in the first test. Run as a normal app instead.
        if (IsShell && ExplorerIsShell())
        {
            IsShell = RunStartupPrograms = false;
            RefusedBecauseExplorer = true;
            Log("Started with --shell, but Explorer is the shell: running as a normal app");
        }
        else if (IsShell) Log($"----- Started as the shell (startup programs: {(RunStartupPrograms ? "yes" : "no")})");
    }

    // ---------- Log (shell mode only): %AppData%\WindowsCustomizer\shell.log ----------

    public static readonly string LogFile = Path.Combine(AppSettings.Folder, "shell.log");
    private static readonly object LogLock = new();

    /// <summary>Adds a timed line to shell.log, so a test sign-in can be traced afterwards. Never throws.</summary>
    public static void Log(string text)
    {
        if (!IsShell && !RefusedBecauseExplorer) return;
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging is optional */ }
    }

    private static Timer? _explorerWatch;

    /// <summary>For a while after sign-in: logs every Explorer that starts in this session, with who started it.</summary>
    public static void WatchForExplorer(TimeSpan howLong)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var seen = new HashSet<int>();
        var until = DateTime.Now + howLong;
        _explorerWatch = new Timer(_ =>
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
                using (p)
                    if (p.SessionId == session && seen.Add(p.Id))
                        Log($"Explorer is running: pid {p.Id}, started by {ParentName(p)}");
            if (DateTime.Now > until) { _explorerWatch?.Dispose(); Log("Stopped watching for Explorer"); }
        }, null, 0, 1000);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessBasicInfo { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, ParentProcessId; }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInfo info, int size, out int returned);

    private static string ParentName(Process p)
    {
        try
        {
            var info = new ProcessBasicInfo();
            if (NtQueryInformationProcess(p.Handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf<ProcessBasicInfo>(), out _) != 0) return "?";
            int parent = info.ParentProcessId.ToInt32();
            string name;
            try { using var pp = Process.GetProcessById(parent); name = pp.Id == Environment.ProcessId ? "CHROVIQ (this app)" : pp.ProcessName; }
            catch (ArgumentException) { name = "a process that has ended"; }
            return $"{name} (pid {parent})";
        }
        catch (Exception) { return "?"; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>True when Explorer owns the shell window or the taskbar (Shell_TrayWnd).</summary>
    private static bool ExplorerIsShell()
    {
        static bool OwnedByExplorer(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; } // the process is gone
        }
        return OwnedByExplorer(GetShellWindow()) || OwnedByExplorer(FindWindow("Shell_TrayWnd", null));
    }

    private static string AppPath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WindowsCustomizer.exe");

    /// <summary>
    /// The command Windows runs at sign-in when we are the shell: this app's own launcher mode, which starts in a
    /// fraction of a second (the older PowerShell launcher took most of a second just to start PowerShell).
    /// </summary>
    private static string LauncherCommand => $"\"{AppPath}\" --launcher";

    /// <summary>The user's own shell setting, or null when Windows uses its default (Explorer).</summary>
    public static string? CurrentUserShell()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WinlogonKey);
        return key?.GetValue("Shell") as string;
    }

    /// <summary>True if Windows Customizer is set as this user's shell (takes effect at the next sign-in).</summary>
    public static bool IsInstalled() =>
        CurrentUserShell() is { } shell &&
        (shell.Contains("--launcher", StringComparison.OrdinalIgnoreCase) || shell.Contains("ShellLauncher.ps1", StringComparison.OrdinalIgnoreCase));

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
    /// restore script is missing (it is the safety net, with the launcher built into the app).
    /// </summary>
    public static string Install()
    {
        if (!File.Exists(AppPath))
            throw new FileNotFoundException("The app can't be found where it runs from, so the shell can't be switched safely.");
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
