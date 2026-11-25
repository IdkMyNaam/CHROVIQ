using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>What a widget shows (clock, stats, weather, media, notes), with its timers; disposed with the widget.</summary>
internal abstract class WidgetContent : IDisposable
{
    public abstract FrameworkElement View { get; }
    protected readonly DesktopWidget Widget;
    protected readonly Color Foreground, Accent;
    private readonly List<Action> _cleanup = new();

    protected WidgetContent(DesktopWidget widget, Color foreground, Color accent)
    {
        Widget = widget;
        Foreground = foreground;
        Accent = accent;
    }

    public static WidgetContent Create(DesktopWidget widget, Color foreground, Color accent, Action changed) => widget.Type switch
    {
        WidgetType.Clock => new ClockContent(widget, foreground, accent),
        WidgetType.Stats => new StatsContent(widget, foreground, accent),
        WidgetType.Weather => new WeatherContent(widget, foreground, accent, changed),
        WidgetType.Media => new MediaContent(widget, foreground, accent),
        _ => new NotesContent(widget, foreground, accent, changed),
    };

    protected DispatcherTimer Every(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();
        _cleanup.Add(timer.Stop);
        return timer;
    }

    protected void OnDispose(Action action) => _cleanup.Add(action);

    public void Dispose()
    {
        foreach (var action in _cleanup) action();
        _cleanup.Clear();
    }

    protected double Size(double factor) => Math.Max(8, Widget.FontSize * factor);

    protected TextBlock Text(string text = "", double factor = 1, bool bold = false, double opacity = 1) => new()
    {
        Text = text, FontSize = Size(factor), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Opacity = opacity,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    protected static string SafeFormat(DateTime time, string format)
    {
        try { return time.ToString(format, CultureInfo.CurrentCulture); }
        catch (FormatException) { return time.ToString("t", CultureInfo.CurrentCulture); }
    }
}

/// <summary>Big time, date under it.</summary>
internal sealed class ClockContent : WidgetContent
{
    public override FrameworkElement View { get; }

    public ClockContent(DesktopWidget widget, Color foreground, Color accent) : base(widget, foreground, accent)
    {
        var time = Text("", 3.4, bold: true);
        var date = Text("", 1.1, opacity: 0.8);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(time);
        if (!string.IsNullOrWhiteSpace(widget.DateFormat)) stack.Children.Add(date);
        View = new Viewbox { Child = stack, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };

        bool seconds = widget.TimeFormat.Contains('s');
        void Update()
        {
            var now = DateTime.Now;
            time.Text = SafeFormat(now, string.IsNullOrWhiteSpace(widget.TimeFormat) ? "t" : widget.TimeFormat);
            date.Text = SafeFormat(now, widget.DateFormat);
        }
        Update();
        // Without seconds: once a minute, right after it changes.
        DispatcherTimer? timer = null;
        TimeSpan Next() => seconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(60_050 - DateTime.Now.Second * 1000 - DateTime.Now.Millisecond);
        timer = Every(Next(), () => { Update(); timer!.Interval = Next(); });
    }
}

/// <summary>CPU, memory and GPU as numbers, each with a small graph of the last minute.</summary>
internal sealed class StatsContent : WidgetContent
{
    public override FrameworkElement View { get; }

