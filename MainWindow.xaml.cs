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

    public MainWindow()
    {
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedIndex = 0;
    }

    private Profile? Selected => ProfileList.SelectedItem as Profile;

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;

        DarkCheck.IsChecked = p.DarkMode;
        WallpaperText.Text = p.WallpaperPath ?? "(none)";
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

    private void ChooseWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp",
            Title = "Choose a wallpaper"
        };

        if (dialog.ShowDialog() == true)
        {
            Selected.WallpaperPath = dialog.FileName;
            WallpaperText.Text = dialog.FileName;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;

        try
        {
            ThemeService.Apply(Selected);
            StatusText.Text = $"Applied \"{Selected.Name}\".";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
        }
    }
}
