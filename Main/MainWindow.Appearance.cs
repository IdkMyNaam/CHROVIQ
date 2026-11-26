using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>The Appearance page: picking, making and editing app themes (see <see cref="AppThemes"/>).</summary>
public partial class MainWindow
{
    /// <summary>A theme tile: name, a small note, and a swatch of its background and accent.</summary>
    private sealed record ThemeItem(AppTheme Theme)
    {
        public string Name => Theme.Name;
        public string Notes => Theme.BuiltIn ? "Built-in" : "Your theme";
        public Brush Swatch => new SolidColorBrush(AppThemes.TryColor(Theme.BackgroundColor) ??
            (Theme.Mode == AppThemeMode.Light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20)));
        public Brush AccentBrush => new SolidColorBrush(AppThemes.TryColor(Theme.Accent) ?? SystemParameters.WindowGlassColor);
    }

    private const string WindowsFont = "(Windows' font)";
    private AppTheme? _editingTheme;
    private bool _themeLoading;
    private readonly DispatcherTimer _themeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void SetUpAppearance()
    {
        ThemeFontBox.ItemsSource = new[] { WindowsFont }.Concat(Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n)).ToList();
        _themeSaveTimer.Tick += (_, _) => { _themeSaveTimer.Stop(); if (_editingTheme != null) SaveTheme(_editingTheme); };
        LoadThemes(AppThemes.Current.Name);
    }

    private void LoadThemes(string? select)
    {
        var items = AppThemes.LoadAll().Select(t => new ThemeItem(t)).ToList();
        ThemeList.ItemsSource = items;
        ThemeList.SelectedItem = items.FirstOrDefault(i => i.Name.Equals(select, StringComparison.CurrentCultureIgnoreCase)) ?? items.First();
    }

    /// <summary>Picking a theme uses it at once (and remembers it).</summary>
    private void ThemeList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ThemeList.SelectedItem is not ThemeItem item) return;
        FlushThemeSave();
        _editingTheme = item.Theme;
        FillThemeEditor(item.Theme);
        AppThemes.Apply(item.Theme);
        _settings.AppTheme = item.Theme.Name;
        SaveSettings();
    }

    private void FillThemeEditor(AppTheme theme)
    {
        _themeLoading = true;
        BuiltInThemeNote.Visibility = theme.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
        ThemeEditorFields.IsEnabled = !theme.BuiltIn;
        DeleteThemeButton.IsEnabled = !theme.BuiltIn;
        ThemeNameBox.Text = theme.Name;
        ThemeModeBox.SelectedIndex = (int)theme.Mode;
        ThemeWindowsAccentCheck.IsChecked = theme.Accent == null;
        ThemeAccentBox.Text = theme.Accent ?? "";
        ThemeBackgroundBox.SelectedIndex = (int)theme.Background;
        ThemeBgColorBox.Text = theme.BackgroundColor ?? "";
        ThemeImageBox.Text = theme.BackgroundImage ?? "";
        ThemeImageOpacitySlider.Value = theme.ImageOpacity;
        ThemeCornerSlider.Value = theme.CornerRadius;
        ThemeCardSlider.Value = theme.CardOpacity;
        ThemeFontBox.SelectedItem = string.IsNullOrWhiteSpace(theme.FontFamily) ? WindowsFont : theme.FontFamily;
        ThemeTextColorBox.Text = theme.TextColor ?? "";
        ThemeSecondaryColorBox.Text = theme.SecondaryTextColor ?? "";
        ThemeBorderColorBox.Text = theme.BorderColor ?? "";
        _themeLoading = false;
        UpdateThemeFieldStates();
    }

    /// <summary>Only the fields that matter for the chosen background (and accent) are shown / usable.</summary>
    private void UpdateThemeFieldStates()
    {
        var background = (AppThemeBackground)Math.Max(0, ThemeBackgroundBox.SelectedIndex);
        ThemeColorCard.Visibility = background is AppThemeBackground.Color or AppThemeBackground.Image or AppThemeBackground.Material
            ? Visibility.Visible : Visibility.Collapsed;
        ThemeMaterialCard.Visibility = background == AppThemeBackground.Material ? Visibility.Visible : Visibility.Collapsed;
        ThemeMaterialText.Text = _editingTheme?.Material is { } material
            ? $"{material}: drawn behind this window's content (the background color shows under it)"
            : "No material chosen yet: press Material settings...";
        ThemePictureCard.Visibility = background == AppThemeBackground.Image ? Visibility.Visible : Visibility.Collapsed;
        ThemeAccentBox.IsEnabled = ThemeWindowsAccentCheck.IsChecked != true;
        var accent = AppThemes.TryColor(ThemeAccentBox.Text);
        ThemeAccentSwatch.Background = new SolidColorBrush(ThemeWindowsAccentCheck.IsChecked == true || accent == null
            ? SystemParameters.WindowGlassColor : accent.Value);
    }

    /// <summary>Any field changed: into the theme, shown at once, saved a moment later.</summary>
    private void ThemeField_Changed(object sender, RoutedEventArgs e)
    {
        if (_themeLoading || _editingTheme is not { BuiltIn: false } theme || ThemeModeBox == null) return;
        theme.Mode = (AppThemeMode)Math.Max(0, ThemeModeBox.SelectedIndex);
        theme.Accent = ThemeWindowsAccentCheck.IsChecked == true || AppThemes.TryColor(ThemeAccentBox.Text) == null ? null : ThemeAccentBox.Text.Trim();
        theme.Background = (AppThemeBackground)Math.Max(0, ThemeBackgroundBox.SelectedIndex);
        theme.BackgroundColor = AppThemes.TryColor(ThemeBgColorBox.Text) == null ? null : ThemeBgColorBox.Text.Trim();
        theme.BackgroundImage = string.IsNullOrWhiteSpace(ThemeImageBox.Text) ? null : ThemeImageBox.Text.Trim().Trim('"');
        theme.ImageOpacity = Math.Round(ThemeImageOpacitySlider.Value, 2);
        theme.CornerRadius = ThemeCornerSlider.Value;
        theme.CardOpacity = Math.Round(ThemeCardSlider.Value, 2);
        theme.FontFamily = ThemeFontBox.SelectedItem is string font && font != WindowsFont ? font : null;
        theme.TextColor = AppThemes.TryColor(ThemeTextColorBox.Text) == null ? null : ThemeTextColorBox.Text.Trim();
        theme.SecondaryTextColor = AppThemes.TryColor(ThemeSecondaryColorBox.Text) == null ? null : ThemeSecondaryColorBox.Text.Trim();
        theme.BorderColor = AppThemes.TryColor(ThemeBorderColorBox.Text) == null ? null : ThemeBorderColorBox.Text.Trim();
        UpdateThemeFieldStates();
        AppThemes.Apply(theme);
        _themeSaveTimer.Stop();
        _themeSaveTimer.Start();
    }

    private void FlushThemeSave()
    {
        if (!_themeSaveTimer.IsEnabled) return;
        _themeSaveTimer.Stop();
        if (_editingTheme != null) SaveTheme(_editingTheme);
    }

    private void SaveTheme(AppTheme theme)
    {
        try { AppThemes.Save(theme); }
        catch (Exception ex) { StatusText.Text = $"Could not save the theme: {ex.Message}"; }
    }

    private void ThemeNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingTheme is not { BuiltIn: false } theme) return;
        string name = ThemeNameBox.Text.Trim();
        if (name.Length == 0 || name.Equals(theme.Name, StringComparison.CurrentCulture)) { ThemeNameBox.Text = theme.Name; return; }
        if (AppThemes.LoadAll().Any(t => t != theme && t.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusText.Text = $"There is already a theme called \"{name}\".";
            ThemeNameBox.Text = theme.Name;
            return;
        }
        FlushThemeSave();
        try { AppThemes.Rename(theme, name); }
        catch (Exception ex) { StatusText.Text = $"Could not rename the theme: {ex.Message}"; return; }
        LoadThemes(name);
    }

    private void NewTheme_Click(object sender, RoutedEventArgs e) => AddTheme(AppThemes.BuiltIns[0].Copy(AppThemes.FreeName("My theme")));

    private void DuplicateTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTheme == null) return;
        FlushThemeSave();
        AddTheme(_editingTheme.Copy(AppThemes.FreeName(_editingTheme.Name + " (my version)")));
    }

    private void AddTheme(AppTheme theme)
    {
        SaveTheme(theme);
        LoadThemes(theme.Name);
        ThemeNameBox.Focus();
        ThemeNameBox.SelectAll();
    }

    private void DeleteTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTheme is not { BuiltIn: false } theme) return;
        if (System.Windows.MessageBox.Show(this, $"Delete the theme \"{theme.Name}\"?", "CHROVIQ",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _themeSaveTimer.Stop();
        try { AppThemes.Delete(theme); }
        catch (Exception ex) { StatusText.Text = $"Could not delete the theme: {ex.Message}"; return; }
        LoadThemes(AppThemes.DefaultName);
    }

    private void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import a theme", Filter = "CHROVIQ theme (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var theme = AppThemes.Import(dialog.FileName);
            LoadThemes(theme.Name);
            StatusText.Text = $"Theme \"{theme.Name}\" added.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not import the theme: {ex.Message}"; }
    }

    private void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTheme == null) return;
        FlushThemeSave();
        var dialog = new SaveFileDialog { Title = "Export the theme", Filter = "CHROVIQ theme (*.json)|*.json", FileName = _editingTheme.Name + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            AppThemes.Export(_editingTheme, dialog.FileName);
            StatusText.Text = $"Theme saved as {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not export the theme: {ex.Message}"; }
    }

    private ThemeMaterialWindow? _themeMaterialWindow;

    /// <summary>The material for a "Material" background, with its settings (applied and saved as you change them).</summary>
    private void ThemeMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTheme is not { BuiltIn: false } theme) return;
        _themeMaterialWindow?.Close();
        _themeMaterialWindow = new ThemeMaterialWindow(theme, () =>
        {
            AppThemes.Apply(theme);
            SaveTheme(theme);
            UpdateThemeFieldStates();
        });
        _themeMaterialWindow.Closed += (_, _) => _themeMaterialWindow = null;
        _themeMaterialWindow.Show();
    }

    private void ThemeBrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a picture", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*" };
        if (dialog.ShowDialog(this) == true) ThemeImageBox.Text = dialog.FileName; // TextChanged applies it
    }
}
