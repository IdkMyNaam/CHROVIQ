using System.Windows;

namespace WindowsCustomizer;

public partial class App : Application
{
    /// <summary>
    /// As the shell, the screen is black until something draws the desktop. So the wallpaper starts loading and the
    /// desktops open first; the main window (taskbars, everything else) is made only after they're drawn.
    /// </summary>
    private void ShowDesktopsFirst()
    {
        try
        {
            Wallpaper.Preload();
            DesktopWindow.Early.AddRange(DesktopWindow.OpenForAllMonitors(null));
            StartupTimer.Mark("desktops opened (shell)");
            ShellMode.Log($"Desktops opened first ({DesktopWindow.Early.Count} monitor(s))");
            if (DesktopWindow.Early.FirstOrDefault() is { } first)
                first.ContentRendered += (_, _) => { StartupTimer.Mark("desktop on screen (shell)"); ShellMode.Log("Desktop on screen"); };
            // A spinner on the main monitor until the taskbar is up (its own thread: it turns while the app is busy).
            if (Monitors.All().FirstOrDefault(m => m.IsPrimary) is { } primary) LoadingSpinner.Start(primary.Bounds);
        }
        catch (Exception ex)
        {
            ShellMode.Log($"Could not open the desktops first: {ex.Message}");
            foreach (var d in DesktopWindow.Early) d.Close();
            DesktopWindow.Early.Clear();
            MakeMainWindow(); // it opens the desktops as before
            return;
        }

        // After the desktops have drawn (and their icons' pictures loaded), at a lower priority.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, MakeMainWindow);
        // Never left without a taskbar: if the app stays busy, the main window comes after 2 seconds anyway.
        var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            if (!_mainMade) ShellMode.Log("Still busy after 2 s: making the main window now");
            MakeMainWindow();
        };
        fallback.Start();
    }

    private bool _mainMade;

    /// <summary>The main window (settings, taskbars, tray icon). Made here instead of through StartupUri, so the shell can open the desktops first.</summary>
    private void MakeMainWindow()
    {
        if (_mainMade) return;
        _mainMade = true;
        var main = new MainWindow();
        MainWindow = main; // the first window made (a desktop, as the shell) would otherwise count as the main window
        main.Show();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTimer.Mark("app starts (.NET and WPF loaded)");
        // A settings restore staged earlier goes into place first, before anything reads the settings.
        SettingsBackup.ApplyPending(e.Args);

        // "--shell" when started by the launcher ("--launcher", or the older ShellLauncher.ps1) as the user's shell.
        ShellMode.ReadCommandLine(e.Args);
        if (ShellMode.RefusedBecauseExplorer)
            MessageBox.Show("Explorer is the shell right now, so CHROVIQ starts as a normal app instead of as the shell.\n\n" +
                            "Shell mode only starts when you sign in with \"Use as shell\" turned on.",
                            "CHROVIQ", MessageBoxButton.OK, MessageBoxImage.Information);

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
        if (ShellMode.IsShell) ShowDesktopsFirst();
        else MakeMainWindow();
    }
}
