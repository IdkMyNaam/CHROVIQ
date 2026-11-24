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
    public double MaxItemWidth { get; set; } = 200;

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
