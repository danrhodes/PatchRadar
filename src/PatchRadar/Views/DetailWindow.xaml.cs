using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Scan;
using PatchRadar.Settings;

namespace PatchRadar.Views;

/// <summary>Shows one update's parsed KB data. Plain text only: remote HTML is never rendered.</summary>
public partial class DetailWindow : Window
{
    private readonly Func<UpdateRow, Task<UpdateRow?>> _refresh;
    private readonly ColorSettings _colors;
    private readonly ILog _log;
    private UpdateRow _row;

    public DetailWindow(UpdateRow row, ColorSettings colors, Func<UpdateRow, Task<UpdateRow?>> refresh, ILog log)
    {
        InitializeComponent();
        _row = row;
        _colors = colors;
        _refresh = refresh;
        _log = log;
        Render();
    }

    public string Kb => _row.Update.Kb;

    public void Update(UpdateRow row) { _row = row; Render(); }

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

    private static TextBlock Heading(string text) => Text(text, 14, FontWeights.SemiBold, null, new Thickness(0, 14, 0, 4));

    private void Render()
    {
        var u = _row.Update;
        Title = $"{u.Kb} - PatchRadar";
        Body.Children.Clear();

        Body.Children.Add(Text(u.Kb, 18, FontWeights.Bold));
        Body.Children.Add(Text(u.Title, 12, null));
        var meta = $"Installed {u.InstalledOn.LocalDateTime:d MMM yyyy HH:mm} · {u.State}";
        if (_row.Kb?.OsBuild is { } build) meta += $" · OS build {build}";
        Body.Children.Add(Text(meta, 11, null, Brushes.Gray));

        // Severity pill + keyword chips
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        chips.Children.Add(Chip(_row.Severity.ToString().ToUpperInvariant(), SeverityBrush(_row.Severity), Brushes.White));
        foreach (var k in _row.Result.MatchedKeywords)
            chips.Children.Add(Chip(k, new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), null));
        Body.Children.Add(chips);

        var kb = _row.Kb;
        if (kb is null || kb.Status != KbStatus.Ok)
        {
            Body.Children.Add(Heading(kb?.Status == KbStatus.NotFound ? "No KB article" : "KB data unavailable"));
            Body.Children.Add(Text(kb?.LastError ?? "Not fetched yet. Use Refresh or Scan now.", 12));
            if (kb?.NextRetryUtc is { } next)
                Body.Children.Add(Text($"Next automatic retry: {next.ToLocalTime():d MMM HH:mm}", 11, null, Brushes.Gray));
            return;
        }

        // Known issues
        Body.Children.Add(Heading($"Known issues ({_row.Result.ActiveIssues.Count} active)"));
        if (kb.KnownIssues.Count == 0) Body.Children.Add(Text("Microsoft lists no known issues for this update.", 12, null, Brushes.Gray));
        foreach (var i in kb.KnownIssues)
        {
            var resolved = i.Status == IssueStatus.Resolved;
            var fg = resolved ? Brushes.Gray : null;
            var box = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8) };
            var sp = new StackPanel();
            var head = string.IsNullOrWhiteSpace(i.Title) ? null : i.Title + (resolved ? "  [Resolved]" : "");
            if (head is not null) sp.Children.Add(Text(head, 12, FontWeights.SemiBold, fg));
            else if (resolved) sp.Children.Add(Text("[Resolved]", 12, FontWeights.SemiBold, fg));
            if (!string.IsNullOrWhiteSpace(i.AppliesTo)) sp.Children.Add(Text("Applies to: " + i.AppliesTo, 11, null, Brushes.Gray));
            sp.Children.Add(Highlighted("Symptom: " + i.Symptom, fg));
            if (i.Workaround.Length > 0) sp.Children.Add(Highlighted("Workaround: " + i.Workaround, fg));
            box.Child = sp;
            Body.Children.Add(box);
        }

        // Behaviour changes
        if (_row.Result.BehaviourChanges.Count > 0)
        {
            Body.Children.Add(Heading("Behaviour changes"));
            foreach (var c in _row.Result.BehaviourChanges)
            {
                var box = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(40, 214, 58, 58)),
                    BorderBrush = SeverityBrush(Severity.Red), BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 8),
                    Child = Highlighted(c.Text, null),
                };
                Body.Children.Add(box);
            }
        }

        // Improvements, collapsed by default
        var list = new StackPanel();
        foreach (var imp in kb.Improvements) { var t = Highlighted(imp.Text, null); t.Margin = new Thickness(0, 0, 0, 6); list.Children.Add(t); }
        Body.Children.Add(new Expander
        {
            Header = new TextBlock { Text = $"Improvements ({kb.Improvements.Count})", FontSize = 14, FontWeight = FontWeights.SemiBold },
            Margin = new Thickness(0, 14, 0, 0), IsExpanded = false, Content = list,
        });

        if (kb.Structure == KbStructure.Unknown)
            Body.Children.Add(Text("This page has no recognised KB structure; text was scanned for keywords only.", 11, null, Brushes.Gray, new Thickness(0, 10, 0, 0)));
        Body.Children.Add(Text($"Source: {kb.Source} · fetched {kb.FetchedUtc.ToLocalTime():d MMM HH:mm}", 11, null, Brushes.Gray, new Thickness(0, 12, 0, 0)));
    }

    private Border Chip(string text, Brush bg, Brush? fg)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(8, 2, 8, 2) };
        if (fg is not null) tb.Foreground = fg;
        return new Border { Background = bg, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 4), Child = tb };
    }

    /// <summary>Plain text with matched critical keywords in bold.</summary>
    private TextBlock Highlighted(string text, Brush? fg)
    {
        var tb = Text("", 12, null, fg);
        tb.Text = "";
        var kws = _row.Result.MatchedKeywords.Where(k => !k.StartsWith("re:", StringComparison.OrdinalIgnoreCase)).ToList();
        if (kws.Count == 0) { tb.Text = text; return tb; }
        var rx = new Regex(string.Join("|", kws.OrderByDescending(k => k.Length).Select(k => $@"(?<!\w){Regex.Escape(k)}(?!\w)")), RegexOptions.IgnoreCase);
        var pos = 0;
        foreach (Match m in rx.Matches(text))
        {
            if (m.Index > pos) tb.Inlines.Add(new System.Windows.Documents.Run(text[pos..m.Index]));
            tb.Inlines.Add(new System.Windows.Documents.Run(m.Value) { FontWeight = FontWeights.Bold });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new System.Windows.Documents.Run(text[pos..]));
        return tb;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => UrlGuard.OpenInBrowser(UrlGuard.KbUrl(_row.Update.Kb), _log);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SummaryFormatter.ForTicket(_row)); }
        catch (Exception ex) { _log.Warn("Clipboard failed: " + ex.Message); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var r = await _refresh(_row);
            if (r is not null) Update(r);
        }
        finally { RefreshButton.IsEnabled = true; }
    }
}
