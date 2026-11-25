using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsCustomizer;

/// <summary>
/// Makes the app's own clickable elements (tiles, icon buttons made from a Border) work for everyone:
/// reachable with Tab, pressed with Enter or Space, and named for screen readers.
/// </summary>
internal static class Accessibility
{
    /// <summary>
    /// A clickable element: focusable, Enter/Space run <paramref name="onClick"/>, and <paramref name="name"/>
    /// is what a screen reader says (as a button).
    /// </summary>
    public static T Clickable<T>(T element, string name, Action onClick) where T : FrameworkElement
    {
        element.Focusable = true;
        KeyboardNavigation.SetIsTabStop(element, true);
        AutomationProperties.SetName(element, name);
        AutomationProperties.SetItemType(element, "button");
        element.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
        return element;
    }

    /// <summary>A name for a screen reader (for icon-only buttons, taken from their tooltip).</summary>
    public static T Named<T>(T element, string? name) where T : DependencyObject
    {
        if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(element, name);
        return element;
    }

    /// <summary>True while Windows uses a contrast theme (Settings > Accessibility > Contrast themes).</summary>
    public static bool HighContrast => SystemParameters.HighContrast;
}
