using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WindowsCustomizer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Profile> _profiles;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GameWatcher _gameWatcher;

    // Saves the profiles shortly after the last edit, so typing doesn't write the file on every key.
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Set when profiles.json exists but could not be read: saving would overwrite it.
    private bool _savingDisabled;

    // True while the UI is filled from a profile, so change handlers don't write back.
    private bool _loading;

    private const string KeepCursors = "(don't change)";
    private readonly ObservableCollection<string> _cursorChoices = new();

    private static readonly string[] AccentPresets =
    {
        "#0078D4", "#0063B1", "#8764B8", "#E3008C", "#E81123", "#FF8C00",
        "#FFB900", "#107C10", "#00B7C3", "#2D7D9A", "#767676", "#4C4A48",
    };

    private TrayIcon _trayIcon = null!;
    private GlobalHotKey _hotKey = null!;

    // The profile applied last (check mark in the tray, starting point for "next").
    private Profile? _current;

    // Ignores hotkey/tray requests while a profile is still being applied (Wallpaper Engine can take seconds).
    private bool _applying;

    // False: the close button hides to the tray. True: really close (Exit in the tray menu, or Windows shutting down).
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        BuildAccentSwatches();
        LoadCursorChoices();

        string? loadWarning;
        try
        {
            _profiles = new ObservableCollection<Profile>(ProfileStore.Load(out loadWarning));
        }
        catch (Exception ex)
        {
            // For example: the file is locked or access is denied. Keep the file as it is.
            _profiles = new ObservableCollection<Profile>(ProfileStore.Defaults());
            _savingDisabled = true;
            loadWarning = $"Could not read profiles.json ({ex.Message}). Changes will not be saved this session.";
        }

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveProfiles();
        };

        WallpaperEngine.SavedExePath = _settings.WallpaperEngineExe;
        UpdateWallpaperEngineText();

        PauseForGamesCheck.IsChecked = _settings.PauseForGames;

        _loading = true; // setting the edge box raises SelectionChanged; don't treat that as a user change
        ShowTaskbarCheck.IsChecked = _settings.ShowTaskbar;
        TaskbarEdgeBox.SelectedIndex = (int)_settings.TaskbarEdge;
        TaskbarButtonsBox.SelectedIndex = (int)_settings.TaskbarButtons;
        _loading = false;
        GamesBox.Text = string.Join(", ", _settings.GameProcesses);

        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedIndex = 0;
        UpdateDeleteButton();

        if (loadWarning != null)
            StatusText.Text = loadWarning;
        if (AppSettings.LoadWarning != null)
            StatusText.Text = AppSettings.LoadWarning;

        _gameWatcher = new GameWatcher(_settings);
        _gameWatcher.StatusChanged += message => StatusText.Text = message;
        _gameWatcher.Start();

        _current = _profiles.FirstOrDefault(p => p.Name == _settings.LastAppliedProfile);
        SetUpTrayAndHotKey();
        WatchDisplayChanges();
        SetUpSearch();

        // Which tray icons stay on the bar (Ctrl+click an icon to change it); remembered in the settings.
        TrayHost.SavedPins = _settings.TrayPinned?.ToArray();
        TrayHost.PinsChanged = pins => { _settings.TrayPinned = pins.ToList(); SaveSettings(); };
        LoadDesigns();
        WatchDesignFiles();

        if (_settings.ShowTaskbar)
        {
            SetTaskbarVisible(true);
            if (_settings.HideWindowsTaskbar)
                SetWindowsTaskbarHidden(true);
        }
        UpdateHideWindowsTaskbarCheck();

        // Windows shutting down or signing out must not be blocked by "hide instead of close".
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            HideToTray();
        };

        Closed += (_, _) =>
        {
            SaveGameList(); // in case the games box still has focus
            _gameWatcher.Stop();
            _saveTimer.Stop();
            SaveProfiles(); // a pending save may not have run yet
            _hotKey.Dispose();
            // Bring the Windows taskbar back first, but keep the setting, so it is hidden again next start.
            WindowsTaskbar.Restore();
            CloseBars(); // releases the reserved screen space
            TrayHost.Shutdown(); // tray icons back to Explorer, even if a bar didn't get to clean up
            _winKeyHook?.Dispose(); // the Windows key opens Start again
            _trayIcon.Dispose();
        };
    }

    // ---------- Tray icon, hotkey, applying ----------

    private void SetUpTrayAndHotKey()
    {
        _hotKey = new GlobalHotKey(this, ModifierKeys.Control | ModifierKeys.Alt, Key.P);
        _hotKey.Pressed += () => _ = ApplyNextAsync();

        _trayIcon = new TrayIcon(BuildAppMenu);
        _trayIcon.Clicked += ShowFromTray;

        if (!_hotKey.IsRegistered && StatusText.Text.Length == 0)
            StatusText.Text = $"The hotkey {_hotKey} is already used by another program, so it is turned off.";
    }

    /// <summary>The app menu, shown by the tray icon and by the custom taskbar's menu button.</summary>
    private IEnumerable<AppMenuItem> BuildAppMenu()
    {
        foreach (var profile in _profiles.ToList())
        {
            var p = profile; // capture this profile, not the loop variable
            yield return new AppMenuItem(p.Name, () => _ = ApplyProfileAsync(p, notify: true),
                IsChecked: ReferenceEquals(p, _current));
        }

        yield return AppMenuItem.Separator;
        yield return new AppMenuItem("Show custom taskbar", () => SetTaskbarVisible(!TaskbarShown),
            IsChecked: TaskbarShown);
        yield return new AppMenuItem("Hide Windows taskbar", () => SetWindowsTaskbarHidden(!WindowsTaskbar.IsHiding),
            IsChecked: WindowsTaskbar.IsHiding, IsEnabled: TaskbarShown);
        yield return new AppMenuItem("Taskbar designer...", OpenDesigner);
        yield return new AppMenuItem("Next profile", () => _ = ApplyNextAsync(), Shortcut: _hotKey.ToString());
        yield return new AppMenuItem("Open", ShowFromTray);
        yield return new AppMenuItem("Exit", ExitApp);
    }

    private void HideToTray()
    {
        Hide();
        if (_settings.TrayHintShown) return;

        _trayIcon.ShowMessage("Windows Customizer is still running",
            $"Press {_hotKey} to switch profiles. Right-click the tray icon to exit.");
        _settings.TrayHintShown = true;
        SaveSettings();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Applies the profile after the last applied one, wrapping around at the end.</summary>
    private Task ApplyNextAsync()
    {
        if (_profiles.Count == 0) return Task.CompletedTask;
        int index = _current == null ? 0 : (_profiles.IndexOf(_current) + 1) % _profiles.Count;
        return ApplyProfileAsync(_profiles[index], notify: true);
    }

    /// <param name="notify">Show a tray message with the result (for the hotkey and tray menu, when the window may be hidden).</param>
    private async Task ApplyProfileAsync(Profile profile, bool notify)
    {
        if (_applying) return;
        _applying = true;
        ApplyButton.IsEnabled = false;
        StatusText.Text = $"Applying \"{profile.Name}\"...";
        try
        {
            await ThemeService.ApplyAsync(profile);

            // The profile's taskbar design (if it has one that still exists, and it isn't already in use).
            if (profile.TaskbarDesign is { } designName
                && _designs.Any(d => d.Name.Equals(designName, StringComparison.CurrentCultureIgnoreCase))
                && !designName.Equals(ActiveDesign().Name, StringComparison.CurrentCultureIgnoreCase))
                UseDesign(designName);

            _current = profile;
            _settings.LastAppliedProfile = profile.Name;
            SaveSettings();

            StatusText.Text = $"Applied \"{profile.Name}\".";
            if (notify) _trayIcon.ShowMessage("Profile applied", profile.Name);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
            if (notify) _trayIcon.ShowMessage($"Could not apply \"{profile.Name}\"", ex.Message);
        }
        finally
        {
            _applying = false;
            ApplyButton.IsEnabled = true;
        }
    }

    // ---------- Accent color ----------

    private void BuildAccentSwatches()
    {
        foreach (var hex in AccentPresets)
        {
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 4),
                Background = BrushFromHex(hex),
                BorderBrush = Brushes.Gray,
                ToolTip = hex,
                Cursor = Cursors.Hand,
            };
            swatch.Click += (_, _) => AccentBox.Text = hex; // TextChanged stores it in the profile
            AccentSwatches.Children.Add(swatch);
        }
    }

    private void AccentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = AccentBox.Text.Trim();
        AccentPreview.Background = BrushFromHex(text);

        if (_loading || Selected == null) return;
        Selected.AccentColor = text.Length == 0 ? null : text;
        ScheduleSave();
    }

    /// <summary>A brush for "#RRGGBB", or transparent if the text isn't a valid color.</summary>
    private static Brush BrushFromHex(string hex) =>
        AccentService.TryParse(hex, out var r, out var g, out var b)
            ? new SolidColorBrush(Color.FromRgb(r, g, b))
            : Brushes.Transparent;

    // ---------- Cursor scheme ----------

    private void LoadCursorChoices()
    {
        _cursorChoices.Add(KeepCursors);
        _cursorChoices.Add(CustomCursorsChoice);
        try
        {
            foreach (var name in CursorSchemes.List())
                _cursorChoices.Add(name);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not list cursor schemes: {ex.Message}";
        }
        CursorBox.ItemsSource = _cursorChoices;
    }

    private void ShowCursorChoice(Profile p)
    {
        if (p.UseCustomCursors)
        {
            CursorBox.SelectedItem = CustomCursorsChoice;
        }
        else if (string.IsNullOrEmpty(p.CursorScheme))
        {
            CursorBox.SelectedItem = KeepCursors;
        }
        else
        {
            // A scheme saved in the profile but no longer installed: still show it instead of hiding the setting.
            if (!_cursorChoices.Contains(p.CursorScheme))
                _cursorChoices.Add(p.CursorScheme);
            CursorBox.SelectedItem = p.CursorScheme;
        }
        UpdateCustomCursorPanel();
    }

    private void CursorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomCursorPanel();
        if (_loading || Selected == null || CursorBox.SelectedItem is not string choice) return;

        // Switching away from Custom keeps the custom set, so switching back restores it.
        Selected.UseCustomCursors = choice == CustomCursorsChoice;
        Selected.CursorScheme = choice == KeepCursors || choice == CustomCursorsChoice ? null : choice;
        ScheduleSave();
    }

    // ---------- Custom cursors ----------

    private const string CustomCursorsChoice = "Custom...";

    // Cursors loaded for the hover preview; disposed when the rows are rebuilt.
    private readonly List<Cursor> _previewCursors = new();

    private void UpdateCustomCursorPanel()
    {
        bool custom = (CursorBox.SelectedItem as string) == CustomCursorsChoice;
        CustomCursorPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom) BuildCustomCursorRows();
    }

    private void BuildCustomCursorRows()
    {
        foreach (var c in _previewCursors) c.Dispose();
        _previewCursors.Clear();
        CustomCursorRows.Children.Clear();
        if (Selected is not { } profile) return;

        foreach (var role in CustomCursors.Roles)
        {
            profile.CustomCursors.TryGetValue(role.Name, out var file);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent };
            row.ToolTip = file ?? "Windows default";

            var clear = new Button { Content = "✕", Width = 24, Margin = new Thickness(4, 0, 0, 0), IsEnabled = file != null, ToolTip = "Use the Windows default" };
            clear.Click += (_, _) => ChangeCustomCursor(() => CustomCursors.ClearRole(profile, role.Name));
            DockPanel.SetDock(clear, Dock.Right);

            var browse = new Button { Content = "Browse...", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0) };
            browse.Click += (_, _) =>
            {
                var path = PickFile($"Cursor for \"{role.Label}\"", "Cursors|*.cur;*.ani");
                if (path != null)
                    ChangeCustomCursor(() => CustomCursors.SetRole(profile, role.Name, path));
            };
            DockPanel.SetDock(browse, Dock.Right);

            var label = new TextBlock { Text = role.Label, Width = 140, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);

            var fileText = new TextBlock
            {
                Text = file != null ? System.IO.Path.GetFileName(file) : "(Windows default)",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            row.Children.Add(clear);
            row.Children.Add(browse);
            row.Children.Add(label);
            row.Children.Add(fileText);

            // Hover preview: show the actual cursor while the mouse is over this row.
            if (file != null && System.IO.File.Exists(file))
            {
                try
                {
                    var cursor = new Cursor(file);
                    _previewCursors.Add(cursor);
                    row.Cursor = cursor;
                }
                catch (Exception)
                {
                    row.ToolTip = $"{file} (cannot preview this file)";
                }
            }

            CustomCursorRows.Children.Add(row);
        }
    }

    /// <summary>Runs a change to the custom set, then refreshes the rows and saves. Errors go to the status line.</summary>
    private void ChangeCustomCursor(Action change)
    {
        try
        {
            change();
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cursor change failed: {ex.Message}";
        }
        BuildCustomCursorRows();
        ScheduleSave();
    }

    private void ImportCursorFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;

        var dialog = new OpenFolderDialog { Title = "Choose a cursor pack folder" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var (found, method) = CustomCursors.ImportFolder(profile, dialog.FolderName);
            var missing = CustomCursors.Roles.Where(r => !found.Contains(r.Name)).Select(r => r.Label).ToList();
            StatusText.Text = $"Imported {found.Count} of {CustomCursors.Roles.Count} cursors (matched by {method})."
                + (missing.Count > 0 ? $" Windows default for: {string.Join(", ", missing)}." : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
        BuildCustomCursorRows();
        ScheduleSave();
    }

    private void ClearCursors_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile || profile.CustomCursors.Count == 0) return;

        var answer = MessageBox.Show(this, $"Remove all custom cursors from \"{profile.Name}\"?", "Clear cursors",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            ChangeCustomCursor(() => CustomCursors.ClearAll(profile));
    }

    private Profile? Selected => ProfileList.SelectedItem as Profile;

    // ---------- Profiles ----------

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;

        _loading = true;
        NameBox.Text = p.Name;
        DarkCheck.IsChecked = p.DarkMode;
        AccentBox.Text = p.AccentColor ?? "";
        ShowCursorChoice(p); // also rebuilds the custom cursor rows for this profile
        ShowProfileDesign(p);
        KindBox.SelectedIndex = (int)p.Kind;
        ValueBox.Text = p.WallpaperValue ?? "";
        _loading = false;

        UpdateKindUi(p.Kind);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        _profiles.Add(new Profile { Name = NextFreeName() });
        ProfileList.SelectedIndex = _profiles.Count - 1;
        UpdateDeleteButton();
        ScheduleSave();

        NameBox.Focus();
        NameBox.SelectAll(); // ready to type a real name
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile || _profiles.Count <= 1) return;

        var answer = MessageBox.Show(this, $"Delete the profile \"{profile.Name}\"?", "Delete profile",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            CustomCursors.DeleteFolder(profile); // its copied cursor files are no longer needed
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not remove the profile's cursor files: {ex.Message}";
        }

        int index = ProfileList.SelectedIndex;
        _profiles.Remove(profile);
        if (ReferenceEquals(_current, profile)) _current = null; // "next" then starts at the top
        ProfileList.SelectedIndex = Math.Min(index, _profiles.Count - 1);
        UpdateDeleteButton();
        ScheduleSave();
    }

    /// <summary>The last profile can't be deleted, so the editor always has something to show.</summary>
    private void UpdateDeleteButton() => DeleteButton.IsEnabled = _profiles.Count > 1;

    /// <summary>"Profile 1", "Profile 2", ...: the first number not already used.</summary>
    private string NextFreeName()
    {
        for (int n = 1; ; n++)
        {
            var name = $"Profile {n}";
            if (!_profiles.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                return name;
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Selected == null) return;
        Selected.Name = NameBox.Text; // the list updates live through PropertyChanged
        ScheduleSave();
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Don't leave a profile without a visible name in the list.
        if (Selected != null && string.IsNullOrWhiteSpace(Selected.Name))
        {
            NameBox.Text = "Unnamed profile"; // TextChanged stores it
        }
    }

    private void DarkCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        Selected.DarkMode = DarkCheck.IsChecked == true;
        ScheduleSave();
    }

    // ---------- Saving profiles ----------

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveProfiles()
    {
        if (_savingDisabled) return;
        try
        {
            ProfileStore.Save(_profiles);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save profiles: {ex.Message}";
        }
    }

    // ---------- Wallpaper ----------

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedIndex < 0) return;
        var kind = (WallpaperKind)KindBox.SelectedIndex;
        UpdateKindUi(kind);

        if (_loading || Selected == null || Selected.Kind == kind) return;

        // The value means something different for each kind, so clear it.
        Selected.Kind = kind;
        Selected.WallpaperValue = null;
        _loading = true;
        ValueBox.Text = "";
        _loading = false;
        ScheduleSave();
    }

    private void UpdateKindUi(WallpaperKind kind)
    {
        ValueLabel.Text = kind switch
        {
            WallpaperKind.Image => "Image file",
            WallpaperKind.WallpaperEngineFile => "Wallpaper Engine project.json (or video file)",
            WallpaperKind.WallpaperEngineProfile => "Name of a profile saved in Wallpaper Engine",
            _ => "",
        };
        // A profile name is typed, there is nothing to browse for.
        BrowseButton.IsEnabled = kind != WallpaperKind.WallpaperEngineProfile;
    }

    private void ValueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Selected == null) return;
        Selected.WallpaperValue = ValueBox.Text;
        ScheduleSave();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;

        string? path = Selected.Kind switch
        {
            WallpaperKind.Image => PickFile("Choose a wallpaper", "Images|*.jpg;*.jpeg;*.png;*.bmp"),
            WallpaperKind.WallpaperEngineFile => PickWallpaperEngineWallpaper(),
            _ => null,
        };

        if (path != null)
            ValueBox.Text = path; // TextChanged stores it in the profile
    }

    private string? PickWallpaperEngineWallpaper()
    {
        var picker = new WorkshopPicker { Owner = this };
        return picker.ShowDialog() == true ? picker.SelectedPath : null;
    }

    private string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } profile)
            await ApplyProfileAsync(profile, notify: false);
    }

    // ---------- App settings ----------

    private void UpdateWallpaperEngineText()
    {
        WeExeText.Text = WallpaperEngine.FindExe() is { } exe
            ? $"Found: {exe}"
            : "Not found. Use \"Set path...\" to choose wallpaper64.exe.";
    }

    private void SetWallpaperEnginePath_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFile("Find wallpaper64.exe",
            "Wallpaper Engine|wallpaper64.exe;wallpaper32.exe|Programs|*.exe");
        if (path == null) return;

        _settings.WallpaperEngineExe = path;
        WallpaperEngine.SavedExePath = path;
        SaveSettings();
        UpdateWallpaperEngineText();
    }

    // ---------- Custom taskbar ----------

    // One bar per monitor while the custom taskbar is on.
    private readonly List<AppBarWindow> _appBars = new();
    private bool TaskbarShown => _appBars.Count > 0;

    // Monitor layout the bars were made for; compared after display changes.
    private string _monitorLayout = "";
    private DispatcherTimer? _displayChangeTimer;

    /// <summary>Shows or hides the custom taskbar and remembers the choice.</summary>
    private void SetTaskbarVisible(bool visible)
    {
        if (visible && !TaskbarShown)
        {
            OpenBars();
            visible = TaskbarShown;
        }
        else if (!visible && TaskbarShown)
        {
            // Never leave the user without any taskbar.
            if (WindowsTaskbar.IsHiding)
                SetWindowsTaskbarHidden(false);
            CloseBars();
        }

        ShowTaskbarCheck.IsChecked = visible;
        UpdateHideWindowsTaskbarCheck();
        if (_settings.ShowTaskbar != visible)
        {
            _settings.ShowTaskbar = visible;
            SaveSettings();
        }
    }

    /// <summary>Opens a bar on every connected monitor.</summary>
    private void OpenBars()
    {
        _monitorLayout = Monitors.Layout();
        bool onlyOwnMonitor = _settings.TaskbarButtons == TaskbarButtons.OwnMonitor;
        var errors = new List<string>();

        foreach (var monitor in Monitors.All())
        {
            var design = DesignFor(monitor);
            var edge = design.Edge ?? _settings.TaskbarEdge; // the design's own edge wins
            var bar = new AppBarWindow(monitor.Device, edge, onlyOwnMonitor, design)
            {
                GetMenuItems = BuildAppMenu,
            };
            try
            {
                bar.Show();
                _appBars.Add(bar);
            }
            catch (Exception ex)
            {
                bar.Close();
                errors.Add($"{monitor.Device}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
            StatusText.Text = $"Could not show the custom taskbar on {string.Join("; ", errors)}";
    }

    private void CloseBars()
    {
        foreach (var bar in _appBars)
            bar.Close(); // each bar gives its reserved screen space back
        _appBars.Clear();
    }

    /// <summary>
    /// Recreates the bars (new settings, or monitors added/removed). The Windows taskbar stays hidden
    /// meanwhile if it was, unless no bar could be opened at all.
    /// </summary>
    private void RebuildBars()
    {
        if (!TaskbarShown) return;
        CloseBars();
        OpenBars();
        if (!TaskbarShown && WindowsTaskbar.IsHiding)
            SetWindowsTaskbarHidden(false);
        ShowTaskbarCheck.IsChecked = TaskbarShown;
        UpdateHideWindowsTaskbarCheck();
    }

    /// <summary>Watches for monitors being added, removed or rearranged.</summary>
    private void WatchDisplayChanges()
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_DISPLAYCHANGE)
            {
                // Windows sends several of these while it rearranges; act once it has settled.
                _displayChangeTimer ??= CreateDisplayChangeTimer();
                _displayChangeTimer.Stop();
                _displayChangeTimer.Start();
            }
            return IntPtr.Zero;
        });
    }

    private DispatcherTimer CreateDisplayChangeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            BuildMonitorDesignBoxes(); // a monitor may have been added or removed
            if (TaskbarShown && Monitors.Layout() != _monitorLayout)
                RebuildBars();
        };
        return timer;
    }

    /// <summary>Hides or shows the real Windows taskbar. Hiding needs the custom taskbar to be showing.</summary>
    private void SetWindowsTaskbarHidden(bool hide)
    {
        if (hide && !TaskbarShown) hide = false;

        try
        {
            if (hide)
            {
                WindowsTaskbar.Hide(_settings.TaskbarAutoHideBefore);
                // Remember the user's own auto-hide setting, in case the app crashes before restoring it.
                _settings.TaskbarAutoHideBefore = WindowsTaskbar.OriginalAutoHide;
            }
            else
            {
                WindowsTaskbar.Restore();
                _settings.TaskbarAutoHideBefore = null;
            }
        }
        catch (Exception ex)
        {
            WindowsTaskbar.Restore();
            StatusText.Text = $"Could not hide the Windows taskbar: {ex.Message}";
            hide = false;
        }

        _settings.HideWindowsTaskbar = hide;
        SaveSettings();
        UpdateHideWindowsTaskbarCheck();
    }

    private void UpdateHideWindowsTaskbarCheck()
    {
        HideWindowsTaskbarCheck.IsEnabled = TaskbarShown;
        HideWindowsTaskbarCheck.IsChecked = WindowsTaskbar.IsHiding;
    }

    private void HideWindowsTaskbarCheck_Click(object sender, RoutedEventArgs e) =>
        SetWindowsTaskbarHidden(HideWindowsTaskbarCheck.IsChecked == true);

    private void ShowTaskbarCheck_Click(object sender, RoutedEventArgs e) =>
        SetTaskbarVisible(ShowTaskbarCheck.IsChecked == true);

    private void TaskbarEdgeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TaskbarEdgeBox.SelectedIndex < 0) return;
        var edge = (TaskbarEdge)TaskbarEdgeBox.SelectedIndex;
        if (edge == _settings.TaskbarEdge) return;

        _settings.TaskbarEdge = edge;
        SaveSettings();
        RebuildBars(); // the edge is fixed while a bar is registered
    }

    // ---------- Taskbar designs ----------

    private List<TaskbarDesign> _designs = new();
    private FileSystemWatcher? _designWatcher;
    private DispatcherTimer? _designReloadTimer;

    /// <summary>The design chosen in the settings, or the default one if that file is gone or broken.</summary>
    private TaskbarDesign ActiveDesign() =>
        _designs.FirstOrDefault(d => d.Name.Equals(_settings.TaskbarDesign, StringComparison.CurrentCultureIgnoreCase))
        ?? _designs.FirstOrDefault(d => d.Name == TaskbarDesigns.DefaultName)
        ?? _designs.FirstOrDefault()
        ?? TaskbarDesign.Classic();

    /// <summary>Reads all design files and fills the design dropdown. Problems go to the status line.</summary>
    private void LoadDesigns()
    {
        try
        {
            var (designs, errors) = TaskbarDesigns.LoadAll();
            _designs = designs;
            if (errors.Count > 0)
                StatusText.Text = "Some taskbar designs could not be read: " + string.Join(" ", errors);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load taskbar designs: {ex.Message}";
        }

        _loading = true; // setting the selection raises SelectionChanged
        TaskbarDesignBox.ItemsSource = _designs.Select(d => d.Name).ToList();
        TaskbarDesignBox.SelectedItem = ActiveDesign().Name;
        _loading = false;
        BuildMonitorDesignBoxes();
        if (Selected is { } profile) ShowProfileDesign(profile); // the list of designs may have changed
    }

    private const string KeepDesign = "(don't change)";

    /// <summary>Fills the profile's "Taskbar design" box. A design that no longer exists is still shown, so it isn't lost silently.</summary>
    private void ShowProfileDesign(Profile p)
    {
        bool wasLoading = _loading;
        _loading = true;
        var choices = new List<string> { KeepDesign };
        choices.AddRange(_designs.Select(d => d.Name));
        if (p.TaskbarDesign != null && !choices.Contains(p.TaskbarDesign, StringComparer.CurrentCultureIgnoreCase))
            choices.Add(p.TaskbarDesign);
        ProfileDesignBox.ItemsSource = choices;
        ProfileDesignBox.SelectedItem = choices.FirstOrDefault(c => c.Equals(p.TaskbarDesign ?? KeepDesign, StringComparison.CurrentCultureIgnoreCase));
        _loading = wasLoading;
    }

    private void ProfileDesignBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected == null || ProfileDesignBox.SelectedItem is not string choice) return;
        Selected.TaskbarDesign = choice == KeepDesign ? null : choice;
        ScheduleSave();
    }

    private const string SameAsMain = "(same as main)";

    /// <summary>The design for a monitor: its own choice if it has one that still exists, else the main design.</summary>
    private TaskbarDesign DesignFor(MonitorInfo monitor)
    {
        if (!monitor.IsPrimary
            && _settings.MonitorDesigns.TryGetValue(monitor.Device, out var name)
            && _designs.FirstOrDefault(d => d.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) is { } own)
            return own;
        return ActiveDesign();
    }

    /// <summary>One "Monitor N" dropdown per extra monitor (none with a single monitor).</summary>
    private void BuildMonitorDesignBoxes()
    {
        MonitorDesignsPanel.Children.Clear();
        var monitors = Monitors.All();
        int number = 1;
        foreach (var monitor in monitors)
        {
            number++;
            if (monitor.IsPrimary) { number--; continue; }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = $"Monitor {number}",
                Width = 70,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                ToolTip = monitor.Device,
            });

            var box = new ComboBox { Width = 150 };
            var choices = new List<string> { SameAsMain };
            choices.AddRange(_designs.Select(d => d.Name));
            box.ItemsSource = choices;
            box.SelectedItem = _settings.MonitorDesigns.TryGetValue(monitor.Device, out var chosen) && choices.Contains(chosen)
                ? chosen
                : SameAsMain;

            var device = monitor.Device;
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is not string name) return;
                if (name == SameAsMain) _settings.MonitorDesigns.Remove(device);
                else _settings.MonitorDesigns[device] = name;
                SaveSettings();
                RebuildBars();
            };
            row.Children.Add(box);
            MonitorDesignsPanel.Children.Add(row);
        }
    }

    private void TaskbarDesignBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TaskbarDesignBox.SelectedItem is not string name) return;
        if (name == _settings.TaskbarDesign) return;

        _settings.TaskbarDesign = name;
        SaveSettings();
        RebuildBars();
    }

    private TaskbarDesignerWindow? _designer;

    private void OpenDesigner_Click(object sender, RoutedEventArgs e) => OpenDesigner();

    /// <summary>Opens the taskbar designer, or brings it to the front if it is already open.</summary>
    private void OpenDesigner()
    {
        if (_designer != null)
        {
            if (_designer.WindowState == WindowState.Minimized) _designer.WindowState = WindowState.Normal;
            _designer.Activate();
            return;
        }

        _designer = new TaskbarDesignerWindow(() => ActiveDesign().Name, UseDesign);
        _designer.Closed += (_, _) => _designer = null;
        _designer.Show();
    }

    /// <summary>Makes <paramref name="name"/> the design the taskbar uses (called by the designer).</summary>
    private void UseDesign(string name)
    {
        _settings.TaskbarDesign = name;
        SaveSettings();
        LoadDesigns();  // also selects it in the dropdown
        RebuildBars();
    }

    private void OpenDesignsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(TaskbarDesigns.Folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{TaskbarDesigns.Folder}\"")?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the designs folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Reloads designs when a file in the designs folder is saved, added, renamed or deleted,
    /// so hand edits show up on the taskbar right away.
    /// </summary>
    private void WatchDesignFiles()
    {
        try
        {
            System.IO.Directory.CreateDirectory(TaskbarDesigns.Folder);
            _designWatcher = new FileSystemWatcher(TaskbarDesigns.Folder, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Design files won't reload automatically: {ex.Message}";
            return;
        }

        _designReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _designReloadTimer.Tick += (_, _) =>
        {
            _designReloadTimer.Stop();
            var before = System.Text.Json.JsonSerializer.Serialize(ActiveDesign());
            LoadDesigns();
            // Only rebuild when the design in use actually changed.
            if (System.Text.Json.JsonSerializer.Serialize(ActiveDesign()) != before)
                RebuildBars();
        };

        // Watcher events come from a background thread, often several per save (editors write in steps).
        void Changed(object? s, EventArgs e) => Dispatcher.InvokeAsync(() =>
        {
            _designReloadTimer.Stop();
            _designReloadTimer.Start();
        });
        _designWatcher.Changed += Changed;
        _designWatcher.Created += Changed;
        _designWatcher.Deleted += Changed;
        _designWatcher.Renamed += Changed;
        Closed += (_, _) => _designWatcher.Dispose();
    }

    private void TaskbarButtonsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TaskbarButtonsBox.SelectedIndex < 0) return;
        var mode = (TaskbarButtons)TaskbarButtonsBox.SelectedIndex;
        if (mode == _settings.TaskbarButtons) return;

        _settings.TaskbarButtons = mode;
        SaveSettings();
        RebuildBars();
    }

    // ---------- Search ----------

    private WinKeyHook? _winKeyHook;
    private SearchSettingsWindow? _searchSettings;

    private void SetUpSearch()
    {
        // Search boxes on the bars open the search window with these settings.
        TaskbarView.SearchLookProvider = () => _settings.Search ??= new SearchLook();
        WinKeySearchCheck.IsChecked = _settings.WinKeyOpensSearch;
        if (_settings.WinKeyOpensSearch) SetWinKeySearch(true);
    }

    /// <summary>Turns "Windows key opens our search" on or off, and remembers the choice.</summary>
    private void SetWinKeySearch(bool on)
    {
        if (on && _winKeyHook == null)
        {
            try
            {
                _winKeyHook = new WinKeyHook();
                // Raised inside the keyboard hook: open the window a moment later, so the hook returns at once.
                _winKeyHook.Pressed += () => Dispatcher.InvokeAsync(() => SearchWindow.Toggle(_settings.Search ??= new SearchLook()));
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not take over the Windows key: {ex.Message}";
                on = false;
            }
        }
        else if (!on && _winKeyHook != null)
        {
            _winKeyHook.Dispose();
            _winKeyHook = null;
        }

        WinKeySearchCheck.IsChecked = on;
        if (_settings.WinKeyOpensSearch != on)
        {
            _settings.WinKeyOpensSearch = on;
            SaveSettings();
        }
    }

    private void WinKeySearchCheck_Click(object sender, RoutedEventArgs e) =>
        SetWinKeySearch(WinKeySearchCheck.IsChecked == true);

    private void SearchSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_searchSettings != null)
        {
            _searchSettings.Activate();
            return;
        }
        _searchSettings = new SearchSettingsWindow(_settings.Search ??= new SearchLook(), SaveSettings);
        _searchSettings.Closed += (_, _) => _searchSettings = null;
        _searchSettings.Show();
    }

    private void PauseForGamesCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.PauseForGames = PauseForGamesCheck.IsChecked == true;
        SaveSettings();
    }

    private void GamesBox_LostFocus(object sender, RoutedEventArgs e) => SaveGameList();

    private void SaveGameList()
    {
        var games = GamesBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (games.SequenceEqual(_settings.GameProcesses)) return;

        _settings.GameProcesses = games;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save settings: {ex.Message}";
        }
    }
}
