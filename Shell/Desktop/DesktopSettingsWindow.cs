namespace WindowsCustomizer;

/// <summary>
/// "Desktop settings...": how the desktop's icons look (size, spacing, names) and which monitor has them.
/// Every change shows at once on the desktop (as the shell) and in the Desktop preview.
/// </summary>
internal sealed class DesktopSettingsWindow : SettingsForm
{
    public DesktopSettingsWindow(Action tryIt, Action monitorChanged)
        : base("Desktop settings", DesktopLayout.NotifyChanged, tryIt)
    {
        var layout = DesktopLayout.Current;

        Header("Icons");
        Slider("Icon size (pixels)", layout.IconPixels, 24, 128,
            v => layout.ChangeKeepingCells(() => layout.CustomIconPixels = (int)Math.Round(v)));
        Hint("The View menu's Large / Medium / Small set this back to 96 / 48 / 32.");
        Slider("Space across (pixels)", layout.SpacingX, 0, 80, v => layout.ChangeKeepingCells(() => layout.SpacingX = Math.Round(v)));
        Slider("Space down (pixels)", layout.SpacingY, 0, 80, v => layout.ChangeKeepingCells(() => layout.SpacingY = Math.Round(v)));

        Header("Names");
        Check("Show the names under the icons", layout.ShowLabels, v => layout.ChangeKeepingCells(() => layout.ShowLabels = v));
        Font("Font", layout.LabelFont ?? "Segoe UI", v => layout.LabelFont = v == "Segoe UI" ? null : v);
        Slider("Size", layout.LabelSize, 8, 24, v => layout.ChangeKeepingCells(() => layout.LabelSize = Math.Round(v)));
        ColorField("Color", layout.LabelColor, v => layout.LabelColor = string.IsNullOrWhiteSpace(v) ? "#FFFFFF" : v);
        Check("Soft shadow (readable on any wallpaper)", layout.LabelShadow, v => layout.LabelShadow = v);

        var monitors = Monitors.All();
        if (monitors.Count > 1)
        {
            Header("Monitor");
            var names = new[] { "The main monitor" }.Concat(monitors.Select((m, i) =>
                $"Monitor {i + 1}{(m.IsPrimary ? " (main)" : "")}: {m.Bounds.Right - m.Bounds.Left}×{m.Bounds.Bottom - m.Bounds.Top}")).ToArray();
            int selected = layout.IconMonitor == null ? 0 : monitors.FindIndex(m => m.Device == layout.IconMonitor) + 1;
            Combo("Icons on", names, Math.Max(0, selected), i =>
            {
                layout.IconMonitor = i <= 0 ? null : monitors[i - 1].Device;
                monitorChanged();
            });
            Hint("As your shell, the desktop with the icons moves to that monitor. The Desktop preview always shows them.");
        }
    }
}
