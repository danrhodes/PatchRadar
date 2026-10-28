using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PatchRadar.Core;
using PatchRadar.Export;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;

namespace PatchRadar.Views;

/// <summary>
/// The pending-update briefing, laid out like the update detail window (severity chips, advice bars, collapsible cards).
/// The Markdown version is still what Copy and "Save as Markdown" produce, ready for a ticket or change request.
/// </summary>
public sealed class BriefingWindow : Window
{
    private readonly ILog _log;
    private readonly ColorSettings _colors;
    private readonly string _markdown;
    private readonly Action<UpdateRow> _openDetail;
    private readonly StackPanel _body = new() { Margin = new Thickness(16, 12, 16, 16) };

    public BriefingWindow(IEnumerable<UpdateRow> pending, bool restartPending, string machine, ColorSettings colors, string markdown, Action<UpdateRow> openDetail, ILog log)
    {
        _log = log;
        _openDetail = openDetail;
        _colors = colors;
        _markdown = markdown;
        Title = "PatchRadar - pending update briefing";
        Width = 760; Height = 700; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        bar.Children.Add(Button("Copy as Markdown", (_, _) =>
        {
            try { Clipboard.SetText(_markdown); }
            catch (Exception ex) { _log.Warn("Clipboard failed: " + ex.Message); }
        }));
        bar.Children.Add(Button("Save as Markdown…", (_, _) => Save()));

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        Content = root;

        Render(pending.ToList(), restartPending, machine);
    }

