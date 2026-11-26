using System.Diagnostics;

namespace WindowsCustomizer;

/// <summary>
/// Startup timing in trace.log ("Startup +812 ms (+40): app themes"): time since the process started, and since the
/// previous step. Shows where a slow start goes; costs nothing worth mentioning.
/// </summary>
internal static class StartupTimer
{
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;
    private static double _last;
    private static bool _done;

    public static void Mark(string step, bool done = false)
    {
        if (_done) return;
        double now = (DateTime.Now - ProcessStart).TotalMilliseconds;
        AppTrace.Log($"Startup +{now:0} ms (+{now - _last:0}): {step}");
        _last = now;
        _done = done;
    }
}
