using System.Diagnostics;
using System.IO;

namespace WindowsCustomizer;

/// <summary>
/// The shell launcher, with a failsafe: whenever the app ends, Explorer starts.
///
/// When "Use as shell" is on, Windows runs "WindowsCustomizer.exe --launcher" at sign-in instead of Explorer
/// (the Shell value in the user's own Winlogon key). It starts the app as the shell and waits for it for the whole
/// session. If the app ends for ANY reason (a crash at startup or later, killed, or Exit), Explorer is started at
/// once, so the user is never left without a taskbar and desktop. What happened goes to
/// %AppData%\WindowsCustomizer\shell-launcher.log.
///
/// It does the same as ShellLauncher.ps1 (still there for older settings), but without starting PowerShell
/// first, which took most of a second at sign-in. It uses nothing from WPF or the rest of the app on purpose:
/// it must keep working even when the app itself is broken.
/// </summary>
internal static class ShellLauncher
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsCustomizer", "shell-launcher.log");

    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
        }
        catch (Exception) { /* no log is no reason to stop */ }
    }

    /// <param name="args">
    /// "--launcher", and optionally "--no-startup-programs" (a test while already signed in) and "--app &lt;path&gt;"
    /// (another program to start, for testing the failsafe).
    /// </param>
    public static int Run(string[] args)
    {
        try
        {
            int appIndex = Array.FindIndex(args, a => a.Equals("--app", StringComparison.OrdinalIgnoreCase));
            string app = appIndex >= 0 && appIndex + 1 < args.Length
                ? args[appIndex + 1]
                : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WindowsCustomizer.exe");
            bool startupPrograms = !args.Any(a => a.Equals("--no-startup-programs", StringComparison.OrdinalIgnoreCase));

            Log($"Starting {app} as shell");
            // --shell: run as the shell (desktop, startup programs); --startup: start the startup programs once.
            using var process = Process.Start(new ProcessStartInfo(app, startupPrograms ? "--shell --startup" : "--shell")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(app) ?? AppContext.BaseDirectory,
            }) ?? throw new InvalidOperationException("the app did not start");
            process.WaitForExit();
            Log($"App ended (exit code {process.ExitCode}); starting explorer.exe");
        }
        catch (Exception ex)
        {
            Log($"Could not run the app: {ex.Message}; starting explorer.exe");
        }

        StartExplorer();
        return 0;
    }

    /// <summary>
    /// Never leaves the user without a shell. If Explorer runs already in this sign-in, starting it again would only
    /// open a folder window. (Other sign-ins, with "Switch user", have their own Explorer and don't count.)
    /// </summary>
    private static void StartExplorer()
    {
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            if (Process.GetProcessesByName("explorer").Any(p => { using (p) return p.SessionId == session; }))
            {
                Log("explorer.exe is already running; not starting it again");
                return;
            }
        }
        catch (Exception ex) { Log($"Could not check for explorer.exe ({ex.Message}); starting it anyway"); }

        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log($"Could not start explorer.exe: {ex.Message}"); }
    }
}
