using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WindowsCustomizer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Profile> _profiles = new()
    {
        new Profile { Name = "Dark", DarkMode = true },
        new Profile { Name = "Light", DarkMode = false },
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GameWatcher _gameWatcher;

    // True while the UI is filled from a profile, so change handlers don't write back.
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        WallpaperEngine.SavedExePath = _settings.WallpaperEngineExe;
        UpdateWallpaperEngineText();

        PauseForGamesCheck.IsChecked = _settings.PauseForGames;
        GamesBox.Text = string.Join(", ", _settings.GameProcesses);

        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedIndex = 0;

        _gameWatcher = new GameWatcher(_settings);
        _gameWatcher.StatusChanged += message => StatusText.Text = message;
        _gameWatcher.Start();

        Closed += (_, _) =>
        {
            SaveGameList(); // in case the games box still has focus
            _gameWatcher.Stop();
        };
    }

    private Profile? Selected => ProfileList.SelectedItem as Profile;

    // ---------- Profiles ----------

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;

        _loading = true;
        DarkCheck.IsChecked = p.DarkMode;
        KindBox.SelectedIndex = (int)p.Kind;
        ValueBox.Text = p.WallpaperValue ?? "";
        _loading = false;

        UpdateKindUi(p.Kind);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        _profiles.Add(new Profile { Name = $"Profile {_profiles.Count + 1}" });
        ProfileList.SelectedIndex = _profiles.Count - 1;
    }

    private void DarkCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null)
            Selected.DarkMode = DarkCheck.IsChecked == true;
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
        if (!_loading && Selected != null)
            Selected.WallpaperValue = ValueBox.Text;
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
