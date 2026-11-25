using System.Diagnostics;
using System.Windows.Threading;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>
/// Checks whether a game from the settings is running: when the active window changes (a game that
/// starts or closes almost always changes it), plus every 30 seconds as a safety net.
/// If so, pauses Wallpaper Engine; when no game runs any more, resumes it.
/// Only resumes if this class paused it, so it never overrides a pause the user made.
/// Does nothing at all while "pause for games" is off.
/// </summary>
public sealed class GameWatcher
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    // The active window often changes several times in a row (alt-tab, a game's launcher): check once after it settles.
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly WinEventProc _foregroundCallback; // kept in a field so the GC doesn't collect it
    private IntPtr _foregroundHook;
    private bool _pausedByUs;
    private bool _busy;

    /// <summary>Raised with a short message when the watcher pauses, resumes, or fails.</summary>
    public event Action<string>? StatusChanged;

    public GameWatcher(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await CheckAsync();
        _settle.Tick += async (_, _) => { _settle.Stop(); await CheckAsync(); };
        _foregroundCallback = (_, _, _, _, _, _, _) => { _settle.Stop(); _settle.Start(); };
    }

    public void Start()
    {
        _timer.Start();
        if (_foregroundHook == IntPtr.Zero)
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                                              _foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _ = CheckAsync();
    }

    /// <summary>Stops watching. Resumes Wallpaper Engine if the watcher had paused it.</summary>
    public void Stop()
    {
        _timer.Stop();
        _settle.Stop();
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_pausedByUs)
        {
            _pausedByUs = false;
            _ = WallpaperEngine.PlayAsync(); // fire and forget: the app is closing
        }
    }

    private async Task CheckAsync()
    {
        if (_busy) return; // previous check still sending a command
        if (!_settings.PauseForGames && !_pausedByUs) return; // off: don't even look at the processes
        _busy = true;
        try
        {
            string? game = _settings.PauseForGames ? FindRunningGame() : null;

            if (game != null && !_pausedByUs && WallpaperEngine.IsRunning)
            {
                await WallpaperEngine.PauseAsync();
                _pausedByUs = true;
                StatusChanged?.Invoke($"Paused Wallpaper Engine ({game} is running).");
            }
            else if (game == null && _pausedByUs)
            {
                await WallpaperEngine.PlayAsync();
                _pausedByUs = false;
                StatusChanged?.Invoke("Resumed Wallpaper Engine.");
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Game mode: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>One look at the running processes (not one per game in the list).</summary>
    private string? FindRunningGame()
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _settings.GameProcesses)
        {
            var name = entry.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length > 0) wanted.Add(name);
        }
        if (wanted.Count == 0) return null;

        string? found = null;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
                if (found == null && wanted.Contains(p.ProcessName)) found = p.ProcessName;
        }
        return found;
    }
}
