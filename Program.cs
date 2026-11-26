using System.Runtime.CompilerServices;

namespace WindowsCustomizer;

/// <summary>
/// The entry point. "--launcher" runs the small shell launcher (see <see cref="ShellLauncher"/>) without loading
/// WPF at all, so it starts in a fraction of a second; anything else starts the app as usual.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--launcher", StringComparison.OrdinalIgnoreCase)))
            return ShellLauncher.Run(args);
        // The administrator copy for the sign-in screen settings: writes them and ends.
        if (args.Any(a => a.Equals("--signin-admin", StringComparison.OrdinalIgnoreCase)))
            return SignInScreen.RunAsAdmin(args);
        return RunApp();
    }

    // Its own method, so WPF (App and everything it needs) is only loaded when the app really starts.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
