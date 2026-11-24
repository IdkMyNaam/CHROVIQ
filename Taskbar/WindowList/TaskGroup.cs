using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WindowsCustomizer;

/// <summary>All open windows of one app, shown as a single button with a count.</summary>
public sealed class TaskGroup : TaskItem
{
    public TaskGroup(string key) => Key = key;

    /// <summary>What the windows have in common: normally the program's path.</summary>
    public string Key { get; }

    /// <summary>The group's windows, in the order they appear in the window list.</summary>
    public List<TaskWindow> Windows { get; } = new();

    /// <summary>Copies title, icon, active state and count from the windows.</summary>
    internal void Update()
    {
        var first = Windows[0];
        Icon = Windows.Select(w => w.Icon).FirstOrDefault(i => i != null);
        IsActive = Windows.Any(w => w.IsActive);
        CountText = Windows.Count > 1 ? Windows.Count.ToString() : null;
        Title = Windows.Count == 1 ? first.Title : AppName(first.ProcessPath) ?? first.Title;
    }

    private static readonly Dictionary<string, string?> AppNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's friendly name ("Microsoft Edge"), from its file description.</summary>
    public static string? AppName(string? path)
    {
        if (path == null) return null;
        if (AppNames.TryGetValue(path, out var cached)) return cached;
        string? name = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = string.IsNullOrWhiteSpace(info.FileDescription) ? Path.GetFileNameWithoutExtension(path) : info.FileDescription;
        }
        catch (Exception)
        {
            name = Path.GetFileNameWithoutExtension(path);
        }
        AppNames[path] = name;
        return name;
    }

    /// <summary>
    /// Which windows belong together. Store apps all run inside ApplicationFrameHost.exe, so those
    /// (and windows whose program is unknown) are never grouped.
    /// </summary>
    public static string KeyOf(TaskWindow window) =>
        window.ProcessPath is { } path && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            ? path.ToLowerInvariant()
            : "window:" + window.Handle;
}

/// <summary>
/// Turns a list of windows into a list of buttons: one per app (grouped) or one per window.
/// Also leaves out windows that a pinned app already shows (dock style).
/// The output list is updated in place, so buttons keep their positions.
/// </summary>
public sealed class TaskItemList
{
    private readonly ObservableCollection<TaskWindow> _windows;
    private readonly bool _group;
    private readonly Func<TaskWindow, bool> _include;

    public ObservableCollection<TaskItem> Items { get; } = new();

    /// <summary>Raised after the list was updated (new windows, closed windows, changed titles/icons).</summary>
    public event Action? Updated;

    /// <param name="include">False for windows that shouldn't get a button here (e.g. shown by a pinned app).</param>
    public TaskItemList(ObservableCollection<TaskWindow> windows, bool group, Func<TaskWindow, bool> include)
    {
        _windows = windows;
        _group = group;
        _include = include;

        _windows.CollectionChanged += OnWindowsChanged;
        foreach (var w in _windows) w.PropertyChanged += OnWindowChanged;
        Rebuild();
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (TaskWindow w in e.NewItems) w.PropertyChanged += OnWindowChanged;
        if (e.OldItems != null) foreach (TaskWindow w in e.OldItems) w.PropertyChanged -= OnWindowChanged;
        Rebuild();
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only grouped buttons copy these from their windows; single windows are the buttons themselves.
        if (_group && e.PropertyName is nameof(TaskItem.Title) or nameof(TaskItem.Icon) or nameof(TaskItem.IsActive))
            Rebuild();
        else if (e.PropertyName == nameof(TaskItem.IsActive))
            Updated?.Invoke();
    }

    public void Rebuild()
    {
        var shown = _windows.Where(_include).ToList();
        if (!_group)
        {
            Sync(shown.Cast<TaskItem>().ToList());
        }
        else
        {
            var existing = Items.OfType<TaskGroup>().ToDictionary(g => g.Key);
            var groups = new List<TaskItem>();
            foreach (var window in shown)
            {
                var key = TaskGroup.KeyOf(window);
                var group = groups.OfType<TaskGroup>().FirstOrDefault(g => g.Key == key);
                if (group == null)
                {
                    group = existing.TryGetValue(key, out var old) ? old : new TaskGroup(key);
                    group.Windows.Clear();
                    groups.Add(group);
                }
                group.Windows.Add(window);
            }
            foreach (TaskGroup g in groups) g.Update();
            Sync(groups);
        }
        Updated?.Invoke();
    }

    /// <summary>Makes <see cref="Items"/> equal to <paramref name="wanted"/> with as few moves as possible.</summary>
    private void Sync(List<TaskItem> wanted)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Items[i])) Items.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Items.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) Items.Move(at, i);
            else Items.Insert(i, wanted[i]);
        }
    }

    public void Detach()
    {
        _windows.CollectionChanged -= OnWindowsChanged;
        foreach (var w in _windows) w.PropertyChanged -= OnWindowChanged;
    }
}