    public StatsContent(DesktopWidget widget, Color foreground, Color accent) : base(widget, foreground, accent)
    {
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var stats = new SystemStats();
        GpuUsage? gpu = widget.ShowGpu ? new GpuUsage() : null;
        // Closed after a reading still running in the background has finished.
        if (gpu != null) OnDispose(() => Task.Run(() => { lock (gpu) gpu.Dispose(); }));
        var updates = new List<Action>();

        void Row(string name, Func<(int Percent, string Detail)?> read)
        {
            var label = Text(name, 0.85, opacity: 0.75);
            var value = Text("", 1.5, bold: true);
            var detail = Text("", 0.8, opacity: 0.65);
            var history = new Queue<int>();
            var graph = new Polyline { Stroke = new SolidColorBrush(accent), StrokeThickness = 1.5, Stretch = Stretch.None };
            var graphBox = new Border { Width = 90, Height = Size(1.8), ClipToBounds = true, Child = graph, VerticalAlignment = VerticalAlignment.Center };
            var texts = new StackPanel();
            texts.Children.Add(label);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(value);
            detail.VerticalAlignment = VerticalAlignment.Bottom;
            detail.Margin = new Thickness(6, 0, 0, 3);
            line.Children.Add(detail);
            texts.Children.Add(line);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (widget.Graphs)
            {
                DockPanel.SetDock(graphBox, Dock.Right);
                row.Children.Add(graphBox);
            }
            row.Children.Add(texts);
            rows.Children.Add(row);

            updates.Add(() =>
            {
                if (read() is not { } r) { value.Text = "n/a"; return; }
                value.Text = $"{r.Percent} %";
                detail.Text = r.Detail;
                history.Enqueue(r.Percent);
                while (history.Count > 60) history.Dequeue();
                if (!widget.Graphs) return;
                double w = graphBox.Width, h = graphBox.Height;
                var points = new PointCollection();
                int i = 60 - history.Count;
                foreach (var p in history) points.Add(new Point(w * i++ / 59.0, h - 1 - (h - 2) * p / 100.0));
                graph.Points = points;
            });
        }

        if (widget.ShowCpu) Row("CPU", () => (stats.CpuPercent(), ""));
        if (widget.ShowMemory) Row("Memory", () =>
        {
            var (percent, used, total) = SystemStats.Memory();
            return (percent, $"{used:0.0} / {total:0} GB");
        });
        // GPU: Windows' counters for it can take a moment to read, so that happens in the background;
        // the row shows the latest reading.
        int? gpuPercent = null;
        bool gpuBusy = false;
        if (gpu != null)
        {
            Row("GPU", () => gpuPercent is { } g ? (g, "") : null);
            Every(TimeSpan.FromSeconds(1), () =>
            {
                if (gpuBusy) return;
                gpuBusy = true;
                Task.Run(() => { try { gpuPercent = gpu.Read(); } finally { gpuBusy = false; } });
            });
        }
        if (rows.Children.Count == 0) rows.Children.Add(Text("Nothing chosen to show", 1, opacity: 0.7));

        View = new Viewbox { Child = rows, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        rows.Width = Math.Max(120, widget.Width - 28);
        foreach (var u in updates) u();
        Every(TimeSpan.FromSeconds(1), () => { foreach (var u in updates) u(); });
    }
}

/// <summary>Now: symbol, temperature, a few words, wind. Under it the coming days. Refreshed every 30 minutes.</summary>
internal sealed class WeatherContent : WidgetContent
{
    public override FrameworkElement View { get; }
    private readonly Action _changed;
    private readonly StackPanel _panel = new();
    private bool _disposed;

    public WeatherContent(DesktopWidget widget, Color foreground, Color accent, Action changed) : base(widget, foreground, accent)
    {
        _changed = changed;
        View = new Viewbox { Child = _panel, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _panel.Width = Math.Max(140, widget.Width - 28);
        OnDispose(() => _disposed = true);
        Show(string.IsNullOrWhiteSpace(widget.City) ? "Right-click > Widget settings to choose a place." : "Loading the weather...");
        _ = RefreshAsync();
        Every(TimeSpan.FromMinutes(30), () => _ = RefreshAsync());
    }

    private void Show(string message)
    {
        _panel.Children.Clear();
        _panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = Size(1) });
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(Widget.City)) return;
        try
        {
            // Look the place up once (again only when the name was changed).
            if (Widget.Latitude == null || Widget.Longitude == null)
            {
                if (await WeatherService.FindAsync(Widget.City) is not { } place) { if (!_disposed) Show($"\"{Widget.City}\" wasn't found."); return; }
                Widget.Latitude = place.Lat;
                Widget.Longitude = place.Lon;
                Widget.Place = place.Name;
                _changed();
            }
            var report = await WeatherService.GetAsync(Widget.Latitude.Value, Widget.Longitude!.Value, Widget.Fahrenheit);
            if (_disposed) return;
            Fill(report);
        }
        catch (Exception ex)
        {
            AppTrace.Log($"Weather widget: {ex.Message}");
            if (!_disposed && _panel.Children.Count <= 1) Show("No weather right now (no internet?). Trying again in 30 minutes.");
        }
    }

