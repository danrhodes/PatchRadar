using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PatchRadar.Infra;

namespace PatchRadar.Views;

/// <summary>Shows exactly what would be reported, then opens GitHub in the browser. Nothing leaves this PC from here except a search for the hash.</summary>
public sealed class ProblemReportWindow : Window
{
    private readonly ProblemReport _report;
    private readonly ProblemLookup _lookup;
    private readonly ProblemStore _store;
    private readonly ILog _log;
    private readonly BitmapSource? _screenshot;
    private readonly CheckBox _includeShot = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _report_ = new() { Content = "Check GitHub and report", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.Gray };

    public ProblemReportWindow(ProblemReport report, ProblemLookup lookup, ProblemStore store, ILog log, BitmapSource? screenshot = null)
    {
        _report = report; _lookup = lookup; _store = store; _log = log; _screenshot = screenshot;
        Title = "PatchRadar - report a problem";
        Width = 760; Height = 640; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "PatchRadar noticed an error in its log. Below is exactly what would go into a GitHub issue, with your user name, computer name and personal paths removed. " +
                   "Pressing the button checks whether this problem is already reported, then opens GitHub in your browser. Nothing is posted until you press Submit there, and you can edit the text first.",
        };
        var text = new TextBox
        {
            Text = report.Body, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true,
        };

        var top = new StackPanel { Margin = new Thickness(16, 16, 16, 0) };
        top.Children.Add(new TextBlock { Text = report.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        top.Children.Add(intro);

        var bottom = new StackPanel { Margin = new Thickness(16, 8, 16, 16) };
        if (screenshot is not null)
        {
            _includeShot.Content = "Include a screenshot of the PatchRadar window (it is copied to the clipboard; paste it into the issue with Ctrl+V). Check the preview first: it may show update names.";
            _includeShot.Content = new TextBlock { Text = (string)_includeShot.Content, TextWrapping = TextWrapping.Wrap };
            bottom.Children.Add(_includeShot);
            bottom.Children.Add(new Image { Source = screenshot, MaxHeight = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Stretch = Stretch.Uniform });
        }
        bottom.Children.Add(_status);

        var dismiss = new Button { Content = "Don't ask about this again", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        var later = new Button { Content = "Not now", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(dismiss); buttons.Children.Add(later); buttons.Children.Add(_report_);
        bottom.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        text.Margin = new Thickness(16, 0, 16, 0);
        root.Children.Add(text);
        Content = root;

        dismiss.Click += (_, _) => { _store.Set(_report.Hash, ProblemStatus.Dismissed); Close(); };
        later.Click += (_, _) => Close();
        _report_.Click += async (_, _) => await ReportAsync();
    }

    private async Task ReportAsync()
    {
        _report_.IsEnabled = false;
        _status.Text = "Checking GitHub for an existing report of this problem…";
        var existing = await _lookup.FindAsync(_report.Hash, CancellationToken.None);
        if (existing is not null)
        {
            _store.Set(_report.Hash, ProblemStatus.Reported, existing.Number);
            _log.Info($"Problem {_report.Hash} is already reported as #{existing.Number}");
            Open(existing.Url);
            MessageBox.Show(this,
                $"This problem is already reported as issue #{existing.Number} ({(existing.Open ? "open" : "closed, so a newer version may already fix it")}). I have opened it. " +
                "You can add a comment there if you have more to say.", "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        var shot = _screenshot is not null && _includeShot.IsChecked == true;
        if (shot)
        {
            try { Clipboard.SetImage(_screenshot); }
            catch (System.Runtime.InteropServices.ExternalException ex) { shot = false; _log.Warn("Could not copy the screenshot: " + ex.Message); }
        }
        _store.Set(_report.Hash, ProblemStatus.Reported);
        Open(ProblemIssue.NewIssueUrl(_report));
        MessageBox.Show(this,
            "GitHub is opening in your browser with the report filled in. Read it, edit it if you like, and press Submit new issue." +
            (shot ? "\n\nThe screenshot is on your clipboard: click in the description box and press Ctrl+V to add it." : ""),
            "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void Open(string url)
    {
        if (!ProblemIssue.IsIssuePage(url)) { _log.Warn("Refused to open " + url); return; }
        if (UrlGuard.Launch(url, _log)) return;
        UrlGuard.ShowLinkFallback(url);
    }
}