    private Brush SeverityBrush(Severity s) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(s switch
    {
        Severity.Red => _colors.Red,
        Severity.Orange => _colors.Orange,
        Severity.Green => _colors.Green,
        _ => _colors.Grey,
    }));

    private static TextBlock Text(string text, double size = 12, FontWeight? weight = null, Brush? fg = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Margin = margin ?? new Thickness(0, 0, 0, 4) };
        if (fg is not null) t.Foreground = fg;
        return t;
    }

    private static TextBlock Label(string text) => Text(text, 11, FontWeights.Bold, Brushes.Gray, new Thickness(0, 8, 0, 2));

    private Border Chip(string text, Brush bg, Brush? fg)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(8, 2, 8, 2) };
        if (fg is not null) tb.Foreground = fg;
        return new Border { Background = bg, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 4), Child = tb };
    }

    private void Render(List<UpdateRow> rows, bool restartPending, string machine)
    {
        var live = rows.Where(r => !r.Ignored).OrderBy(Briefing.Rank).ThenBy(r => r.Update.Title).ToList();
        var ignored = rows.Where(r => r.Ignored).ToList();

        _body.Children.Add(Text($"Pending Windows updates on {machine}", 18, FontWeights.Bold));
        _body.Children.Add(Text($"Prepared {DateTime.Now:d MMM yyyy HH:mm} · {live.Count} update{(live.Count == 1 ? "" : "s")} waiting to be installed", 11, null, Brushes.Gray));

        if (restartPending)
            _body.Children.Add(Callout("A restart is already pending from an earlier install.", Severity.Orange));

        if (live.Count == 0)
        {
            _body.Children.Add(Text("Nothing is waiting to be installed.", 12, null, null, new Thickness(0, 12, 0, 0)));
        }
        else
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var s in new[] { Severity.Red, Severity.Orange, Severity.Grey, Severity.Green })
            {
                var n = live.Count(r => r.Severity == s);
                if (n > 0) chips.Children.Add(Chip($"{n} {Briefing.Label(s)}", SeverityBrush(s), Brushes.White));
            }
            _body.Children.Add(chips);
        }

        foreach (var r in live) _body.Children.Add(UpdateCard(r));

        if (ignored.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(4, 6, 0, 0) };
            foreach (var r in ignored) list.Children.Add(Text("• " + Briefing.Name(r), 12, null, Brushes.Gray));
            _body.Children.Add(new Expander
            {
                Header = new TextBlock { Text = $"Ignored ({ignored.Count})", FontSize = 14, FontWeight = FontWeights.SemiBold },
                Margin = new Thickness(0, 14, 0, 0), IsExpanded = false, Content = list,
            });
        }
    }

    private Border Callout(string text, Severity severity, FontWeight? weight = null) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        BorderBrush = SeverityBrush(severity), BorderThickness = new Thickness(2, 0, 0, 0),
        Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 8, 0, 0),
        Child = Text(text, 12, weight ?? FontWeights.SemiBold),
    };

    /// <summary>One update: severity chip and name in the header, open for red/orange, closed for the rest.</summary>
    private Expander UpdateCard(UpdateRow r)
    {
        var u = r.Update;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Chip(r.Severity.ToString().ToUpperInvariant(), SeverityBrush(r.Severity), Brushes.White));
        header.Children.Add(new TextBlock
        {
            Text = Briefing.Name(r), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel { Margin = new Thickness(4, 0, 0, 8) };
        body.Children.Add(Text($"Offered since {u.InstalledOn.LocalDateTime:d MMM yyyy} · {u.Category}", 11, null, Brushes.Gray));

        if (r.Advice is { } advice) body.Children.Add(Callout(advice, r.Severity));
        if (r.DriverNote is { } note) body.Children.Add(Text("Driver: " + note, 12, null, null, new Thickness(0, 6, 0, 0)));
        if (r.Result.MatchedKeywords.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var k in r.Result.MatchedKeywords)
                chips.Children.Add(Chip(k, new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), null));
            body.Children.Add(chips);
        }

        if (!u.HasKb)
            body.Children.Add(Text("Microsoft publishes no KB article for this update, so there are no known issues to list.", 12, null, Brushes.Gray, new Thickness(0, 8, 0, 0)));
        else if (r.Kb is not { Status: KbStatus.Ok })
            body.Children.Add(Text("The KB article could not be read: " + (r.Kb?.LastError ?? "not fetched yet") + ".", 12, null, Brushes.Gray, new Thickness(0, 8, 0, 0)));

        if (r.Result.BehaviourChanges.Count > 0)
        {
            body.Children.Add(Label("BEHAVIOUR CHANGES"));
            foreach (var c in r.Result.BehaviourChanges)
                body.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(40, 214, 58, 58)),
                    BorderBrush = SeverityBrush(Severity.Red), BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6),
                    Child = Text(Flat(c.Text), 12, null, null, new Thickness(0)),
                });
        }

        if (r.Result.ActiveIssues.Count > 0)
        {
            body.Children.Add(Label($"KNOWN ISSUES ({r.Result.ActiveIssues.Count} ACTIVE)"));
            foreach (var i in r.Result.ActiveIssues) body.Children.Add(IssueRow(i));
        }
        else if (u.HasKb && r.Kb is { Status: KbStatus.Ok })
            body.Children.Add(Text("No active known issues.", 12, null, Brushes.Gray, new Thickness(0, 8, 0, 0)));

        var open = new Button { Content = "Open details", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => _openDetail(r);
        body.Children.Add(open);

        return new Expander
        {
            Header = header, Content = body, Margin = new Thickness(0, 12, 0, 0),
            IsExpanded = r.Severity is Severity.Red or Severity.Orange,
        };
    }

    private Border IssueRow(KbIssue i)
    {
        var hasFix = i.ResolvedInKb is not null;
        var hasWorkaround = i.Workaround.Length + i.Resolution.Length > 0;
        var tag = hasFix ? (i.PartialFix ? "PARTIAL FIX" : "FIX AVAILABLE") : hasWorkaround ? "WORKAROUND" : "NO FIX YET";
        var accent = SeverityBrush(hasFix ? Severity.Green : hasWorkaround ? Severity.Orange : Severity.Red);

        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Chip(tag, accent, Brushes.White));
        if (!string.IsNullOrWhiteSpace(i.Title))
            head.Children.Add(new TextBlock { Text = i.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        sp.Children.Add(Text(Flat(i.Symptom), 12));
        if (i.Workaround.Length > 0) { sp.Children.Add(Label("WORKAROUND")); sp.Children.Add(Text(Flat(i.Workaround), 12)); }
        if (i.Resolution.Length > 0) { sp.Children.Add(Label("RESOLUTION (PER MICROSOFT)")); sp.Children.Add(Text(Flat(i.Resolution), 12)); }
        if (hasFix)
        {
            sp.Children.Add(Label(i.PartialFix ? "PARTIAL FIX" : "FIX"));
            sp.Children.Add(Text($"{i.ResolvedInKb}{(i.OutOfBandFix ? " (out-of-band)" : "")}", 12));
        }
        return new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 2, 4, 2),
            Margin = new Thickness(0, 0, 0, 8), Child = sp,
        };
    }

    private static string Flat(string s) => string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static Button Button(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += click;
        return b;
    }

    private void Save()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"PatchRadar-pending-{DateTime.Now:yyyyMMdd-HHmm}.md", Filter = "Markdown (*.md)|*.md|All files|*.*",
            DefaultExt = ".md", OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, _markdown, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Briefing save failed", ex);
            MessageBox.Show("Could not write the file: " + ex.Message, "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
