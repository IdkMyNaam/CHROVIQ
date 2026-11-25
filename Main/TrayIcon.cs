using System.Drawing;
using Forms = System.Windows.Forms;

namespace WindowsCustomizer;

/// <summary>Notification-area icon with the app menu (see <see cref="AppMenuItem"/>).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Func<IEnumerable<AppMenuItem>> _getMenuItems;

    /// <summary>Left-click on the icon.</summary>
    public event Action? Clicked;

    /// <param name="getMenuItems">Called each time the menu opens, so it always shows the current state.</param>
    public TrayIcon(Func<IEnumerable<AppMenuItem>> getMenuItems)
    {
        _getMenuItems = getMenuItems;

        _menu.Renderer = new HoverRenderer();
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
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
        };
    }

    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        foreach (var entry in _getMenuItems())
        {
            if (entry.IsSeparator)
            {
                _menu.Items.Add(new Forms.ToolStripSeparator());
                continue;
            }

            var item = new Forms.ToolStripMenuItem(entry.Text)
            {
                Checked = entry.IsChecked,
                Enabled = entry.IsEnabled,
                ShortcutKeyDisplayString = entry.Shortcut,
            };
            var onClick = entry.OnClick;
            item.Click += (_, _) => onClick?.Invoke();
            // Highlight on hover even when the menu isn't the active window (opened from our own tray).
            item.MouseEnter += (_, _) => { HoverRenderer.Hovered = item; item.Invalidate(); };
            item.MouseLeave += (_, _) => { if (HoverRenderer.Hovered == item) HoverRenderer.Hovered = null; item.Invalidate(); };
            _menu.Items.Add(item);
        }
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

    /// <summary>The standard menu look, plus a highlight for the item under the mouse.</summary>
    private sealed class HoverRenderer : Forms.ToolStripProfessionalRenderer
    {
        public static Forms.ToolStripItem? Hovered;

        private static readonly Color Highlight = Color.FromArgb(0xE5, 0xF1, 0xFB); // Windows' light blue
        private static readonly Color HighlightBorder = Color.FromArgb(0xCC, 0xE4, 0xF7);

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Enabled && (e.Item.Selected || e.Item == Hovered))
            {
                var area = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height - 1);
                using var fill = new SolidBrush(Highlight);
                using var border = new Pen(HighlightBorder);
                e.Graphics.FillRectangle(fill, area);
                e.Graphics.DrawRectangle(border, area);
                return;
            }
            base.OnRenderMenuItemBackground(e);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false; // removes it from the tray right away instead of on mouse-over
        _icon.Dispose();
        _menu.Dispose();
    }
}
