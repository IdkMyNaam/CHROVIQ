using System.IO;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>
/// A small trace log (%AppData%\WindowsCustomizer\trace.log) of steps worth knowing about when something
/// goes wrong (the Alt+Tab switcher, for one), and a watch that notes when the UI thread stops responding:
/// how long, and which step it was busy with. Kept under 512 KB.
/// </summary>
internal static class AppTrace
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "trace.log");
    private static readonly object Lock = new();
    private static volatile string _lastStep = "(none)";

    /// <summary>Notes a step (any thread). The latest one is named if the UI thread then freezes.</summary>
    public static void Log(string text)
    {
        _lastStep = text;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 512 * 1024) file.MoveTo(FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {text}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* tracing is optional */ }
    }

    private static Thread? _watch;

    /// <summary>Starts watching the UI thread (call once, on the UI thread).</summary>
    public static void WatchForFreezes()
    {
        if (_watch != null) return;
        var ui = Dispatcher.CurrentDispatcher;
        _watch = new Thread(() =>
        {
            while (true)
            {
                using var answered = new ManualResetEventSlim();
                ui.BeginInvoke(DispatcherPriority.Send, () => answered.Set());
                if (answered.Wait(TimeSpan.FromSeconds(3))) { Thread.Sleep(1000); continue; }

                var since = DateTime.Now.AddSeconds(-3);
                Log($"FREEZE: the UI thread has not responded for 3 s; last step: {_lastStep}");
                answered.Wait();
                Log($"FREEZE over after {(DateTime.Now - since).TotalSeconds:0.0} s");
            }
        })
        { IsBackground = true, Name = "Freeze watch" };
        _watch.Start();
    }
}
