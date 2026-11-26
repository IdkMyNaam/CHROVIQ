using System.Drawing;
using Forms = System.Windows.Forms;

namespace WindowsCustomizer;

/// <summary>Notification-area icon with the app menu (see <see cref="AppMenuItem"/>).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Func<IEnumerable<AppMenuItem>> _getMenuItems;

    public event Action? Clicked;

    /// <summary>The app's tray icon (one per app), for the taskbar's Tray module to handle clicks on it directly.</summary>
    public static TrayIcon? Instance { get; private set; }

    /// <summary>What a left click does: show CHROVIQ.</summary>
    public void Click() => Clicked?.Invoke();

    public TrayIcon(Func<IEnumerable<AppMenuItem>> getMenuItems)
    {
        Instance = this;
        _getMenuItems = getMenuItems;
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "CHROVIQ",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    // ---------- The menu ----------

    /// <summary>
    /// A tiny invisible window the menu belongs to: a tray menu only closes when you click elsewhere if its window
    /// is the active one, and CHROVIQ's main window may be hidden.
    /// </summary>
    private System.Windows.Window? _menuHost;

    /// <summary>The app menu in the modern menu look (Menu settings: colors, corners, font, material), at the mouse.</summary>
    public void ShowMenu()
    {
        _menuHost ??= new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, Width = 1, Height = 1, ShowActivated = true, Title = "CHROVIQ menu",
        };
        var cursor = Forms.Cursor.Position;
        _menuHost.Left = cursor.X;
        _menuHost.Top = cursor.Y;
        _menuHost.Show();
        _menuHost.Activate();

        var menu = AppMenuView.Create(_getMenuItems());
        menu.PlacementTarget = _menuHost;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => _menuHost?.Hide();
        menu.IsOpen = true;
    }

    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    /// <summary>The icon of this exe (the default app icon until the project gets its own).</summary>
    /// <summary>
    /// The app icon in the tray's own size (16 px, more with display scaling), straight from the .ico, so it's
    /// sharp instead of a shrunk 32 px icon.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CHROVIQ.ico"));
            if (resource != null)
                using (resource.Stream) return new Icon(resource.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception)
        {
            // Try the exe's icon below.
        }
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch (Exception)
        {
            // Fall through to the stock icon.
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false; // removes it from the tray right away instead of on mouse-over
        _icon.Dispose();
        _menuHost?.Close();
    }
}
