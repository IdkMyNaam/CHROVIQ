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

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ObservableCollection<Profile> _profiles;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GameWatcher _gameWatcher;
    private readonly ProfileSwitcher _profileSwitcher;

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
        // Before any bar or window with a material is made: what glass materials show.
        WallpaperBehind.Live = _settings.LiveGlass;
        // The app theme (how this app's own windows look); "Windows" follows Windows' light/dark.
        AppThemes.Apply(AppThemes.Find(_settings.AppTheme));
        InitializeComponent();
        AppThemes.Register(this);
        SetUpAppearance();
        // While this window is open (on screen: not minimized, not in the tray), auto-hiding taskbars show,
        // so you see what you change. Can be turned off on the Taskbar page.
        IsVisibleChanged += (_, _) => UpdateKeepTaskbarShown();
        StateChanged += (_, _) => UpdateKeepTaskbarShown();
        NavList.SelectedIndex = 0;
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}";
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

        MaterialClock.FramesPerSecond = _settings.MaterialFps;
        _settings.Menus?.Apply(); // the desktop menus' material
        ShowTaskbarWhileOpenCheck.IsChecked = _settings.ShowTaskbarWhileOpen;
        LiveGlassCheck.IsChecked = _settings.LiveGlass;
        _loading = true; // setting the edge box raises SelectionChanged; don't treat that as a user change
        MaterialFpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(FpsChoices, _settings.MaterialFps) is var i && i >= 0 ? i : 1);
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

        // Automatic profile switching (time and app rules).
        _profileSwitcher = new ProfileSwitcher(_settings, async name =>
        {
            if (_profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) is { } profile)
                await ApplyProfileAsync(profile, notify: true);
        });
        AutoSwitchCheck.IsChecked = _settings.AutoSwitch;
        UpdateAutoSwitchText();
        // Once everything is set up (designs loaded): a time rule may apply a profile right away.
        Dispatcher.BeginInvoke(_profileSwitcher.Update, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // The very first start: a few steps to set up the basics (not as the shell: that's set up already).
        if (AppSettings.IsFirstRun && !ShellMode.IsShell)
            Dispatcher.BeginInvoke(RunSetupWizard, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        _current = _profiles.FirstOrDefault(p => p.Name == _settings.LastAppliedProfile);
        SetUpTrayAndHotKey();
        WatchDisplayChanges();
        SetUpSearch();
        UpdateShellStatus();

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

        if (ShellMode.IsShell) StartShellMode();
        AppTrace.WatchForFreezes();
        UpdateAltTab();

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
            _profileSwitcher.Stop();
            _saveTimer.Stop();
            SaveProfiles(); // a pending save may not have run yet
            _hotKey.Dispose();
            // Bring the Windows taskbar back first, but keep the setting, so it is hidden again next start.
            WindowsTaskbar.Restore();
            CloseBars(); // releases the reserved screen space
            TrayHost.Shutdown(); // tray icons back to Explorer, even if a bar didn't get to clean up
            _winKeyHook?.Dispose(); // the Windows key opens Start again
            _altTab?.Dispose();     // Windows' own Alt+Tab again
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
        yield return new AppMenuItem("Start menu", OpenStartMenu);
        if (!ShellMode.IsShell) yield return new AppMenuItem("Desktop preview", OpenDesktopPreview);
        DesktopPreviewButton.IsEnabled = !ShellMode.IsShell;
        yield return new AppMenuItem("Own Alt+Tab switcher", () => { _settings.OwnAltTab = !_settings.OwnAltTab; SaveSettings(); UpdateAltTab(); },
            IsChecked: _altTab != null, IsEnabled: !ShellMode.IsShell); // as the shell it's always on (Windows' own needs Explorer)
        yield return new AppMenuItem("Next profile", () => _ = ApplyNextAsync(), Shortcut: _hotKey.ToString());
        yield return new AppMenuItem("Open", ShowFromTray);
        yield return new AppMenuItem("Exit", ExitApp);
    }

    // ---------- Sidebar and the small buttons of the new layout ----------

    /// <summary>Shows the page picked in the sidebar.</summary>
    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageProfiles == null) return; // during InitializeComponent
        var pages = new FrameworkElement[] { PageProfiles, PageAppearance, PageTaskbar, PageStart, PageDesktop, PageWallpaper, PageAbout };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == NavList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKeepTaskbarShown() =>
        AppBarWindow.KeepShown = _settings.ShowTaskbarWhileOpen && IsVisible && WindowState != WindowState.Minimized;

    private void ShowTaskbarWhileOpenCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowTaskbarWhileOpen = ShowTaskbarWhileOpenCheck.IsChecked == true;
        SaveSettings();
        UpdateKeepTaskbarShown();
    }

    private void LiveGlassCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.LiveGlass = LiveGlassCheck.IsChecked == true;
        SaveSettings();
        WallpaperBehind.Live = _settings.LiveGlass;
        if (!_settings.LiveGlass) ScreenBehind.StopAll(); // the bars show in screenshots and screen sharing again
        RebuildBars(); // the bars pick up the new glass source
        StatusText.Text = _settings.LiveGlass
            ? "Live glass on: glass materials on the taskbar show what's behind them; those bars don't show in screen sharing."
            : "Live glass off: glass shows the wallpaper picture, and the bars show in screenshots and screen sharing again.";
    }

    private static readonly int[] FpsChoices = { 15, 30, 60 };

    private void MaterialFpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MaterialFpsBox.SelectedIndex < 0) return;
        _settings.MaterialFps = FpsChoices[MaterialFpsBox.SelectedIndex];
        MaterialClock.FramesPerSecond = _settings.MaterialFps;
        SaveSettings();
    }

    private void AltTabCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.OwnAltTab = AltTabCheck.IsChecked == true;
        SaveSettings();
        UpdateAltTab();
    }

    private void OpenDesktopPreview_Click(object sender, RoutedEventArgs e) => OpenDesktopPreview();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => ShellLaunch.OpenFolder(AppSettings.Folder);

    private void OpenAppFolder_Click(object sender, RoutedEventArgs e) => ShellLaunch.OpenFolder(AppContext.BaseDirectory);

    private void RunSetupWizard_Click(object sender, RoutedEventArgs e) => RunSetupWizard();

    /// <summary>Shows the setup wizard; Finish applies its choices with the same code as the settings pages.</summary>
    private void RunSetupWizard()
    {
        var wizard = new SetupWizard(_settings.TaskbarEdge);
        if (IsVisible) wizard.Owner = this;
        bool finished = wizard.ShowDialog() == true;
        if (!finished)
        {
            AppThemes.Apply(AppThemes.Find(_settings.AppTheme)); // the look tried in step 1 goes back
            SaveSettings(); // so it doesn't show again next start
            return;
        }
        var c = wizard.Choices;

        // App look: through the theme list, which uses it and remembers it.
        if (ThemeList.Items.OfType<ThemeItem>().FirstOrDefault(t => t.Theme.Name == c.AppTheme) is { } theme) ThemeList.SelectedItem = theme;
        else { AppThemes.Apply(AppThemes.Find(c.AppTheme)); _settings.AppTheme = c.AppTheme; }

        // Taskbar.
        if (c.TaskbarDesign is { } design)
        {
            _settings.TaskbarEdge = c.Edge;
            _loading = true;
            TaskbarEdgeBox.SelectedIndex = (int)c.Edge;
            _loading = false;
            _settings.TaskbarDesign = design;
            SaveSettings();
            LoadDesigns();
            if (TaskbarShown) RebuildBars(); else SetTaskbarVisible(true);
            if (c.HideWindowsTaskbar != WindowsTaskbar.IsHiding) SetWindowsTaskbarHidden(c.HideWindowsTaskbar);
        }
        else if (TaskbarShown) SetTaskbarVisible(false);

        // Windows key and Alt+Tab.
        SetWinKey(c.WinKey);
        AltTabCheck.IsChecked = c.OwnAltTab;
        _settings.OwnAltTab = c.OwnAltTab;
        SaveSettings();
        UpdateAltTab();
        StatusText.Text = "All set. Everything can be changed on these pages; the About page can run the wizard again.";
    }

    private void BackUpSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();   // what's on screen is in the backup
        SaveProfiles();
        if (SettingsBackup.BackUp(this) is { } status) StatusText.Text = status;
    }

    private void RestoreSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();   // so the automatic "before restore" backup has the latest
        SaveProfiles();
        if (SettingsBackup.Restore(this, out bool restart) is not { } status) return;
        StatusText.Text = status;
        if (!restart) return;
        SettingsBackup.Restart(); // the new copy waits for this one to close, then restores
        ExitApp();
    }

    private AltTabSwitcher? _altTab;

    /// <summary>Turns our Alt+Tab switcher on or off to match the setting (always on as the shell).</summary>
    private void UpdateAltTab()
    {
        bool wanted = _settings.OwnAltTab || ShellMode.IsShell;
        if (wanted && _altTab == null)
        {
            try { _altTab = new AltTabSwitcher(() => _settings.AltTab ??= new AltTabLook()); }
            catch (Exception ex) { StatusText.Text = $"Alt+Tab switcher: {ex.Message}"; }
        }
        else if (!wanted && _altTab != null)
        {
            _altTab.Dispose(); // Windows' own Alt+Tab again
            _altTab = null;
        }
        AltTabCheck.IsChecked = _altTab != null;
        AltTabCheck.IsEnabled = !ShellMode.IsShell; // as the shell it's always on (Windows' own needs Explorer)
    }

    private DesktopWindow? _desktopPreview;

    /// <summary>The shell's desktop in a normal window, to try it without being the shell (your real Desktop folder).</summary>
    private void OpenDesktopPreview()
    {
        if (_desktopPreview != null) { _desktopPreview.Activate(); return; }
        var primary = Monitors.All().FirstOrDefault(m => m.IsPrimary) ?? Monitors.All().First();
        _desktopPreview = new DesktopWindow(primary, showIcons: true, preview: true) { OpenCustomizer = ShowFromTray };
        _desktopPreview.Closed += (_, _) => _desktopPreview = null;
        _desktopPreview.Show();
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
        SafeUi.Clear(CustomCursorRows);
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

    private void AutoSwitchCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoSwitch = AutoSwitchCheck.IsChecked == true;
        SaveSettings();
        if (_settings.AutoSwitch && _settings.AutoSwitchRules.Count == 0) AutoSwitchRules_Click(sender, e); // nothing to follow yet
        UpdateAutoSwitchText();
        _profileSwitcher.Update();
    }

    private void AutoSwitchRules_Click(object sender, RoutedEventArgs e)
    {
        var window = new AutoSwitchWindow(_settings.AutoSwitchRules, _profiles.Select(p => p.Name).ToList(), SaveSettings) { Owner = this };
        window.ShowDialog();
        UpdateAutoSwitchText();
        _profileSwitcher.Update();
    }

    /// <summary>The card's text says how many rules there are once there are some.</summary>
    private void UpdateAutoSwitchText()
    {
        int times = _settings.AutoSwitchRules.Count(r => r.When == AutoSwitchWhen.Time);
        int apps = _settings.AutoSwitchRules.Count - times;
        AutoSwitchText.Text = _settings.AutoSwitchRules.Count == 0
            ? "By time of day (Night at 21:00), or while an app runs (Gaming while a game is open)."
            : $"{times} time rule{(times == 1 ? "" : "s")}, {apps} app rule{(apps == 1 ? "" : "s")}." +
              (_settings.AutoSwitch ? "" : " Off: turn it on to follow them.");
    }

    /// <summary>A menu of ready-made profiles; the chosen one is added (with a free name) and selected.</summary>
    private void AddExampleProfile_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var example in ProfileStore.Examples())
        {
            var item = new MenuItem { Header = example.Name };
            item.Click += (_, _) =>
            {
                string name = example.Name;
                for (int n = 2; _profiles.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)); n++)
                    name = $"{example.Name} {n}";
                example.Name = name;
                _profiles.Add(example);
                ProfileList.SelectedIndex = _profiles.Count - 1;
                UpdateDeleteButton();
                ScheduleSave();
                StatusText.Text = $"Added \"{name}\". Change anything you like, then press Apply.";
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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
            if (ShellMode.IsShell) OpenDesktops();
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
        SafeUi.Clear(MonitorDesignsPanel);
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
            ShellLaunch.OpenFolder(TaskbarDesigns.Folder);
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

    // ---------- Shell (Phase 4) ----------

    private readonly List<DesktopWindow> _desktops = new();
    private ShellWindow? _shellWindow;

    /// <summary>
    /// Started as the user's shell (Explorer isn't running): desktops, a taskbar no matter what,
    /// the startup programs, and the main window out of sight (it lives in the tray / bar menu).
    /// </summary>
    private void StartShellMode()
    {
        // Out of sight from the start: no flash of the window at sign-in.
        Opacity = 0;
        Loaded += (_, _) => { Hide(); Opacity = 1; };

        ShellMode.WatchForExplorer(TimeSpan.FromMinutes(3));

        // The shell window first: until one is registered, anything that starts explorer.exe makes
        // Explorer take over as a second shell.
        var registered = RegisterShellWindow();
        Wallpaper.Apply();
        OpenDesktops();

        // As the shell, never leave the user without a taskbar, whatever the setting says.
        if (!TaskbarShown) SetTaskbarVisible(true);
        ShellMode.Log("Taskbar shown");

        if (ShellMode.RunStartupPrograms) _ = StartStartupProgramsAsync(registered);
        Closed += (_, _) =>
        {
            foreach (var d in _desktops) d.Close();
            _shellWindow?.Dispose();
            ShellMode.Log("Windows Customizer closed");
        };
    }

    /// <summary>The startup programs, only once our shell window is registered (or after 35 s at most).</summary>
    private static async Task StartStartupProgramsAsync(Task<bool> registered)
    {
        bool ok = await Task.WhenAny(registered, Task.Delay(TimeSpan.FromSeconds(35))) == registered && registered.Result;
        ShellMode.Log(ok ? "Starting the startup programs" : "Starting the startup programs WITHOUT a registered shell window");
        await StartupApps.RunAllAsync(ShellMode.Log);
    }

    /// <summary>One desktop per monitor (icons on the main one).</summary>
    private void OpenDesktops()
    {
        foreach (var d in _desktops) d.Close();
        _desktops.Clear();
        // The icons go on the monitor chosen in Desktop settings (the main one if none, or if it's gone).
        var monitors = Monitors.All();
        string? iconMonitor = DesktopLayout.Current.IconMonitor;
        if (iconMonitor == null || !monitors.Any(m => m.Device == iconMonitor))
            iconMonitor = monitors.FirstOrDefault(m => m.IsPrimary)?.Device;
        foreach (var monitor in monitors)
        {
            var desktop = new DesktopWindow(monitor, showIcons: monitor.Device == iconMonitor) { OpenCustomizer = ShowFromTray };
            desktop.Show();
            _desktops.Add(desktop);
        }
        ShellMode.Log($"Desktops opened on {_desktops.Count} monitor(s)");
    }

    private DispatcherTimer? _shellWindowTimer;

    /// <summary>
    /// Registers our (hidden) shell window, see <see cref="ShellWindow"/>. If Windows refuses at first
    /// (for a moment after another shell window went away), it is retried every half second for up to 30 seconds.
    /// </summary>
    private Task<bool> RegisterShellWindow()
    {
        var done = new TaskCompletionSource<bool>();
        try { _shellWindow = new ShellWindow(); }
        catch (Exception ex)
        {
            ShellMode.Log($"Could not create the shell window: {ex.Message}");
            done.SetResult(false);
            return done.Task;
        }

        int attempts = 1;
        string? problem = _shellWindow.TryRegister();
        if (problem == null)
        {
            ShellMode.Log("Registered as the shell window (first attempt)");
            done.SetResult(true);
            return done.Task;
        }
        ShellMode.Log($"Shell window not registered yet: {problem}; retrying");
        _shellWindowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _shellWindowTimer.Tick += (_, _) =>
        {
            attempts++;
            problem = _shellWindow.TryRegister();
            if (problem == null)
            {
                _shellWindowTimer!.Stop();
                StatusText.Text = $"Registered as the shell window (attempt {attempts}).";
                ShellMode.Log(StatusText.Text);
                done.TrySetResult(true);
            }
            else if (attempts >= 60)
            {
                _shellWindowTimer!.Stop();
                StatusText.Text = "Could not register as the shell window: opening a folder may start Explorer as the shell.";
                ShellMode.Log($"{StatusText.Text} Last reason: {problem}");
                done.TrySetResult(false);
            }
        };
        _shellWindowTimer.Start();
        return done.Task;
    }

    private void UpdateShellStatus()
    {
        bool installed = ShellMode.IsInstalled();
        string now = ShellMode.IsShell ? "Windows Customizer is your shell right now." : "Explorer is your shell right now.";
        string next = installed
            ? "From the next sign-in: Windows Customizer (if it ever closes, Explorer starts automatically)."
            : ShellMode.CurrentUserShell() is { } other ? $"Your user has another shell set: {other}" : "From the next sign-in: Explorer.";
        ShellStatusText.Text = $"{now} {next}";
        UseAsShellButton.IsEnabled = !installed;
        BackToExplorerButton.IsEnabled = installed || ShellMode.CurrentUserShell() != null;
    }

    private void UseAsShell_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Make Windows Customizer your shell instead of Explorer, from your next sign-in?\n\n" +
            "• Only your user is changed; a backup of the setting is saved first.\n" +
            "• If Windows Customizer ever closes or crashes, Explorer starts automatically.\n" +
            "• Not available without Explorer: Quick Settings (Win+A) and the notification center (Win+N).\n\n" +
            "To go back: the \"Back to Explorer\" button, or Restore-Shell.ps1 next to the app " +
            "(also from Ctrl+Alt+Del > Task Manager > Run new task).\n\n" +
            "Tip: have a second administrator account, so you can always undo it from there.",
            "Use as shell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var backup = ShellMode.Install();
            StatusText.Text = $"Windows Customizer will be your shell from the next sign-in. Backup: {backup}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Not changed: {ex.Message}";
        }
        UpdateShellStatus();
    }

    private void BackToExplorer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backup = ShellMode.Uninstall();
            StatusText.Text = $"Explorer will be your shell again from the next sign-in. Backup: {backup}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Not changed: {ex.Message}";
        }
        UpdateShellStatus();
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ShellMode.BackupFolder);
        ShellLaunch.OpenFolder(ShellMode.BackupFolder);
    }

    // ---------- Search ----------

    private WinKeyHook? _winKeyHook;
    private SearchSettingsWindow? _searchSettings;

    private void SetUpSearch()
    {
        // Search boxes and Start buttons on the bars open our windows with these settings.
        TaskbarView.SearchLookProvider = () => _settings.Search ??= new SearchLook();
        TaskbarView.StartMenuOpener = (anchor, edge) => StartMenuWindow.Toggle(StartMenuContext(), anchor, edge);

        var action = _settings.EffectiveWinKey;
        _loading = true;
        WinKeyBox.SelectedIndex = (int)action;
        _loading = false;
        SetWinKey(action);
    }

    /// <summary>Everything the start menu needs: its look, the search settings, the pins and what its buttons do.</summary>
    private StartMenuContext StartMenuContext() => new()
    {
        Look = _settings.StartMenu ??= new StartMenuLook(),
        Search = _settings.Search ??= new SearchLook(),
        Pins = _settings.StartPinned ??= new List<StartPin>(),
        Save = SaveSettings,
        PinToTaskbar = PinToTaskbar,
        OpenCustomizer = ShowFromTray,
    };

    private void OpenStartMenu() => StartMenuWindow.Toggle(StartMenuContext());

    /// <summary>
    /// What the Windows key opens. Anything but the Windows Start menu needs the keyboard hook;
    /// the Windows Start menu means no hook at all.
    /// </summary>
    private void SetWinKey(WinKeyAction action)
    {
        bool needsHook = action != WinKeyAction.WindowsStart;
        if (needsHook && _winKeyHook == null)
        {
            try
            {
                _winKeyHook = new WinKeyHook();
                // Raised inside the keyboard hook: open the window a moment later, so the hook returns at once.
                _winKeyHook.Pressed += () => Dispatcher.InvokeAsync(() =>
                {
                    if (_settings.EffectiveWinKey == WinKeyAction.StartMenu) OpenStartMenu();
                    else SearchWindow.Toggle(_settings.Search ??= new SearchLook());
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not take over the Windows key: {ex.Message}";
                action = WinKeyAction.WindowsStart;
            }
        }
        if (action == WinKeyAction.WindowsStart && _winKeyHook != null)
        {
            _winKeyHook.Dispose();
            _winKeyHook = null;
        }

        if (_settings.WinKey != action)
        {
            _settings.WinKey = action;
            _settings.WinKeyOpensSearch = action == WinKeyAction.Search; // keep the older setting consistent
            SaveSettings();
        }
        _loading = true;
        WinKeyBox.SelectedIndex = (int)action;
        _loading = false;
    }

    private void WinKeyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || WinKeyBox.SelectedIndex < 0) return;
        SetWinKey((WinKeyAction)WinKeyBox.SelectedIndex);
    }

    private StartMenuSettingsWindow? _startSettings;

    private void StartMenuSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_startSettings != null)
        {
            _startSettings.Activate();
            return;
        }
        _startSettings = new StartMenuSettingsWindow(_settings.StartMenu ??= new StartMenuLook(), SaveSettings, OpenStartMenu);
        _startSettings.Closed += (_, _) => _startSettings = null;
        _startSettings.Show();
    }

    /// <summary>
    /// "Pin to taskbar" from the start menu: adds the app to the first pinned-apps module of the design
    /// in use (a new one at the start of the left zone if it has none) and saves the design file, which
    /// rebuilds the bars.
    /// </summary>
    private void PinToTaskbar(AppEntry app)
    {
        var design = ActiveDesign();
        var pins = design.AllModules.FirstOrDefault(m => m.Type == ModuleType.PinnedApps);
        if (pins == null)
        {
            pins = TaskbarModule.Create(ModuleType.PinnedApps);
            int afterMenu = design.Left.FindIndex(m => m.Type is ModuleType.Menu or ModuleType.Start) + 1;
            design.Left.Insert(afterMenu, pins);
        }
        pins.Apps ??= new List<string>();
        if (pins.Apps.Contains(app.ShellName, StringComparer.OrdinalIgnoreCase)) return;
        pins.Apps.Add(app.ShellName);
        try
        {
            TaskbarDesigns.Save(design);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not pin to the taskbar: {ex.Message}";
        }
    }

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

    private DesktopSettingsWindow? _desktopSettings;

    private void DesktopSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_desktopSettings != null) { _desktopSettings.Activate(); return; }
        _desktopSettings = new DesktopSettingsWindow(
            tryIt: () => { if (!ShellMode.IsShell) OpenDesktopPreview(); },
            monitorChanged: () => { DesktopLayout.NotifyChanged(); if (ShellMode.IsShell) OpenDesktops(); });
        _desktopSettings.Closed += (_, _) => _desktopSettings = null;
        _desktopSettings.Show();
    }

    private MenuSettingsWindow? _menuSettings;

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_menuSettings != null) { _menuSettings.Activate(); return; }
        _menuSettings = new MenuSettingsWindow(_settings.Menus ??= new MenuLook(), SaveSettings);
        _menuSettings.Closed += (_, _) => _menuSettings = null;
        _menuSettings.Show();
    }

    private AltTabSettingsWindow? _altTabSettings;

    private void AltTabSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_altTabSettings != null) { _altTabSettings.Activate(); return; }
        _altTabSettings = new AltTabSettingsWindow(_settings.AltTab ??= new AltTabLook(), SaveSettings);
        _altTabSettings.Closed += (_, _) => _altTabSettings = null;
        _altTabSettings.Show();
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
