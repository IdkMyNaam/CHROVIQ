using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace WindowsCustomizer;

/// <summary>
/// Right-click menus in the Windows 11 look: rounded, a symbol per entry (Segoe Fluent Icons), light or dark
/// like Windows' app mode, and an optional row of symbol buttons at the top (Cut, Copy, Rename, Delete).
/// </summary>
internal static class ModernMenu
{
    // Segoe Fluent Icons code points.
    public const string Cut = "", Copy = "", Paste = "", Rename = "", Delete = "",
        Open = "", Folder = "", View = "", Sort = "", Refresh = "", New = "",
        Display = "", Personalize = "", Terminal = "", More = "", Settings = "",
        Properties = "", Link = "", Location = "", Undo = "", File = "";

    private static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static ResourceDictionary? _styles;
    private static bool _stylesDark;

    /// <summary>The menu styles for the current light/dark mode (made once per mode).</summary>
    private static ResourceDictionary Styles()
    {
        bool dark = IsDark();
        if (_styles != null && _stylesDark == dark) return _styles;
        var (background, border, text, hover, separator) = dark
            ? ("#F22C2C2C", "#FF3F3F3F", "#FFFFFFFF", "#1AFFFFFF", "#FF3F3F3F")
            : ("#F7F9F9F9", "#FFE0E0E0", "#FF1A1A1A", "#0F000000", "#FFE5E5E5");

        string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="MenuBackground" Color="BG"/>
  <SolidColorBrush x:Key="MenuBorder" Color="BORDER"/>
  <SolidColorBrush x:Key="MenuText" Color="TEXT"/>
  <SolidColorBrush x:Key="MenuHover" Color="HOVER"/>

  <!-- Three layers: the shadow, the background (where a material goes, "MaterialLayer"), and the border with the items. -->
  <ControlTemplate x:Key="PanelTemplate" TargetType="ContentControl">
    <Grid Margin="8">
      <Border CornerRadius="8" Background="{StaticResource MenuBackground}">
        <Border.Effect><DropShadowEffect BlurRadius="16" ShadowDepth="3" Opacity="0.28"/></Border.Effect>
      </Border>
      <Border Name="MaterialLayer" CornerRadius="8" Background="{StaticResource MenuBackground}"/>
      <Border Padding="0,4" CornerRadius="8" BorderBrush="{StaticResource MenuBorder}" BorderThickness="1">
        <ContentPresenter/>
      </Border>
    </Grid>
  </ControlTemplate>

  <Style x:Key="Menu" TargetType="ContextMenu">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="HasDropShadow" Value="False"/>
    <Setter Property="Foreground" Value="{StaticResource MenuText}"/>
    <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <ContentControl Template="{StaticResource PanelTemplate}">
            <StackPanel IsItemsHost="True" MinWidth="220" KeyboardNavigation.DirectionalNavigation="Cycle"/>
          </ContentControl>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Separator" TargetType="Separator">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Separator">
          <Border Height="1" Margin="0,4" Background="SEPARATOR"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Item" TargetType="MenuItem">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Foreground" Value="{StaticResource MenuText}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Border x:Name="Bd" Margin="4,1" Height="32" CornerRadius="4" Background="Transparent">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="36"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="28"/>
              </Grid.ColumnDefinitions>
              <TextBlock x:Name="Glyph" Text="{TemplateBinding Tag}" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"
                         FontSize="15" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              <TextBlock x:Name="Check" Text="&#xE73E;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="13"
                         HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
              <ContentPresenter Grid.Column="1" ContentSource="Header" VerticalAlignment="Center" RecognizesAccessKey="True"/>
              <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Opacity="0.6" Margin="24,0,0,0" VerticalAlignment="Center"/>
              <TextBlock x:Name="Arrow" Grid.Column="3" Text="&#xE76C;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="11"
                         HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
              <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="-4" VerticalOffset="-12" AllowsTransparency="True" Focusable="False"
                     IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}" PopupAnimation="Fade">
                <ContentControl Template="{StaticResource PanelTemplate}" Foreground="{StaticResource MenuText}"
                                FontFamily="Segoe UI Variable Text, Segoe UI" FontSize="13">
                  <StackPanel IsItemsHost="True" MinWidth="200" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                </ContentControl>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Bd" Property="Background" Value="{StaticResource MenuHover}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Bd" Property="Opacity" Value="0.4"/></Trigger>
            <Trigger Property="Role" Value="SubmenuHeader"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
              <Setter TargetName="Glyph" Property="Visibility" Value="Collapsed"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Row" TargetType="MenuItem">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <ContentPresenter ContentSource="Header" Margin="8,2,8,4"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="RowButton" TargetType="Button">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Foreground" Value="{StaticResource MenuText}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Width="40" Height="34" Margin="0,0,4,0" CornerRadius="4" Background="Transparent">
            <TextBlock Text="{TemplateBinding Content}" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="16"
                       HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="{StaticResource MenuHover}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Bd" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";
        xaml = xaml.Replace("\"BG\"", $"\"{background}\"").Replace("\"BORDER\"", $"\"{border}\"").Replace("\"TEXT\"", $"\"{text}\"")
                   .Replace("\"HOVER\"", $"\"{hover}\"").Replace("\"SEPARATOR\"", $"\"{separator}\"");
        _styles = (ResourceDictionary)XamlReader.Parse(xaml);
        _stylesDark = dark;
        return _styles;
    }

    /// <summary>The menus' material (shader) and its settings; null for a plain background. Set from the settings.</summary>
    public static string? Material { get; set; }
    public static Dictionary<string, string>? MaterialValues { get; set; }

    /// <summary>A new, empty menu in the Windows 11 look.</summary>
    public static ContextMenu Create()
    {
        var styles = Styles();
        var menu = new ContextMenu { Style = (Style)styles["Menu"] };
        menu.Resources.MergedDictionaries.Add(styles);
        // The material on the menu's background layer, and on each submenu's when it opens.
        menu.Opened += (_, _) => AttachMaterial(menu);
        menu.AddHandler(MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is MenuItem item && item.Template?.FindName("PART_Popup", item) is System.Windows.Controls.Primitives.Popup { Child: { } child })
                AttachMaterial(child);
        }));
        return menu;
    }

    private static void AttachMaterial(DependencyObject root)
    {
        if (Material == null || FindLayer(root) is not { } layer || layer.Effect != null) return;
        if (MaterialHost.Attach(layer, layer, Material, MaterialValues, SystemParameters.WindowGlassColor) is { } error)
            AppTrace.Log($"Menu material: {error}");
    }

    /// <summary>The first "MaterialLayer" border in the visual tree (the menu panel's background).</summary>
    private static Border? FindLayer(DependencyObject root)
    {
        if (root is Border { Name: "MaterialLayer" } found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindLayer(VisualTreeHelper.GetChild(root, i)) is { } inChild) return inChild;
        return null;
    }

    public static MenuItem Item(ItemsControl parent, string glyph, string text, Action? action, string? shortcut = null, bool enabled = true)
    {
        var item = new MenuItem
        {
            Style = (Style)Styles()["Item"], Tag = glyph, Header = text, InputGestureText = shortcut ?? "", IsEnabled = enabled,
        };
        if (action != null) item.Click += (_, e) => { if (e.OriginalSource == item) action(); };
        parent.Items.Add(item);
        return item;
    }

    /// <summary>An entry with a submenu.</summary>
    public static MenuItem Submenu(ItemsControl parent, string glyph, string text) => Item(parent, glyph, text, null);

    /// <summary>An entry with a check mark (instead of its symbol) when it's on.</summary>
    public static MenuItem Check(ItemsControl parent, string text, bool isChecked, Action action)
    {
        var item = Item(parent, "", text, action);
        item.IsChecked = isChecked;
        return item;
    }

    public static void Separator(ItemsControl parent)
    {
        // No separator at the top, and never two in a row.
        if (parent.Items.Count == 0 || parent.Items[^1] is Separator) return;
        parent.Items.Add(new Separator { Style = (Style)Styles()["Separator"] });
    }

    /// <summary>The row of symbol buttons at the top (like Windows 11's Cut / Copy / Rename / Share / Delete).</summary>
    public static void ButtonRow(ContextMenu menu, params (string Glyph, string Tip, Action Action, bool Enabled)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, tip, action, enabled) in buttons)
        {
            var button = new Button { Style = (Style)Styles()["RowButton"], Content = glyph, ToolTip = tip, IsEnabled = enabled };
            button.Click += (_, _) => { menu.IsOpen = false; action(); };
            row.Children.Add(button);
        }
        menu.Items.Insert(0, new MenuItem { Style = (Style)Styles()["Row"], Header = row });
        menu.Items.Insert(1, new Separator { Style = (Style)Styles()["Separator"] });
    }
}
