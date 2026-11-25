using System.Windows;
using System.Windows.Threading;

namespace WindowsCustomizer;

/// <summary>Opens, rebuilds and closes the desktop widgets from the settings; saves when one changes.</summary>
internal static class WidgetManager
{
    private static AppSettings? _settings;
    private static Action? _save;
    private static readonly List<WidgetWindow> Open = new();
    private static readonly Dictionary<string, WidgetSettingsWindow> SettingsWindows = new();
    private static readonly DispatcherTimer SaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public static void Start(AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        SaveTimer.Tick += (_, _) => { SaveTimer.Stop(); _save?.Invoke(); };
        Update();
    }

    /// <summary>Shows or hides all widgets to match "Show widgets".</summary>
    public static void Update()
    {
        if (_settings == null) return;
        CloseAll();
        if (!_settings.ShowWidgets) return;
        foreach (var widget in _settings.Widgets) OpenWidget(widget);
    }

    public static void CloseAll()
    {
        foreach (var window in Open.ToList()) window.Close();
        Open.Clear();
    }

    /// <summary>Adds a widget of this type near the top left of the main monitor's work area, and opens its settings.</summary>
    public static void Add(WidgetType type)
    {
        if (_settings == null) return;
        var widget = DesktopWidget.Create(type);
        var work = SystemParameters.WorkArea;
        int n = _settings.Widgets.Count;
        widget.Left = work.Left + 40 + 30 * (n % 8);
        widget.Top = work.Top + 40 + 30 * (n % 8);
        _settings.Widgets.Add(widget);
        _save?.Invoke();
        if (!_settings.ShowWidgets) { _settings.ShowWidgets = true; _save?.Invoke(); Update(); }
        else OpenWidget(widget);
        if (Open.FirstOrDefault(w => w.Widget == widget) is { } window) OpenSettings(window);
    }

    private static void OpenWidget(DesktopWidget widget)
    {
        var window = new WidgetWindow(widget, Changed, OpenSettings, Remove);
        window.Closed += (_, _) => Open.Remove(window);
        Open.Add(window);
        window.Show();
    }

    private static void Changed()
    {
        SaveTimer.Stop();
        SaveTimer.Start();
    }

    /// <summary>Draws a widget again with its new settings.</summary>
    private static void Rebuild(DesktopWidget widget)
    {
        if (Open.FirstOrDefault(w => w.Widget == widget) is { } old) old.Close();
        if (_settings?.ShowWidgets == true && _settings.Widgets.Contains(widget)) OpenWidget(widget);
    }

    private static void OpenSettings(WidgetWindow window)
    {
        var widget = window.Widget;
        if (SettingsWindows.TryGetValue(widget.Id, out var existing)) { existing.Activate(); return; }
        // Every change saves and redraws the widget a moment later, so you see it while you change it.
        var rebuild = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        rebuild.Tick += (_, _) => { rebuild.Stop(); Rebuild(widget); };
        var settings = new WidgetSettingsWindow(widget, () => { Changed(); rebuild.Stop(); rebuild.Start(); }, () => Rebuild(widget));
        SettingsWindows[widget.Id] = settings;
        settings.Closed += (_, _) => { SettingsWindows.Remove(widget.Id); if (rebuild.IsEnabled) { rebuild.Stop(); Rebuild(widget); } };
        settings.Show();
    }

    private static void Remove(WidgetWindow window)
    {
        if (_settings == null) return;
        _settings.Widgets.Remove(window.Widget);
        if (SettingsWindows.TryGetValue(window.Widget.Id, out var settings)) settings.Close();
        window.Close();
        _save?.Invoke();
    }
}
