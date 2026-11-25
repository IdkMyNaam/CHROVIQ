using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCustomizer;

/// <summary>
/// "Report a problem" / "Ask a question": the person writes what happened (or their question), may give an email for
/// a reply, chooses whether the logs and PC details go along, sees exactly what's sent, and presses Send.
/// "Save as .zip instead" keeps the old way (nothing is sent).
/// </summary>
internal sealed class ReportWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ReportSender.Kind _kind;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _email = new();
    private readonly CheckBox _details = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
        Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _send = new() { Content = "Send", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 100 };

    public ReportWindow(ReportSender.Kind kind)
    {
        _kind = kind;
        Width = 640;
        Height = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        bool problem = kind == ReportSender.Kind.Problem;
        _details.Content = "Include the logs and PC details (helps a lot with problems)";
        _details.IsChecked = problem;

        var form = new StackPanel();
        form.Children.Add(Label(problem ? "What happened? What did you do just before, and what did you expect?" : "Your question"));
        form.Children.Add(_text);
        form.Children.Add(Label("Your email (optional: only if you'd like an answer)"));
        form.Children.Add(_email);
        form.Children.Add(_details);
        form.Children.Add(Label("This is exactly what's sent:"));
        form.Children.Add(_preview);
        form.Children.Add(FluentLook.Secondary(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
            Text = "It goes to the developer of CHROVIQ (as a private Discord message). Folder paths in the logs contain your Windows user name. " +
                   "Nothing is sent until you press Send.",
        }));
        form.Children.Add(_status);

        var zip = new Wpf.Ui.Controls.Button { Content = "Save as .zip instead", Margin = new Thickness(0, 0, 8, 0) };
        zip.Click += (_, _) => LogWindow.Report(this);
        var close = new Wpf.Ui.Controls.Button { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        _send.Click += async (_, _) => await SendAsync();
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_send, Dock.Right);
        buttons.Children.Add(close);
        buttons.Children.Add(_send);
        if (problem) buttons.Children.Add(zip); else buttons.Children.Add(new Border());

        var body = new DockPanel { Margin = new Thickness(16, 4, 16, 16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        FluentLook.Apply(this, problem ? "Report a problem" : "Ask a question", body);

        _text.TextChanged += (_, _) => UpdatePreview();
        _email.TextChanged += (_, _) => UpdatePreview();
        _details.Click += (_, _) => UpdatePreview();
        UpdatePreview();
        if (!ReportSender.IsSetUp)
        {
            _send.IsEnabled = false;
            _status.Text = "Sending isn't set up in this version yet." + (problem ? " Use \"Save as .zip instead\" and attach it to a message." : "");
        }
        Loaded += (_, _) => _text.Focus();
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };

    private void UpdatePreview()
    {
        _preview.Text = ReportSender.Compose(_kind, _text.Text, _email.Text, _details.IsChecked == true);
        _send.IsEnabled = ReportSender.IsSetUp && _text.Text.Trim().Length >= 5;
    }

    private async Task SendAsync()
    {
        string email = _email.Text.Trim();
        if (email.Length > 0 && (!email.Contains('@') || !email.Contains('.')))
        {
            _status.Text = "That email address doesn't look right (or leave it empty).";
            return;
        }
        _send.IsEnabled = false;
        _status.Text = "Sending...";
        var error = await ReportSender.SendAsync(_kind, _preview.Text, _text.Text, email);
        if (error != null)
        {
            _status.Text = error;
            _send.IsEnabled = true;
            return;
        }
        MessageBox.Show(this, _kind == ReportSender.Kind.Problem
                ? "Thanks! Your report was sent." + (email.Length > 0 ? " You'll get an answer at that email address." : "")
                : "Thanks! Your question was sent." + (email.Length > 0 ? " The answer comes to your email." : ""),
            "Sent", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }
}
