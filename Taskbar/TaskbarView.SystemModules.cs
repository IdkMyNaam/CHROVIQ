using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsCustomizer.NativeMethods;

namespace WindowsCustomizer;

/// <summary>Modules that show or control parts of Windows: desktop, search, volume, CPU/memory, media.</summary>
public partial class TaskbarView
{
    /// <summary>Things to clean up when the bar closes (media session listener, ...).</summary>
    private readonly List<IDisposable> _disposables = new();

    // ---------- Show desktop / Windows search ----------

    /// <summary>A thin line, like the "show desktop" corner of the Windows taskbar.</summary>
    private FrameworkElement ShowDesktopModule()
    {
        var line = new Border { Width = 1, Height = Math.Max(12, BarHeight * 0.5), Opacity = 0.45 };
        line.SetResourceReference(Border.BackgroundProperty, "BarForeground");
        var button = BarButton(line, "Show desktop");
        button.Padding = new Thickness(7, 0, 7, 0);
        button.Click += (_, _) => ToggleDesktop();
        return button;
    }

    /// <summary>Minimizes all windows, or brings them back (Windows' own "show desktop").</summary>
    private static void ToggleDesktop()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            shell.ToggleDesktop();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception)
        {
            PressWinShortcut('D'); // same thing through the keyboard shortcut
        }
    }

    // ---------- CPU and memory ----------

    private FrameworkElement SystemStatsModule()
    {
        var stats = new SystemStats();
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var button = BarButton(text, "CPU and memory");
        button.Click += (_, _) => Launch("taskmgr.exe", null);

        void Update()
        {
            int cpu = stats.CpuPercent();
            var (memory, usedGb, totalGb) = SystemStats.Memory();
            text.Text = $"CPU {cpu}%   RAM {memory}%";
            button.ToolTip = $"Processor: {cpu}%\nMemory: {usedGb:0.0} of {totalGb:0.0} GB ({memory}%)\nClick for Task Manager";
        }
        Update();
        EverySecond += Update;
        return button;
    }

    // ---------- Volume ----------

    private FrameworkElement VolumeModule(TaskbarModule module)
    {
        var glyph = new TextBlock { FontFamily = (FontFamily)Resources["IconFont"], FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        if (module.ShowValue == true) content.Children.Add(value);

        var button = BarButton(content, "Volume");
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "Sound settings" };
        settings.Click += (_, _) => Launch("ms-settings:sound", null);
        menu.Items.Add(settings);
        button.ContextMenu = menu;

        void Update()
        {
            if (AudioVolume.Get() is not var (percent, muted))
            {
                glyph.Text = "";
                value.Text = "";
                button.ToolTip = "No audio device";
                return;
            }
            glyph.Text = muted || percent == 0 ? "" : ""; // mute / speaker
            value.Text = muted ? "muted" : $"{percent}%";
            button.ToolTip = (muted ? $"Volume {percent}% (muted)" : $"Volume {percent}%") +
                             "\nClick: mute   Mouse wheel: louder/quieter   Right-click: sound settings";
        }

        button.Click += (_, _) => { AudioVolume.ToggleMute(); Update(); };
        button.MouseWheel += (_, e) =>
        {
            if (AudioVolume.Get() is var (percent, _))
                AudioVolume.Set(percent + (e.Delta > 0 ? 2 : -2));
            Update();
            e.Handled = true;
        };
        Update();
        // Follow changes made elsewhere (keyboard keys, the Windows slider, other apps, new speakers): Windows tells us.
        // Several quick changes (dragging a slider) give one update.
        bool queued = false;
        Action onChanged = () =>
        {
            if (queued) return;
            queued = true;
            Dispatcher.InvokeAsync(() => { queued = false; Update(); }, System.Windows.Threading.DispatcherPriority.Background);
        };
        AudioVolume.Changed += onChanged;
        _disposables.Add(new Unsubscribe(() => AudioVolume.Changed -= onChanged));
        return button;
    }

    // ---------- Media ----------

    private FrameworkElement MediaModule(TaskbarModule module)
    {
        var watcher = new MediaWatcher();
        _disposables.Add(watcher);

        double coverSize = Math.Max(16, Math.Min(32, BarHeight - 12));
        var cover = new Image { Width = coverSize, Height = coverSize, Margin = new Thickness(0, 0, 8, 0), Stretch = Stretch.UniformToFill };
        cover.Clip = new RectangleGeometry(new Rect(0, 0, coverSize, coverSize), 4, 4);
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
        var artist = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Opacity = 0.75 };
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Clamp(module.Width ?? 240, 60, 800) };
        lines.Children.Add(title);
        lines.Children.Add(artist);

        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        info.Children.Add(cover);
        info.Children.Add(lines);

        var previous = GlyphButton("", "Previous", () => watcher.Previous());
        var playPause = GlyphButton("", "Play / pause", () => watcher.PlayPause());
        var next = GlyphButton("", "Next", () => watcher.Next());
        previous.Padding = playPause.Padding = next.Padding = new Thickness(6, 0, 6, 0);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        panel.Children.Add(info);
        panel.Children.Add(previous);
        panel.Children.Add(playPause);
        panel.Children.Add(next);

        void Update()
        {
            panel.Visibility = watcher.HasSession && (watcher.Title.Length > 0 || watcher.IsPlaying)
                ? Visibility.Visible : Visibility.Collapsed;
            title.Text = watcher.Title;
            artist.Text = watcher.Artist;
            artist.Visibility = watcher.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            cover.Source = watcher.Cover;
            cover.Visibility = watcher.Cover != null ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)playPause.Content).Text = watcher.IsPlaying ? "" : ""; // pause / play
            info.ToolTip = watcher.Artist.Length > 0 ? $"{watcher.Title}\n{watcher.Artist}" : watcher.Title;
        }

        // The media API reports from background threads.
        watcher.Changed += () => Dispatcher.InvokeAsync(Update);
        _ = watcher.StartAsync();
        return panel;
    }

    // ---------- Search box ----------

    /// <summary>Opens Windows Customizer's start menu next to an element; set by the main window.</summary>
    public static Action<FrameworkElement, TaskbarEdge>? StartMenuOpener { get; set; }

    /// <summary>The look of the search window; set by the main window from the settings.</summary>
    public static Func<SearchLook> SearchLookProvider { get; set; } = () => new SearchLook();

    /// <summary>True while the search window opened from this bar is open (auto-hide keeps the bar visible then).</summary>
    private bool _searchActive;

    /// <summary>
    /// A search box on the bar. Clicking it opens the search window right next to it (the same window
    /// the Windows key opens), so the search looks and works the same everywhere.
    /// </summary>
    private FrameworkElement SearchBoxModule(TaskbarModule module)
    {
        double width = Math.Clamp(module.Width ?? 220, 80, 800);
        string placeholderText = string.IsNullOrWhiteSpace(module.Text) ? "Search apps and the web" : module.Text!;

        var icon = new TextBlock
        {
            Text = "\uE721", // magnifier
            FontFamily = (FontFamily)Resources["IconFont"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Opacity = 0.8,
        };
        var label = new TextBlock { Text = placeholderText, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(icon);
        inner.Children.Add(label);

        var frame = new Border
        {
            Width = width,
            Height = Math.Max(22, BarHeight - 14),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(4, 0, 4, 0),
            Child = inner,
            Cursor = Cursors.IBeam,
            ToolTip = "Search apps and the web",
        };
        frame.SetResourceReference(Border.BackgroundProperty, "BarHover");
        frame.SetResourceReference(Border.CornerRadiusProperty, "ButtonCornerRadius");

        frame.MouseLeftButtonUp += (_, _) =>
        {
            var window = SearchWindow.Toggle(SearchLookProvider(), frame, Edge, module.SearchUrl);
            if (window == null) return;
            _searchActive = true;
            window.Closed += (_, _) => _searchActive = false;
        };
        return frame;
    }
}
