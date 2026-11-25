using System.Windows;

namespace WindowsCustomizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A settings restore staged earlier goes into place first, before anything reads the settings.
        SettingsBackup.ApplyPending(e.Args);

        // "--shell" when started by ShellLauncher.ps1 as the user's shell (Phase 4).
        ShellMode.ReadCommandLine(e.Args);
        if (ShellMode.RefusedBecauseExplorer)
            MessageBox.Show("Explorer is the shell right now, so Windows Customizer starts as a normal app instead of as the shell.\n\n" +
                            "Shell mode only starts when you sign in with \"Use as shell\" turned on.",
                            "Windows Customizer", MessageBoxButton.OK, MessageBoxImage.Information);

        // If the app crashes or is ended while the Windows taskbar is hidden, bring it back.
        // These don't swallow the error; they only clean up first. (Killing the process from
        // Task Manager skips them: use Restore-Taskbar.ps1 then.)
        DispatcherUnhandledException += (_, e) =>
        {
            // A known, harmless WPF animation glitch (a control removed mid-animation): note it and carry on,
            // instead of crashing (or, started from Visual Studio, freezing while Windows waits for a debugger).
            if (SafeUi.IsAnimationGlitch(e.Exception))
            {
                AppTrace.Log("WPF animation glitch caught (harmless): " + e.Exception.Message);
                e.Handled = true;
                return;
            }
            AppTrace.Log($"CRASH: {e.Exception}");
            WindowsTaskbar.RestoreNative();
            // Tray icons back to Explorer (this handler runs on the UI thread, which the tray needs).
            try { TrayHost.Shutdown(); } catch (Exception) { /* Restore-Taskbar.ps1 also does this */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppTrace.Log($"CRASH (background thread): {e.ExceptionObject}");
            WindowsTaskbar.RestoreNative();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTaskbar.RestoreNative();

        base.OnStartup(e);
    }
}
