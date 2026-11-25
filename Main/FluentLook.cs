using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;

namespace WindowsCustomizer;

/// <summary>
/// The Windows 11 look (WPF-UI) for Windows Customizer's own settings windows: Mica, a Windows 11 title bar,
/// and WPF-UI's control styles for that window only (the taskbar, start menu and desktop keep their own looks).
/// </summary>
internal static class FluentLook
{
    /// <summary>For windows made in code: Mica, rounded corners, WPF-UI styles, and a title bar above the content.</summary>
    public static void Apply(FluentWindow window, string title, UIElement content)
    {
        window.Title = title;
        window.ExtendsContentIntoTitleBar = true;
        window.WindowBackdropType = WindowBackdropType.Mica;
        window.WindowCornerPreference = WindowCornerPreference.Round;
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false };
        root.Children.Add(bar);
        Grid.SetRow((UIElement)content, 1);
        root.Children.Add(content);
        window.Content = root;
        AppThemes.Register(window); // the app theme, now and when it changes
        window.Loaded += (_, _) => Loc.TranslateTree(window); // its texts in the chosen language
    }

    /// <summary>
    /// Keeps WPF-UI's styles out of part of a window (the taskbar preview in the designer), so it looks exactly
    /// like the real thing: an empty style per control type stops the window's styles from reaching it.
    /// </summary>
    public static void Shield(FrameworkElement area)
    {
        foreach (var type in new[]
        {
            typeof(System.Windows.Controls.Button), typeof(ToggleButton), typeof(RepeatButton), typeof(System.Windows.Controls.TextBlock),
            typeof(System.Windows.Controls.TextBox), typeof(ScrollViewer), typeof(ScrollBar), typeof(ListBox), typeof(ListBoxItem),
            typeof(System.Windows.Controls.ContextMenu), typeof(System.Windows.Controls.MenuItem), typeof(System.Windows.Controls.ToolTip),
            typeof(Slider), typeof(CheckBox), typeof(ComboBox), typeof(ProgressBar), typeof(Separator), typeof(Label),
        })
            area.Resources[type] = new Style(type);
    }

    /// <summary>A secondary-text look that follows light/dark (WPF-UI's color).</summary>
    public static T Secondary<T>(T element) where T : FrameworkElement
    {
        element.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        if (element is System.Windows.Controls.TextBlock text) text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return element;
    }
}
