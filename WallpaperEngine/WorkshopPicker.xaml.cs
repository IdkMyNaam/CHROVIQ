using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>Dialog listing installed Wallpaper Engine wallpapers. Sets <see cref="SelectedPath"/> on OK.</summary>
public partial class WorkshopPicker : Wpf.Ui.Controls.FluentWindow
{
    private List<WallpaperItem> _all = new();

    /// <summary>Path to the chosen project.json (or video file).</summary>
    public string? SelectedPath { get; private set; }

    public WorkshopPicker()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            CountText.Text = "Searching...";
            try
            {
                // Scanning many Workshop folders can take a moment; keep the UI responsive.
                _all = await Task.Run(() => WallpaperEngine.FindWallpapers());
            }
            catch (Exception ex)
            {
                CountText.Text = $"Search failed: {ex.Message}";
                return;
            }
            ApplyFilter();
            FilterBox.Focus();
        };
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var items = filter.Length == 0
            ? _all
            : _all.Where(i => i.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();

        WallpaperList.ItemsSource = items;
        CountText.Text = _all.Count == 0
            ? "No wallpapers found."
            : $"{items.Count} of {_all.Count}";
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void WallpaperList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (WallpaperList.SelectedItem is not WallpaperItem item) return;
        SelectedPath = item.ProjectJsonPath;
        DialogResult = true;
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Wallpaper Engine project or video",
            Filter = "Wallpaper Engine project|project.json|Videos|*.mp4;*.webm;*.mkv;*.avi|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            SelectedPath = dialog.FileName;
            DialogResult = true;
        }
    }
}