    private void Fill(WeatherService.Report report)
    {
        _panel.Children.Clear();
        string unit = Widget.Fahrenheit ? "°F" : "°C";
        var (symbol, words) = WeatherService.Describe(report.Code);
        _panel.Children.Add(Text(Widget.Place ?? Widget.City ?? "", 0.85, opacity: 0.75));
        var now = new StackPanel { Orientation = Orientation.Horizontal };
        now.Children.Add(Text(symbol, 2.6));
        var temp = Text($"{Math.Round(report.Temperature)}{unit}", 2.6, bold: true);
        temp.Margin = new Thickness(10, 0, 0, 0);
        now.Children.Add(temp);
        _panel.Children.Add(now);
        _panel.Children.Add(Text($"{words}, wind {Math.Round(report.Wind)} {(Widget.Fahrenheit ? "mph" : "km/h")}", 0.9, opacity: 0.8));

        int days = Math.Clamp(Widget.Days, 0, 6);
        if (days == 0) return;
        var grid = new UniformGrid { Columns = days, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var day in report.Days.Skip(1).Take(days))
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var name = Text(day.Date.ToString("ddd", CultureInfo.CurrentCulture), 0.8, opacity: 0.75);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            var sym = Text(WeatherService.Describe(day.Code).Symbol, 1.3);
            sym.HorizontalAlignment = HorizontalAlignment.Center;
            var range = Text($"{Math.Round(day.Max)}° {Math.Round(day.Min)}°", 0.8);
            range.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(name);
            cell.Children.Add(sym);
            cell.Children.Add(range);
            grid.Children.Add(cell);
        }
        _panel.Children.Add(grid);
    }
}

/// <summary>What's playing (any app that tells Windows), with cover, previous, play/pause and next.</summary>
internal sealed class MediaContent : WidgetContent
{
    public override FrameworkElement View { get; }

    public MediaContent(DesktopWidget widget, Color foreground, Color accent) : base(widget, foreground, accent)
    {
        var watcher = new MediaWatcher();
        OnDispose(watcher.Dispose);
        var cover = new Image { Width = Size(4.5), Height = Size(4.5), Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 12, 0) };
        RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);
        var coverBox = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = cover, VerticalAlignment = VerticalAlignment.Center };
        var title = Text("Nothing playing", 1.05, bold: true);
        var artist = Text("", 0.9, opacity: 0.75);
        var play = Button("", 1.6, watcher.PlayPause);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 4, 0, 0) };
        buttons.Children.Add(Button("", 1.2, watcher.Previous));
        buttons.Children.Add(play);
        buttons.Children.Add(Button("", 1.2, watcher.Next));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(artist);
        texts.Children.Add(buttons);
        var row = new DockPanel();
        DockPanel.SetDock(coverBox, Dock.Left);
        row.Children.Add(coverBox);
        row.Children.Add(texts);
        View = row;

        void Update()
        {
            bool has = watcher.HasSession;
            title.Text = has && watcher.Title.Length > 0 ? watcher.Title : "Nothing playing";
            artist.Text = has ? watcher.Artist : "";
            cover.Source = has ? watcher.Cover : null;
            coverBox.Visibility = cover.Source == null ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)play.Child).Text = watcher.IsPlaying ? "" : "";
            buttons.IsEnabled = has;
            buttons.Opacity = has ? 1 : 0.4;
        }
        watcher.Changed += () => View.Dispatcher.InvokeAsync(Update);
        Update();
        _ = watcher.StartAsync();
    }

    private Border Button(string glyph, double factor, Action onClick)
    {
        var text = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = Size(factor), VerticalAlignment = VerticalAlignment.Center };
        var button = new Border { Child = text, Padding = new Thickness(6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        var hover = new SolidColorBrush(Color.FromArgb(0x30, Foreground.R, Foreground.G, Foreground.B));
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        // Handled, so a click isn't also the start of dragging the widget.
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }
}

/// <summary>A sticky note to type in; saved a moment after you stop typing.</summary>
internal sealed class NotesContent : WidgetContent
{
    public override FrameworkElement View { get; }

    public NotesContent(DesktopWidget widget, Color foreground, Color accent, Action changed) : base(widget, foreground, accent)
    {
        var box = new TextBox
        {
            Text = widget.Text ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(foreground),
            CaretBrush = new SolidColorBrush(foreground), FontSize = Size(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var placeholder = Text("Type a note...", 1, opacity: 0.5);
        placeholder.IsHitTestVisible = false;
        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        save.Tick += (_, _) => { save.Stop(); widget.Text = box.Text; changed(); };
        box.TextChanged += (_, _) =>
        {
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            save.Stop();
            save.Start();
        };
        OnDispose(() => { if (save.IsEnabled) { save.Stop(); widget.Text = box.Text; changed(); } });
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(placeholder);
        View = grid;
    }
}
