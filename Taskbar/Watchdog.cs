using System.Diagnostics;
using System.IO;

namespace WindowsCustomizer;

/// <summary>
/// A tiny hidden helper process that restores the Windows taskbar if this app ends without cleaning up
/// (killed from Task Manager or Visual Studio's stop button; those skip every exit and crash handler).
/// It runs Restore-Taskbar.ps1 in waiting mode: it waits for this process to end, then restores.
/// Runs only while the app has taken something over: the Windows taskbar hidden, or the tray icons.
/// A normal exit stops the helper first, because the app restores everything itself then.
/// </summary>
internal static class Watchdog
{
    private static Process? _helper;
    private static string? _helperArguments;

    /// <summary>Starts, restarts or stops the helper to match what the app currently has taken over.</summary>
    public static void Update()
    {
        bool needed = WindowsTaskbar.IsHiding || TrayHost.IsRunning;
        if (!needed)
        {
            Stop();
            return;
        }

        // Turn auto-hide off when restoring only if we turned it on ourselves (the user didn't have it).
        bool keepAutoHide = !WindowsTaskbar.IsHiding || WindowsTaskbar.OriginalAutoHide == true;
        string script = Path.Combine(AppContext.BaseDirectory, "Restore-Taskbar.ps1");
        if (!File.Exists(script)) return; // run from somewhere without the script: nothing to start

        string arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                           $"-WaitForProcessId {Environment.ProcessId}" + (keepAutoHide ? " -KeepAutoHide" : "");
        // Already running as wanted (checked without asking the process, which can wait: see Stop).
        if (_helper != null && _helperArguments == arguments) return;

        Stop();
        try
        {
            _helper = Process.Start(new ProcessStartInfo("powershell.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            _helperArguments = arguments;
        }
        catch (Exception)
        {
            // No PowerShell: the manual Restore-Taskbar.ps1 is still there.
        }
    }

    /// <summary>Stops the helper (the app is restoring things itself, or nothing needs restoring).</summary>
    public static void Stop()
    {
        var helper = _helper;
        _helper = null;
        _helperArguments = null;
        if (helper == null) return;
        // In the background: asking a process whether it has ended can wait about a second, and this runs at
        // startup (it made starting a second slower). The new helper is started meanwhile; both restore the
        // same way, so a moment of overlap is harmless.
        Task.Run(() =>
        {
            try
            {
                if (!helper.HasExited) helper.Kill();
            }
            catch (Exception)
            {
                // Already gone.
            }
            helper.Dispose();
        });
    }
}
