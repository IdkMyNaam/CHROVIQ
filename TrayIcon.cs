using System.Drawing;
using Forms = System.Windows.Forms;

namespace WindowsCustomizer;

/// <summary>Notification-area icon with a menu: profiles, next profile, open, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Func<IEnumerable<Profile>> _getProfiles;
    private readonly Func<Profile?> _getCurrent;

    public event Action<Profile>? ProfileClicked;
    public event Action? NextClicked;
    public event Action? OpenClicked;
    public event Action? ExitClicked;

    /// <param name="getProfiles">Read each time the menu opens, so it always shows the current list.</param>
    /// <param name="getCurrent">The profile to show with a check mark.</param>
    /// <param name="hotKeyText">Shown next to "Next profile", for example "Ctrl+Alt+P".</param>
    public TrayIcon(Func<IEnumerable<Profile>> getProfiles, Func<Profile?> getCurrent, string hotKeyText)
    {
        _getProfiles = getProfiles;
        _getCurrent = getCurrent;
        HotKeyText = hotKeyText;

        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false; // WinForms cancels opening when the menu was empty beforehand
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Windows Customizer",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenClicked?.Invoke();
        };
    }

    public string HotKeyText { get; set; }

    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var current = _getCurrent();

        foreach (var profile in _getProfiles())
        {
            var item = new Forms.ToolStripMenuItem(profile.Name) { Checked = ReferenceEquals(profile, current) };
            var p = profile; // capture this profile, not the loop variable
            item.Click += (_, _) => ProfileClicked?.Invoke(p);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Next profile", null, (_, _) => NextClicked?.Invoke())
        {
            ShortcutKeyDisplayString = HotKeyText,
        });
        _menu.Items.Add(new Forms.ToolStripMenuItem("Open", null, (_, _) => OpenClicked?.Invoke()));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitClicked?.Invoke()));
    }

    /// <summary>The icon of this exe (the default app icon until the project gets its own).</summary>
    private static Icon LoadAppIcon()
    {
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
        _menu.Dispose();
    }
}
