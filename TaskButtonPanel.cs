using System.Windows;
using System.Windows.Controls;

namespace WindowsCustomizer;

/// <summary>
/// Lays window buttons out in a row. Each button gets an equal share of the space, but never
/// more than <see cref="MaxItemWidth"/>, so a few windows get normal-sized buttons and many
/// windows get narrower ones. Works in any zone (a fixed-width side zone or the centered zone).
/// </summary>
public sealed class TaskButtonPanel : Panel
{
    // A dependency property, so the window list's panel template can set it per design.
    public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
        nameof(MaxItemWidth), typeof(double), typeof(TaskButtonPanel),
        new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    private double ItemWidth(double available) =>
        InternalChildren.Count == 0 ? 0
        : double.IsInfinity(available) ? MaxItemWidth
        : Math.Min(MaxItemWidth, available / InternalChildren.Count);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = ItemWidth(availableSize.Width);
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width * InternalChildren.Count, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = ItemWidth(finalSize.Width);
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
