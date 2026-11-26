using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>The web widget part of a widget's settings: what it is, what it may use, its files and its commands.</summary>
internal sealed partial class WidgetSettingsWindow
{
    private void WebFields(DesktopWidget w, Action save)
    {
        var plugin = WidgetPlugins.Find(w.Plugin);
        if (plugin == null)
        {
            Hint(string.Format(Loc.T("This widget's folder can't be found ({0})."), w.Plugin ?? "?"));
            return;
        }
        var m = plugin.Manifest;
        string title = plugin.Name + (string.IsNullOrWhiteSpace(m.Version) ? "" : " " + m.Version.Trim());
        if (!string.IsNullOrWhiteSpace(m.Author)) title += " · " + string.Format(Loc.T("by {0}"), m.Author.Trim());
        Hint(title);
        if (!string.IsNullOrWhiteSpace(m.Description)) Hint(m.Description.Trim());
        Hint(plugin.OriginText);

        // ---------- What it may use ----------
        Header("What it may use");
        var data = new List<string>();
        if (plugin.Wants("stats")) data.Add(Loc.T("CPU, memory and GPU use"));
        if (plugin.Wants("media")) data.Add(Loc.T("what's playing (with play/pause)"));
        if (plugin.Wants("weather")) data.Add(Loc.T("the weather"));
        Hint(data.Count == 0 ? Loc.T("No data from CHROVIQ.") : Loc.T("Data from CHROVIQ:") + " " + string.Join(", ", data) + ".");
        var network = plugin.NetworkOrigins;
        Hint(network.Count == 0
            ? Loc.T("No internet or network: everything else is blocked.")
            : Loc.T("It may connect to these addresses, and nothing else:") + " " + string.Join(", ", network));
        Hint(Loc.T("It can't read your files, start programs, open windows, download, or use the camera, microphone or location."));

        if (plugin.Wants("weather"))
        {
            Text("Place", w.City ?? "", v =>
            {
                if (string.Equals(v.Trim(), w.City, StringComparison.CurrentCultureIgnoreCase)) return;
                w.City = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                w.Latitude = w.Longitude = null;
                w.Place = null;
            }, "For its weather: a city or town. The weather comes from Open-Meteo; only the place is sent.");
            Check("Fahrenheit (and mph)", w.Fahrenheit, v => w.Fahrenheit = v);
        }

        // ---------- Files you choose for it ----------
        if (m.Files.Count > 0)
        {
            Header("Files it reads");
            Hint("It can only read the files you choose here, and sees them again whenever they change.");
            foreach (var file in m.Files)
            {
                var box = new TextBox { Text = w.PluginFiles?.GetValueOrDefault(file.Id) ?? "", Margin = new Thickness(0, 0, 8, 0) };
                box.TextChanged += (_, _) =>
                {
                    (w.PluginFiles ??= new())[file.Id] = box.Text.Trim().Trim('"');
                    save();
                };
                var browse = new Wpf.Ui.Controls.Button { Content = Loc.T("Browse...") };
                browse.Click += (_, _) =>
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Title = file.Label };
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
                };
                var row = new DockPanel();
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
                row.Children.Add(box);
                AddCard(string.IsNullOrWhiteSpace(file.Label) ? file.Id : file.Label, row);
            }
        }

        // ---------- Commands ----------
        if (m.Commands.Count > 0)
        {
            Header("Commands");
            Add(new Wpf.Ui.Controls.InfoBar
            {
                Title = Loc.T("Commands can do anything on your PC"),
                Message = Loc.T("This widget wants to run the commands below and show what they print. A command runs with your rights: it can read, change or delete your files. Only allow it if you trust whoever made this widget, and you understand the commands."),
                Severity = plugin.Origin == WidgetOrigin.BuiltIn ? Wpf.Ui.Controls.InfoBarSeverity.Informational : Wpf.Ui.Controls.InfoBarSeverity.Warning,
                IsOpen = true, IsClosable = false, Margin = new Thickness(0, 0, 0, 8),
            });
            Hint(plugin.OriginText);
            foreach (var command in m.Commands)
            {
                string every = command.EverySeconds > 0
                    ? string.Format(Loc.T("every {0} seconds"), Math.Clamp(command.EverySeconds, 2, 3600))
                    : Loc.T("when the widget asks");
                AddCard($"{(string.IsNullOrWhiteSpace(command.Label) ? command.Id : command.Label)} ({every})", new TextBox
                {
                    Text = command.Run, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                });
            }

            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            void ShowStatus() => status.Text =
                w.ApprovedCommands == plugin.CommandsFingerprint ? Loc.T("You approved these commands.")
                : w.ApprovedCommands != null ? Loc.T("The commands changed since you approved them: they don't run until you approve them again.")
                : Loc.T("Not approved: these commands don't run.");
            ShowStatus();

            var approve = new Wpf.Ui.Controls.Button { Margin = new Thickness(0, 0, 8, 8) };
            void ShowButton() => approve.Content = Loc.T(w.ApprovedCommands == plugin.CommandsFingerprint ? "Withdraw approval" : "Approve these commands...");
            ShowButton();
            approve.Click += (_, _) =>
            {
                if (w.ApprovedCommands == plugin.CommandsFingerprint) w.ApprovedCommands = null;
                else
                {
                    string list = string.Join("\n", m.Commands.Select(c => "  " + c.Run));
                    string question = string.Format(Loc.T("Allow \"{0}\" to run these commands?"), plugin.Name) + "\n\n" + list + "\n\n" + plugin.OriginText +
                                      "\n\n" + Loc.T("They run with your rights. Only say Yes if you trust whoever made this widget.");
                    if (MessageBox.Show(this, question, "CHROVIQ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    w.ApprovedCommands = plugin.CommandsFingerprint;
                }
                ShowStatus();
                ShowButton();
                save();
            };
            Add(status);
            Add(approve);

            var allow = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = WidgetManager.CommandsAllowed, Content = Loc.T("Let widgets run commands (all widgets)") };
            allow.Click += (_, _) =>
            {
                if (allow.IsChecked == true &&
                    MessageBox.Show(this, Loc.T("Turn on commands for widgets? Even then, a widget's commands only run once you've approved them."),
                        "CHROVIQ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    allow.IsChecked = false;
                    return;
                }
                WidgetManager.CommandsAllowed = allow.IsChecked == true;
            };
            Add(allow);
            Hint("Off by default. Both are needed: commands turned on here, and this widget's commands approved.");
        }

        // ---------- Making and sharing ----------
        Header("Making widgets");
        Check("Developer tools (F12)", w.DeveloperTools, v => w.DeveloperTools = v);
        Hint("For making widgets: F12 opens the browser's tools, and F5 reloads the page.");
        var buttons = new WrapPanel();
        var folder = new Wpf.Ui.Controls.Button { Content = Loc.T("Open its folder"), Margin = new Thickness(0, 0, 8, 8) };
        folder.Click += (_, _) => ShellLaunch.OpenFolder(plugin.Folder);
        var export = new Wpf.Ui.Controls.Button { Content = Loc.T("Export (.zip)..."), Margin = new Thickness(0, 0, 8, 8) };
        export.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = plugin.Name + ".zip", Filter = "Zip (*.zip)|*.zip" };
            if (dialog.ShowDialog(this) != true) return;
            try { WidgetPlugins.Export(plugin, dialog.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "CHROVIQ", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        buttons.Children.Add(folder);
        buttons.Children.Add(export);
        Add(buttons);
    }
}
