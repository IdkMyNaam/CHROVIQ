using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.Common.Helpers;
using ShellTrayIcon = ManagedShell.WindowsTray.NotifyIcon;
using TrayRect = ManagedShell.Interop.NativeMethods.Rect;

namespace WindowsCustomizer;

/// <summary>The Tray module: notification-area icons of all programs (see <see cref="TrayHost"/>).</summary>
public partial class TaskbarView
{
    private FrameworkElement TrayModule(TaskbarModule module)
    {
        // The designer's preview shouldn't take over everyone's tray icons: show a stand-in there.
        if (!IsHitTestVisible)
            return new TextBlock { Text = "[tray icons]", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };

        var area = TrayHost.Acquire();
        if (area == null)
        {
            return new TextBlock
            {
                Text = "⚠", Foreground = Brushes.Orange, Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Tray icons could not be started.",
            };
        }
        _disposables.Add(new Disposer(TrayHost.Release));

        bool overflow = module.TrayOverflow == true;
        var icons = TrayIconList(overflow ? area.PinnedIcons : area.AllIcons, wrap: false, module);
        if (!overflow) return icons;

        // ^ button with the other icons in a flyout.
        var flyoutIcons = TrayIconList(area.UnpinnedIcons, wrap: true, module);
        var popup = new Popup
        {
            Placement = Edge == TaskbarEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(ParseColor(module.FlyoutBackground, Color.FromArgb(0xF5, 0x24, 0x24, 0x24))),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Child = flyoutIcons,
            },
        };
        var more = GlyphButton(Edge == TaskbarEdge.Top ? "" : "", "Show hidden icons", null);
        more.Padding = new Thickness(6, 0, 6, 0);
        popup.PlacementTarget = more;
        // Clicking ^ while the flyout is open: the mouse press already closed it (it closes on any click
        // outside), so the click that follows must not open it again.
        DateTime closedAt = DateTime.MinValue;
        more.Click += (_, _) =>
        {
            if ((DateTime.Now - closedAt).TotalMilliseconds < 300) return;
            popup.IsOpen = true;
        };
        popup.Opened += (_, _) => _openMenus++;
        popup.Closed += (_, _) =>
        {
            _openMenus = Math.Max(0, _openMenus - 1);
            closedAt = DateTime.Now;
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(more);
        row.Children.Add(icons);
        return row;
    }

    /// <summary>A row (or, in the flyout, a wrapping grid) of tray icon buttons.</summary>
    private ItemsControl TrayIconList(ICollectionView icons, bool wrap, TaskbarModule module)
    {
        double iconSize = Math.Clamp(module.IconSize ?? 16, 10, 48);
        double spacing = Math.Clamp(module.IconSpacing ?? 5, 0, 30);
        int columns = Math.Clamp(module.FlyoutColumns ?? 5, 1, 20);

        var panel = new FrameworkElementFactory(wrap ? typeof(WrapPanel) : typeof(StackPanel));
        if (wrap) panel.SetValue(MaxWidthProperty, columns * (iconSize + 2 * spacing + 2));
        else panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var hidden = new HashSet<string>(module.HiddenIcons ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        Color? tint = string.IsNullOrWhiteSpace(module.IconTint) ? null : ParseColor(module.IconTint, Colors.White);
        return new ItemsControl
        {
            ItemsSource = icons,
            ItemsPanel = new ItemsPanelTemplate(panel),
            ItemTemplate = TrayIconTemplate(iconSize, spacing, hidden, tint),
            Focusable = false,
        };
    }

    /// <summary>One icon: a bar button with the program's icon, passing every mouse event to that program.</summary>
    private DataTemplate TrayIconTemplate(double iconSize, double spacing, HashSet<string> hidden, Color? tint)
    {
        var button = new FrameworkElementFactory(typeof(Button));
        // Icons chosen to be hidden (module.HiddenIcons) aren't shown at all.
        if (hidden.Count > 0)
            button.SetBinding(VisibilityProperty, new Binding(nameof(ShellTrayIcon.Identifier)) { Converter = new HiddenConverter(hidden) });
        button.SetResourceReference(StyleProperty, "BarButton");
        button.SetValue(PaddingProperty, new Thickness(spacing, 0, spacing, 0));
        button.SetValue(MinHeightProperty, Math.Max(28.0, iconSize + 8));
        button.SetBinding(ToolTipProperty, new Binding(nameof(ShellTrayIcon.Title)) { StringFormat = "{0}\n(Ctrl+click: move between the bar and ^)" });
        button.AddHandler(MouseEnterEvent, new MouseEventHandler(TrayIcon_MouseEnter));
        button.AddHandler(MouseLeaveEvent, new MouseEventHandler(TrayIcon_MouseLeave));
        button.AddHandler(MouseMoveEvent, new MouseEventHandler(TrayIcon_MouseMove));
        // Preview events: the Button would otherwise swallow the left mouse button for its own Click.
        button.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(TrayIcon_MouseDown));
        button.AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler(TrayIcon_MouseUp));

        var image = new FrameworkElementFactory(typeof(Image));
        // One color for all icons (module.IconTint), keeping their shapes and details.
        image.SetBinding(Image.SourceProperty, new Binding(nameof(ShellTrayIcon.Icon)) { Converter = tint is { } c ? new TintConverter(c) : null });
        image.SetValue(WidthProperty, iconSize);
        image.SetValue(HeightProperty, iconSize);
        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        button.AppendChild(image);

        return new DataTemplate { VisualTree = button };
    }

    private static ShellTrayIcon? IconOf(object sender) => (sender as FrameworkElement)?.DataContext as ShellTrayIcon;

    private static int DoubleClickTime => System.Windows.Forms.SystemInformation.DoubleClickTime;

    private void TrayIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        if (IconOf(sender) is not { } icon || sender is not FrameworkElement element) return;
        // Where the icon is on screen (physical pixels): some programs place their menu or flyout by it.
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        icon.Placement = new TrayRect((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y);
        icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
    }

    private void TrayIcon_MouseLeave(object sender, MouseEventArgs e) =>
        IconOf(sender)?.IconMouseLeave(MouseHelper.GetCursorPositionParam());

    private void TrayIcon_MouseMove(object sender, MouseEventArgs e) =>
        IconOf(sender)?.IconMouseMove(MouseHelper.GetCursorPositionParam());

    /// <summary>Ctrl + left click is ours (pin/unpin); everything else goes to the program.</summary>
    private static bool IsPinClick(MouseButtonEventArgs e) =>
        e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    private void TrayIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsPinClick(e)) { e.Handled = true; return; }
        IconOf(sender)?.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), DoubleClickTime);
    }

    private void TrayIcon_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsPinClick(e))
        {
            if (IconOf(sender) is { } icon) TrayHost.TogglePin(icon);
            e.Handled = true;
            return;
        }
        IconOf(sender)?.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), DoubleClickTime);
    }

    /// <summary>Collapsed for the identifiers in the set.</summary>
    private sealed class HiddenConverter(HashSet<string> hidden) : IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is string id && hidden.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>A one-color copy of the icon (see IconColors.Tinted).</summary>
    private sealed class TintConverter(Color tint) : IValueConverter
    {
        public object? Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is ImageSource icon ? IconColors.Tinted(icon, tint) ?? icon : value;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>Runs an action when disposed (for cleanups in <see cref="_disposables"/>).</summary>
    private sealed class Disposer : IDisposable
    {
        private Action? _action;
        public Disposer(Action action) => _action = action;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }
}
