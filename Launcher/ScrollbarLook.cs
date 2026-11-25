using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>How scroll bars look in the start menu and search.</summary>
public enum ScrollbarStyle
{
    /// <summary>A thin rounded bar in the text color, no arrows (the default).</summary>
    Thin,
    /// <summary>No bar; the mouse wheel and keys still scroll.</summary>
    Hidden,
    /// <summary>The normal Windows scroll bar.</summary>
    Windows,
}

internal static class ScrollbarLook
{
    /// <summary>
    /// Gives the scroll bars inside <paramref name="scope"/> (a ScrollViewer, or a list with one) the chosen look.
    /// </summary>
    /// <param name="color">Thin: the bar's color (it is drawn see-through, a bit stronger under the mouse).</param>
    public static void Apply(FrameworkElement scope, ScrollbarStyle style, Color color, double width = 6)
    {
        // Lists here never scroll sideways (names are cut off with "..."), and the thin style is made for an up/down bar.
        ScrollViewer.SetHorizontalScrollBarVisibility(scope, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(scope, style == ScrollbarStyle.Hidden ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
        if (scope is ScrollViewer viewer)
            viewer.VerticalScrollBarVisibility = style == ScrollbarStyle.Hidden ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        if (style != ScrollbarStyle.Thin) return;

        scope.Resources["ThinScrollThumb"] = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B));
        scope.Resources["ThinScrollThumbHover"] = new SolidColorBrush(Color.FromArgb(0xA0, color.R, color.G, color.B));
        scope.Resources[typeof(ScrollBar)] = ThinStyle(Math.Clamp(width, 2, 20));
    }

    private static Style ThinStyle(double width)
    {
        string w = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string r = (width / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (Style)XamlReader.Parse($$"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
              <Setter Property="Width" Value="{{w}}" />
              <Setter Property="MinWidth" Value="{{w}}" />
              <Setter Property="Margin" Value="4,2,0,2" />
              <Setter Property="Background" Value="Transparent" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Track x:Name="PART_Track" IsDirectionReversed="True">
                      <Track.Thumb>
                        <Thumb>
                          <Thumb.Template>
                            <ControlTemplate TargetType="Thumb">
                              <Border x:Name="Bar" CornerRadius="{{r}}" Background="{DynamicResource ThinScrollThumb}" />
                              <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                  <Setter TargetName="Bar" Property="Background" Value="{DynamicResource ThinScrollThumbHover}" />
                                </Trigger>
                                <Trigger Property="IsDragging" Value="True">
                                  <Setter TargetName="Bar" Property="Background" Value="{DynamicResource ThinScrollThumbHover}" />
                                </Trigger>
                              </ControlTemplate.Triggers>
                            </ControlTemplate>
                          </Thumb.Template>
                        </Thumb>
                      </Track.Thumb>
                    </Track>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
    }
}
