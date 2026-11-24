using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace WindowsCustomizer;

/// <summary>
/// Follows what's playing on the PC (Spotify, browsers, media players: anything that shows up in
/// Windows' own media flyout) and controls it. Uses Windows' media session API.
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public bool HasSession => _session != null;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public ImageSource? Cover { get; private set; }

    /// <summary>Something changed (new song, paused, other app). May be raised on a background thread.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach(_manager.GetCurrentSession());
            Attach(_manager.GetCurrentSession());
        }
        catch (Exception)
        {
            // Media sessions not available on this PC: the module just stays hidden.
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_disposed) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
        }
        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
    {
        IsPlaying = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        Changed?.Invoke();
    }

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session == null)
        {
            Title = Artist = "";
            IsPlaying = false;
            Cover = null;
            Changed?.Invoke();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";
            IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Cover = props?.Thumbnail != null ? await LoadCoverAsync(props.Thumbnail) : null;
        }
        catch (Exception)
        {
            // The app closed its session while we asked; the next event updates us.
        }
        Changed?.Invoke();
    }

    /// <summary>Album art as a small frozen image (frozen, so the UI thread can use it).</summary>
    private static async Task<ImageSource?> LoadCoverAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void PlayPause() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    public void Dispose()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session = null;
        }
        _disposed = true; // also stops CurrentSessionChanged from attaching again
        Changed = null;
    }
}
