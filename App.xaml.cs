using System.Windows;

namespace WindowsCustomizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // If the app crashes or is ended while the Windows taskbar is hidden, bring it back.
        // These don't swallow the error; they only clean up first. (Killing the process from
        // Task Manager skips them: use Restore-Taskbar.ps1 then.)
        DispatcherUnhandledException += (_, _) => WindowsTaskbar.RestoreNative();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => WindowsTaskbar.RestoreNative();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTaskbar.RestoreNative();

        base.OnStartup(e);
    }
}
