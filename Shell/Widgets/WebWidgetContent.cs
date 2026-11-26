using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WindowsCustomizer;

/// <summary>
/// A web widget (a plugin): its HTML page in a browser view (WebView2, drawn inside the widget so it stays see-through),
/// with live data from CHROVIQ. What it may use is limited to what its widget.json asks for:
/// - Its own files are served by CHROVIQ from its folder only, with a strict Content-Security-Policy.
/// - Any other address is blocked, except the ones in "network" (that includes WebSockets).
/// - It can't open windows, download, use the camera, microphone, location or notifications.
/// - Files: only the ones you chose for it. Commands: only once you approved exactly those commands, and only while
///   commands are allowed for widgets at all.
/// </summary>
internal sealed class WebWidgetContent : WidgetContent
{
    private const string Host = "widget.chroviq";
    private const string Origin = "https://" + Host;

    public override FrameworkElement View { get; }

    /// <summary>The page was right-clicked: open the widget's menu. Set by the widget window.</summary>
    public Action? OpenMenu { get; set; }
    /// <summary>The page was pressed where nothing reacts to it: move the widget. Set by the widget window.</summary>
    public Action? StartDrag { get; set; }

    private readonly WidgetPlugin? _plugin;
    private readonly Grid _root = new();
    private WebView2CompositionControl? _web;
    private CoreWebView2? _core;
    private bool _disposed;
    private MediaWatcher? _media;
    private SystemStats? _stats;
    private GpuUsage? _gpu;
    private readonly List<FileSystemWatcher> _fileWatchers = new();
    private readonly Dictionary<string, DateTime> _lastRun = new();
    private readonly HashSet<string> _running = new();

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>One browser for all web widgets (one set of background processes), with its data in AppData.</summary>
    private static Task<CoreWebView2Environment> SharedEnvironment() => _environment ??= CoreWebView2Environment.CreateAsync(
        null, Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "WindowsCustomizer", "WebView2"));

    public WebWidgetContent(DesktopWidget widget, Color foreground, Color accent) : base(widget, foreground, accent)
    {
        View = _root;
        _plugin = WidgetPlugins.Find(widget.Plugin);
        if (_plugin == null)
        {
            Message(string.Format(Loc.T("This widget's folder can't be found ({0})."), widget.Plugin ?? "?"));
            return;
        }
        _ = StartAsync();
        OnDispose(() =>
        {
            _disposed = true;
            foreach (var w in _fileWatchers) w.Dispose();
            _media?.Dispose();
            if (_gpu is { } gpu) Task.Run(() => { lock (gpu) gpu.Dispose(); });
            _web?.Dispose();
        });
    }

    private void Message(string text)
    {
        _root.Children.Clear();
        _root.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        });
    }

    private async Task StartAsync()
    {
        var plugin = _plugin!;
        try
        {
            var environment = await SharedEnvironment();
            if (_disposed) return;
            _web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            _root.Children.Add(_web);
            await _web.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            var core = _core = _web.CoreWebView2;

            var s = core.Settings;
            s.AreDevToolsEnabled = Widget.DeveloperTools;
            s.AreDefaultContextMenusEnabled = true; // never shown: ContextMenuRequested opens the widget's menu instead
            s.AreDefaultScriptDialogsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = Widget.DeveloperTools; // F12, reload while making a widget
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = true;

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => OnRequest(environment, e);
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                if (e.IsUserInitiated) OpenInBrowser(e.Uri); // a link you clicked: in your browser, not in the widget
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.IsUserInitiated) OpenInBrowser(e.Uri);
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ContextMenuRequested += (_, e) =>
            {
                e.Handled = true;
                AppTrace.Log($"Widget {plugin.Name}: right-click (browser), menu");
                OpenMenu?.Invoke();
            };
            core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ApiScript);

            string entry = string.IsNullOrWhiteSpace(plugin.Manifest.Entry) ? "index.html" : plugin.Manifest.Entry.Trim().TrimStart('/', '\\');
            core.Navigate($"{Origin}/{Uri.EscapeDataString(entry).Replace("%2F", "/")}");
            StartData();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Message(Loc.T("Web widgets need Microsoft's WebView2 runtime (it comes with Windows 11). Install it from Microsoft's website."));
        }
        catch (Exception ex)
        {
            AppTrace.Log($"Widget {plugin.Name}: {ex}");
            Message(string.Format(Loc.T("This widget couldn't start: {0}"), ex.Message));
        }
    }

    private static void OpenInBrowser(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        try { ShellLaunch.Start(u.AbsoluteUri); } catch (Exception) { /* no browser */ }
    }

    // ---------- Requests: its own files, its allowed addresses, nothing else ----------

    private void OnRequest(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) { e.Response = Blocked(environment); return; }
        if (uri.Scheme is "data" or "blob") return;
        if (uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) && uri.Scheme == "https")
        {
            e.Response = ServeFile(environment, uri);
            return;
        }
        string origin = $"{uri.Scheme}://{uri.Authority}";
        if (_plugin!.NetworkOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return; // allowed in its widget.json
        AppTrace.Log($"Widget {_plugin.Name}: blocked {origin} (not in its widget.json)");
        e.Response = Blocked(environment);
    }

    private static CoreWebView2WebResourceResponse Blocked(CoreWebView2Environment environment) =>
        environment.CreateWebResourceResponse(null, 403, "Blocked by CHROVIQ", "");

    private CoreWebView2WebResourceResponse ServeFile(CoreWebView2Environment environment, Uri uri)
    {
        string folder = Path.GetFullPath(_plugin!.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(folder, relative));
        // Only inside its own folder (no "..\" tricks), and never CHROVIQ's own note of where it came from.
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
            Path.GetFileName(path).Equals(".chroviq-origin.json", StringComparison.OrdinalIgnoreCase))
            return environment.CreateWebResourceResponse(null, 404, "Not found", "");
        try
        {
            var bytes = File.ReadAllBytes(path);
            string headers = $"Content-Type: {ContentType(path)}\r\nCache-Control: no-cache\r\nContent-Security-Policy: {Policy()}";
            return environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", headers);
        }
        catch (Exception) { return environment.CreateWebResourceResponse(null, 500, "Can't read it", ""); }
    }

    /// <summary>What the page may load and connect to: its own files, and its allowed addresses.</summary>
    private string Policy()
    {
        string net = string.Join(" ", _plugin!.NetworkOrigins);
        string own = Origin;
        return string.Join("; ",
            "default-src 'none'",
            $"script-src {own} 'unsafe-inline' 'unsafe-eval'",
            $"style-src {own} 'unsafe-inline' {net}".TrimEnd(),
            $"img-src {own} data: blob: {net}".TrimEnd(),
            $"font-src {own} data: {net}".TrimEnd(),
            $"media-src {own} data: blob: {net}".TrimEnd(),
            net.Length > 0 ? $"connect-src {net}" : "connect-src 'none'",
            "frame-src 'none'", "object-src 'none'", "base-uri 'none'", "form-action 'none'", "worker-src 'none'");
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    // ---------- Talking with the page ----------

    private void Post(object message)
    {
        if (_disposed || _core == null) return;
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception) { /* the page is gone or reloading */ }
    }

    /// <summary>What the page asks for. It's the widget's own code, so nothing is trusted: everything is checked here.</summary>
    private void OnMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("action", out var a)) return;
            switch (a.GetString())
            {
                case "ready":
                    AppTrace.Log($"Widget {_plugin!.Name}: page ready");
                    SendTheme();
                    SendEverything();
                    break;
                case "drag":
                    if (!Widget.Locked) StartDrag?.Invoke();
                    break;
                case "menu":
                    AppTrace.Log($"Widget {_plugin!.Name}: right-click, menu");
                    OpenMenu?.Invoke();
                    break;
                case "media" when _media != null && m.TryGetProperty("command", out var c):
                    switch (c.GetString())
                    {
                        case "playpause": _media.PlayPause(); break;
                        case "next": _media.Next(); break;
                        case "previous": _media.Previous(); break;
                    }
                    break;
                case "run" when m.TryGetProperty("id", out var id) && id.GetString() is { } commandId:
                    if (_plugin!.Manifest.Commands.FirstOrDefault(x => x.Id == commandId) is { } command) RunCommand(command);
                    break;
            }
        }
        catch (JsonException) { /* not a message from the API */ }
    }

    private void SendTheme() => Post(new
    {
        type = "theme",
        text = Hex(Foreground), accent = Hex(Accent),
        font = Widget.FontFamily, fontSize = Widget.FontSize,
        locked = Widget.Locked,
    });

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---------- Data ----------

    private void StartData()
    {
        var plugin = _plugin!;
        if (plugin.Wants("stats"))
        {
            _stats = new SystemStats();
            _gpu = new GpuUsage();
            Every(TimeSpan.FromSeconds(1), () => _ = SendStatsAsync());
        }
        if (plugin.Wants("media"))
        {
            _media = new MediaWatcher();
            _media.Changed += () => View.Dispatcher.InvokeAsync(SendMedia);
            _ = _media.StartAsync();
        }
        if (plugin.Wants("weather"))
            Every(TimeSpan.FromMinutes(30), () => _ = SendWeatherAsync());
        WatchFiles();
        StartCommands();
    }

    private void SendEverything()
    {
        if (_stats != null) _ = SendStatsAsync();
        if (_media != null) SendMedia();
        if (_plugin!.Wants("weather")) _ = SendWeatherAsync();
        foreach (var file in _plugin.Manifest.Files) SendFile(file.Id);
        foreach (var command in _plugin.Manifest.Commands)
            if (!CommandsAllowed) Post(new { type = "command", id = command.Id, blocked = true, reason = CommandsBlockedReason });
    }

    private async Task SendStatsAsync()
    {
        if (_stats == null) return;
        int cpu = _stats.CpuPercent();
        var (memory, used, total) = SystemStats.Memory();
        var gpuReader = _gpu;
        int? gpu = gpuReader == null ? null : await Task.Run(() => { lock (gpuReader) return gpuReader.Read(); });
        Post(new { type = "stats", cpu, memory, memoryUsedGb = Math.Round(used, 1), memoryTotalGb = Math.Round(total, 1), gpu });
    }

    private void SendMedia()
    {
        var media = _media;
        if (media == null) return;
        string? cover = null;
        if (media.Cover is BitmapSource bitmap)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                cover = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
            catch (Exception) { /* no cover */ }
        }
        Post(new { type = "media", active = media.HasSession, playing = media.IsPlaying, title = media.Title, artist = media.Artist, cover });
    }

    private async Task SendWeatherAsync()
    {
        if (string.IsNullOrWhiteSpace(Widget.City)) { Post(new { type = "weather", error = Loc.T("No place chosen in the widget's settings.") }); return; }
        try
        {
            if (Widget.Latitude == null || Widget.Longitude == null)
            {
                if (await WeatherService.FindAsync(Widget.City) is not { } found) { Post(new { type = "weather", error = Loc.T("That place wasn't found.") }); return; }
                (Widget.Latitude, Widget.Longitude, Widget.Place) = (found.Lat, found.Lon, found.Name);
            }
            var report = await WeatherService.GetAsync(Widget.Latitude.Value, Widget.Longitude.Value, Widget.Fahrenheit);
            var (symbol, text) = WeatherService.Describe(report.Code);
            Post(new
            {
                type = "weather", place = Widget.Place ?? Widget.City, temperature = report.Temperature, code = report.Code,
                description = text, wind = report.Wind, unit = Widget.Fahrenheit ? "°F" : "°C",
                days = report.Days.Select(d => new { date = d.Date.ToString("yyyy-MM-dd"), code = d.Code, max = d.Max, min = d.Min, description = WeatherService.Describe(d.Code).Text }),
            });
        }
        catch (Exception ex) { Post(new { type = "weather", error = ex.Message }); }
    }

    // ---------- Files you chose for it ----------

    private string? ChosenFile(string id) =>
        Widget.PluginFiles?.TryGetValue(id, out var path) == true && !string.IsNullOrWhiteSpace(path) ? Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')) : null;

    private void WatchFiles()
    {
        foreach (var file in _plugin!.Manifest.Files)
        {
            if (ChosenFile(file.Id) is not { } path || Path.GetDirectoryName(path) is not { } dir || !Directory.Exists(dir)) continue;
            try
            {
                var watcher = new FileSystemWatcher(dir, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
                var id = file.Id;
                var soon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                soon.Tick += (_, _) => { soon.Stop(); SendFile(id); };
                void Changed(object? s, FileSystemEventArgs e) => View.Dispatcher.InvokeAsync(() => { soon.Stop(); soon.Start(); });
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Renamed += (s, e) => Changed(s, e);
                _fileWatchers.Add(watcher);
            }
            catch (Exception ex) { AppTrace.Log($"Widget {_plugin.Name}: can't watch {path}: {ex.Message}"); }
        }
    }

    private void SendFile(string id)
    {
        if (ChosenFile(id) is not { } path) { Post(new { type = "file", id, missing = true, reason = Loc.T("No file chosen in the widget's settings.") }); return; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int max = 1024 * 1024;
            var buffer = new byte[Math.Min(stream.Length, max)];
            int read = stream.Read(buffer, 0, buffer.Length);
            Post(new { type = "file", id, text = Encoding.UTF8.GetString(buffer, 0, read), modified = File.GetLastWriteTime(path).ToString("o"), cut = stream.Length > max });
        }
        catch (Exception ex) { Post(new { type = "file", id, missing = true, reason = ex.Message }); }
    }

    // ---------- Commands (only approved ones) ----------

    private bool CommandsAllowed => WidgetManager.CommandsAllowed && Widget.ApprovedCommands == _plugin!.CommandsFingerprint;

    private string CommandsBlockedReason => !WidgetManager.CommandsAllowed
        ? Loc.T("Commands are turned off for widgets.")
        : Loc.T("This widget's commands aren't approved (or they changed since).");

    private void StartCommands()
    {
        if (!CommandsAllowed) return;
        foreach (var command in _plugin!.Manifest.Commands)
        {
            RunCommand(command);
            if (command.EverySeconds > 0)
                Every(TimeSpan.FromSeconds(Math.Clamp(command.EverySeconds, 2, 3600)), () => RunCommand(command));
        }
    }

    private void RunCommand(WidgetCommand command)
    {
        if (!CommandsAllowed) { Post(new { type = "command", id = command.Id, blocked = true, reason = CommandsBlockedReason }); return; }
        // Not twice at once, and not more than once a second when the page asks.
        if (_running.Contains(command.Id)) return;
        if (_lastRun.TryGetValue(command.Id, out var last) && (DateTime.Now - last).TotalSeconds < 1) return;
        _lastRun[command.Id] = DateTime.Now;
        _running.Add(command.Id);
        string folder = _plugin!.Folder;
        Task.Run(() => Execute(command.Run, folder)).ContinueWith(t =>
        {
            _running.Remove(command.Id);
            var (output, error, exitCode) = t.Status == TaskStatus.RanToCompletion ? t.Result : ("", t.Exception?.InnerException?.Message ?? "?", -1);
            Post(new { type = "command", id = command.Id, output, error, exitCode });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static (string Output, string Error, int ExitCode) Execute(string commandLine, string folder)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{commandLine}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("It didn't start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* ended meanwhile */ }
            return ("", "It took longer than 15 seconds and was stopped.", -1);
        }
        static string Cut(string s) => s.Length > 64 * 1024 ? s[..(64 * 1024)] : s;
        return (Cut(output.Result), Cut(error.Result), process.ExitCode);
    }

    // ---------- The chroviq object in the page ----------

    private const string ApiScript = """
        (() => {
          if (window.chroviq) return;
          const handlers = {}, latest = {};
          const send = m => window.chrome.webview.postMessage(m);
          const keyOf = m => m.id !== undefined ? m.type + ':' + m.id : m.type;
          function applyTheme(t) {
            const s = document.documentElement.style;
            s.setProperty('--chroviq-text', t.text);
            s.setProperty('--chroviq-accent', t.accent);
            s.setProperty('--chroviq-font', `"${t.font}", "Segoe UI", sans-serif`);
            s.setProperty('--chroviq-font-size', t.fontSize + 'px');
          }
          window.chrome.webview.addEventListener('message', e => {
            const m = e.data;
            if (!m || typeof m.type !== 'string') return;
            latest[keyOf(m)] = m;
            if (m.type === 'theme') applyTheme(m);
            for (const f of handlers[m.type] || []) { try { f(m); } catch (err) { console.error(err); } }
          });
          // Pressing the widget where nothing reacts moves it (like the built-in widgets).
          const interactive = 'a,button,input,textarea,select,option,label,summary,[contenteditable],[data-no-drag]';
          window.addEventListener('mousedown', e => {
            if (e.button === 0 && !window.chroviq.noDrag && !(e.target instanceof Element && e.target.closest(interactive))) send({ action: 'drag' });
          }, true);
          // Right-click anywhere: the widget's own menu (settings, lock, remove), not the browser's.
          window.addEventListener('contextmenu', e => { e.preventDefault(); send({ action: 'menu' }); }, true);
          window.chroviq = {
            noDrag: false,
            on(type, f) {
              (handlers[type] ||= []).push(f);
              for (const [k, m] of Object.entries(latest)) if (m.type === type) { try { f(m); } catch (err) { console.error(err); } }
            },
            media: {
              playPause: () => send({ action: 'media', command: 'playpause' }),
              next: () => send({ action: 'media', command: 'next' }),
              previous: () => send({ action: 'media', command: 'previous' }),
            },
            run: id => send({ action: 'run', id: String(id) }),
            menu: () => send({ action: 'menu' }),
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => send({ action: 'ready' }));
          else send({ action: 'ready' });
        })();
        """;
}
