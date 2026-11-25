using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>An app the search box can find and start.</summary>
public sealed record AppEntry(string Name, string LaunchPath, bool IsShellApp)
{
    /// <summary>The app's icon: from its shortcut, or for Store apps the one Windows shows (via the shell).</summary>
    public ImageSource? Icon => IsShellApp ? ShellIcons.Get(ShellName, 32) : FileIcons.Get(LaunchPath);

    /// <summary>Where the app is: the program a shortcut opens (or the file itself). Null for Store apps.</summary>
    public string? Location => IsShellApp ? null : PinnedTargets.ProgramOf(LaunchPath) ?? LaunchPath;

    /// <summary>How the shell names this app: the shortcut file, or "shell:AppsFolder\&lt;id&gt;" for apps without one.</summary>
    public string ShellName => IsShellApp ? $"shell:AppsFolder\\{LaunchPath}" : LaunchPath;

    /// <summary>Starts the app: a Start menu shortcut directly, other apps through the Windows "Apps" folder.</summary>
    public void Launch()
    {
        if (IsShellApp)
            ShellLaunch.Start("explorer.exe", $"shell:AppsFolder\\{LaunchPath}");
        else
            ShellLaunch.Start(LaunchPath);
    }

    public override string ToString() => Name;
}

/// <summary>
/// Finds installed apps by name: the Start menu shortcuts of all users and this user, plus
/// everything in the Windows "Apps" folder (which also has Store apps). Built once in the background.
/// </summary>
internal static class AppSearch
{
    private static Task<List<AppEntry>>? _index;
    private static DateTime _indexedAt;

    /// <summary>The app list; built on first use and refreshed when older than 10 minutes.</summary>
    public static Task<List<AppEntry>> Index()
    {
        if (_index == null || (_index.IsCompleted && DateTime.Now - _indexedAt > TimeSpan.FromMinutes(10)))
        {
            _indexedAt = DateTime.Now;
            _index = RunOnStaThread(BuildIndex); // the shell's COM objects want an STA thread
        }
        return _index;
    }

    /// <summary>Best matches first: name starts with the text, then a word starts with it, then it appears anywhere.</summary>
    public static List<AppEntry> Find(List<AppEntry> apps, string text, int max)
    {
        text = text.Trim();
        if (text.Length == 0) return new();

        int Rank(AppEntry app)
        {
            var name = app.Name;
            if (name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return 0;
            if (name.Split(' ', '-', '.', '_').Any(w => w.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))) return 1;
            if (name.Contains(text, StringComparison.CurrentCultureIgnoreCase)) return 2;
            return -1;
        }

        return apps.Select(a => (app: a, rank: Rank(a)))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank).ThenBy(x => x.app.Name.Length).ThenBy(x => x.app.Name)
            .Take(max)
            .Select(x => x.app)
            .ToList();
    }

    private static List<AppEntry> BuildIndex()
    {
        var byName = new Dictionary<string, AppEntry>(StringComparer.CurrentCultureIgnoreCase);

        // 1. Start menu shortcuts (all users + this user): real files, with good icons.
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> shortcuts;
            try { shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; } // a folder we may not read

            foreach (var shortcut in shortcuts)
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue; // not something to search for
                byName.TryAdd(name, new AppEntry(name, shortcut, IsShellApp: false));
            }
        }

        // 2. The "Apps" folder adds Store apps and anything without a Start menu shortcut.
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic? folder = shell.NameSpace("shell:AppsFolder");
                if (folder != null)
                {
                    foreach (dynamic item in folder.Items())
                    {
                        string name = item.Name;
                        string path = item.Path;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                            byName.TryAdd(name, new AppEntry(name, path, IsShellApp: true));
                    }
                }
            }
        }
        catch (Exception)
        {
            // The Start menu shortcuts are still there.
        }

        return byName.Values.OrderBy(a => a.Name).ToList();
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }
}
