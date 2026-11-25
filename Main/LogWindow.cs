using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>
/// "Logs": the app's log files (trace.log, shell.log, shell-launcher.log) in a window, newest at the bottom,
/// following new lines as they're written, with a filter box. "Report a problem..." collects the logs, the
/// settings and a few facts about this PC into one .zip on your desktop, to attach to a bug report.
/// </summary>
internal sealed class LogWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly string[] LogNames = { "trace.log", "shell.log", "shell-launcher.log", "trace.log.old" };
    private readonly ComboBox _file = new() { Width = 180, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _filter = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        AcceptsReturn = true,
    };
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _shownWrite;

    public LogWindow()
    {
        Width = 980;
        Height = 640;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _file.ItemsSource = LogNames.Where(n => File.Exists(PathOf(n))).ToList();
        _file.SelectedIndex = 0;
        _file.SelectionChanged += (_, _) => Load(force: true);
        _filter.TextChanged += (_, _) => Load(force: true);

        var report = new Wpf.Ui.Controls.Button { Content = "Report a problem...", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Bug24) };
        report.Click += (_, _) => Report(this);
        var folder = new Wpf.Ui.Controls.Button { Content = "Open folder", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.FolderOpen24), Margin = new Thickness(0, 0, 8, 0) };
        folder.Click += (_, _) => ShellLaunch.OpenFolder(AppSettings.Folder);
        var copy = new Wpf.Ui.Controls.Button { Content = "Copy", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Copy24), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch (COMException) { /* clipboard busy */ } };

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(report, Dock.Right);
        bar.Children.Add(report);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_file);
        left.Children.Add(new TextBlock { Text = "Show only lines with", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        left.Children.Add(_filter);
        left.Children.Add(copy);
        left.Children.Add(folder);
        bar.Children.Add(left);

        var body = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        body.Children.Add(_text);
        FluentLook.Apply(this, "Logs", body);

        _follow.Tick += (_, _) => Load(force: false);
        Loaded += (_, _) => { Load(force: true); _follow.Start(); };
        Closed += (_, _) => _follow.Stop();
    }

    private static string PathOf(string name) => Path.Combine(AppSettings.Folder, name);

    /// <summary>Shows the chosen log (the last 3000 lines, filtered); without <paramref name="force"/> only when it changed.</summary>
    private void Load(bool force)
    {
        if (_file.SelectedItem is not string name) { _text.Text = "There are no log files yet."; return; }
        var path = PathOf(name);
        var written = File.Exists(path) ? File.GetLastWriteTime(path) : default;
        if (!force && written == _shownWrite) return;
        _shownWrite = written;
        bool atBottom = _text.VerticalOffset + _text.ViewportHeight >= _text.ExtentHeight - 4;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r'));
            string filter = _filter.Text.Trim();
            if (filter.Length > 0) lines = lines.Where(l => l.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
            _text.Text = string.Join(Environment.NewLine, lines.TakeLast(3000));
        }
        catch (IOException ex) { _text.Text = $"Can't read {name}: {ex.Message}"; }
        if (force || atBottom) _text.ScrollToEnd(); // follow new lines, unless you scrolled up to read
    }

    // ---------- Report a problem ----------

    /// <summary>
    /// Puts the logs, settings.json and a short description of this PC in a .zip on the desktop, shows it in
    /// File Explorer, and explains what's in it (paths in it contain your user name; nothing is sent anywhere).
    /// </summary>
    public static void Report(Window owner)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string zipPath = Path.Combine(desktop, $"CHROVIQ report {DateTime.Now:yyyy-MM-dd HHmm}.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var name in LogNames.Append("settings.json"))
                {
                    var path = PathOf(name);
                    if (!File.Exists(path)) continue;
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = zip.CreateEntry(name).Open();
                    source.CopyTo(target);
                }
                using var info = new StreamWriter(zip.CreateEntry("about this PC.txt").Open());
                info.Write(AboutThisPc());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, $"The report could not be made: {ex.Message}", "Report a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start("explorer.exe", $"/select,\"{zipPath}\""); } catch (Exception) { /* it's on the desktop anyway */ }
        MessageBox.Show(owner,
            $"The report is on your desktop:\n{Path.GetFileName(zipPath)}\n\n" +
            "It has the logs, your settings and the Windows version, monitors and app version. Nothing is sent: attach it to your " +
            "bug report yourself, with a few words on what you did and what went wrong.\n\n" +
            "Folder paths in it contain your Windows user name; look through it first if that matters to you.",
            "Report a problem", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string AboutThisPc()
    {
        var text = new StringBuilder();
        text.AppendLine($"CHROVIQ {typeof(App).Assembly.GetName().Version}");
        text.AppendLine($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        text.AppendLine($"Running as the shell: {(ShellMode.IsShell ? "yes" : "no")}");
        text.AppendLine($"Made: {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine("Monitors:");
        foreach (var monitor in Monitors.All())
        {
            var b = monitor.Bounds;
            text.AppendLine($"  {monitor.Device}: {b.Right - b.Left}x{b.Bottom - b.Top} at {b.Left},{b.Top}{(monitor.IsPrimary ? " (main)" : "")}");
        }
        text.AppendLine($"Wallpaper Engine running: {(WallpaperEngine.IsRunning ? "yes" : "no")}");
        return text.ToString();
    }
}
