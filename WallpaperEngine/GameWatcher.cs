using System.Diagnostics;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>
/// Every 5 seconds, checks whether a game from the settings is running.
/// If so, pauses Wallpaper Engine; when no game runs any more, resumes it.
/// Only resumes if this class paused it, so it never overrides a pause the user made.
/// </summary>
public sealed class GameWatcher
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _pausedByUs;
    private bool _busy;

    /// <summary>Raised with a short message when the watcher pauses, resumes, or fails.</summary>
    public event Action<string>? StatusChanged;

    public GameWatcher(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start() => _timer.Start();

    /// <summary>Stops watching. Resumes Wallpaper Engine if the watcher had paused it.</summary>
    public void Stop()
    {
        _timer.Stop();
        if (_pausedByUs)
        {
            _pausedByUs = false;
            _ = WallpaperEngine.PlayAsync(); // fire and forget: the app is closing
        }
    }

    private async Task CheckAsync()
    {
        if (_busy) return; // previous check still sending a command
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

    private string? FindRunningGame()
    {
        foreach (var entry in _settings.GameProcesses)
        {
            var name = entry.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.Length == 0) continue;

            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes) p.Dispose();
            if (processes.Length > 0) return name;
        }
        return null;
    }
}
