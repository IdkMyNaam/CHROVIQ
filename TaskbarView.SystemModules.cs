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
        EverySecond += Update; // follow changes made elsewhere (keyboard keys, other apps)
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

    /// <summary>True while the search box has the keyboard (auto-hide keeps the bar visible then).</summary>
    private bool _searchActive;

    /// <summary>A web search entry in the results list.</summary>
    private sealed record WebSearch(string Text)
    {
        public override string ToString() => $"Search the web for \"{Text}\"";
    }

    private FrameworkElement SearchBoxModule(TaskbarModule module)
    {
        double width = Math.Clamp(module.Width ?? 220, 80, 800);
        string placeholderText = string.IsNullOrWhiteSpace(module.Text) ? "Search apps and the web" : module.Text!;
        string searchUrl = string.IsNullOrWhiteSpace(module.SearchUrl) ? TaskbarModule.DefaultSearchUrl : module.SearchUrl!;

        var box = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
        };
        box.SetResourceReference(ForegroundProperty, "BarForeground");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "BarForeground");

        var placeholder = new TextBlock { Text = placeholderText, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)Resources["IconFont"],
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Opacity = 0.8,
        };
        DockPanel.SetDock(icon, Dock.Left);

        var field = new Grid();
        field.Children.Add(placeholder);
        field.Children.Add(box);
        var inner = new DockPanel();
        inner.Children.Add(icon);
        inner.Children.Add(field);

        var frame = new Border
        {
            Width = width,
            Height = Math.Max(22, BarHeight - 14),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(4, 0, 4, 0),
            Child = inner,
            Cursor = Cursors.IBeam,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "BarHover");
        frame.SetResourceReference(Border.CornerRadiusProperty, "ButtonCornerRadius");

        // Results: matching apps, then a web search.
        var results = new ListBox
        {
            MinWidth = Math.Max(width, 260),
            MaxHeight = 360,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
        };
        results.ItemTemplate = SearchResultTemplate();
        var popup = new Popup
        {
            PlacementTarget = frame,
            Placement = Edge == TaskbarEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
            VerticalOffset = Edge == TaskbarEdge.Top ? 6 : -6,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x24, 0x24, 0x24)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Child = results,
            },
        };

        List<AppEntry> apps = new();
        void UpdatePlaceholder() =>
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        void UpdateResults()
        {
            UpdatePlaceholder();
            var text = box.Text.Trim();
            if (text.Length == 0) { popup.IsOpen = false; return; }

            var items = new List<object>(AppSearch.Find(apps, text, 7)) { new WebSearch(text) };
            results.ItemsSource = items;
            results.SelectedIndex = 0;
            popup.IsOpen = true;
        }

        void Finish()
        {
            popup.IsOpen = false;
            box.Text = "";
            UpdatePlaceholder();
            SetKeyboardMode(false);
            _searchActive = false;
        }

        void Run(object? choice)
        {
            try
            {
                switch (choice)
                {
                    case AppEntry app:
                        app.Launch();
                        break;
                    case WebSearch web:
                        var query = Uri.EscapeDataString(web.Text);
                        var url = searchUrl.Contains("{0}") ? searchUrl.Replace("{0}", query) : searchUrl + query;
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open it:\n{ex.Message}", "Windows Customizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Finish();
        }

        // The bar normally never takes the keyboard (so clicking it doesn't steal focus from your app).
        // Clicking the search box is the exception: let the bar take the keyboard until you're done.
        frame.PreviewMouseLeftButtonDown += async (_, e) =>
        {
            if (_searchActive) return;
            _searchActive = true;
            SetKeyboardMode(true);
            box.Focus();
            Keyboard.Focus(box);
            e.Handled = true;
            apps = await AppSearch.Index(); // built in the background on first use
            UpdateResults();
        };

        box.TextChanged += (_, _) => UpdateResults();
        box.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down when results.Items.Count > 0:
                    results.SelectedIndex = Math.Min(results.SelectedIndex + 1, results.Items.Count - 1);
                    results.ScrollIntoView(results.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up when results.Items.Count > 0:
                    results.SelectedIndex = Math.Max(results.SelectedIndex - 1, 0);
                    results.ScrollIntoView(results.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (box.Text.Trim().Length > 0) Run(results.SelectedItem ?? new WebSearch(box.Text.Trim()));
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Finish();
                    e.Handled = true;
                    break;
            }
        };
        results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is { } item and (AppEntry or WebSearch))
                Run(item);
        };

        // Clicking somewhere else (another app, the desktop) ends the search.
        box.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject target && popup.Child.IsAncestorOf(target)) return;
            Dispatcher.InvokeAsync(() => { if (!box.IsKeyboardFocusWithin) Finish(); });
        };

        UpdatePlaceholder();
        return frame;
    }

    /// <summary>A result row: the app's icon (or a symbol) and its name.</summary>
    private DataTemplate SearchResultTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(MarginProperty, new Thickness(4, 3, 8, 3));

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Icon"));
        image.SetValue(WidthProperty, 20.0);
        image.SetValue(HeightProperty, 20.0);
        image.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
        row.AppendChild(image);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        name.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(name);

        return new DataTemplate { VisualTree = row };
    }

    /// <summary>
    /// Lets the bar's window take the keyboard (for the search box) or gives it back. The bar normally
    /// has WS_EX_NOACTIVATE, so clicking it never takes the focus away from the app you're using.
    /// </summary>
    private void SetKeyboardMode(bool on)
    {
        if (Window.GetWindow(this) is not { } host) return;
        var hwnd = new WindowInteropHelper(host).Handle;
        if (hwnd == IntPtr.Zero) return;

        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle = on ? exStyle & ~WS_EX_NOACTIVATE : exStyle | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        if (on)
        {
            SetForegroundWindow(hwnd); // allowed: the click on the box was ours
            host.Activate();
        }
    }
}
