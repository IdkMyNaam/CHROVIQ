using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow()
    {
        InitializeComponent();

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
        GamesBox.Text = string.Join(", ", _settings.GameProcesses);

        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedIndex = 0;
        UpdateDeleteButton();

        if (loadWarning != null)
            StatusText.Text = loadWarning;

        _gameWatcher = new GameWatcher(_settings);
        _gameWatcher.StatusChanged += message => StatusText.Text = message;
        _gameWatcher.Start();

        Closed += (_, _) =>
        {
            SaveGameList(); // in case the games box still has focus
            _gameWatcher.Stop();
            _saveTimer.Stop();
            SaveProfiles(); // a pending save may not have run yet
        };
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

        int index = ProfileList.SelectedIndex;
        _profiles.Remove(profile);
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
        if (Selected is not { } profile) return;

        ApplyButton.IsEnabled = false;
        StatusText.Text = $"Applying \"{profile.Name}\"...";
        try
        {
            await ThemeService.ApplyAsync(profile);
            StatusText.Text = $"Applied \"{profile.Name}\".";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
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
