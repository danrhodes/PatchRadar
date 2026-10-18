using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Uninstall;

namespace PatchRadar.Views;

/// <summary>Shows one update's parsed KB data. Plain text only: remote HTML is never rendered.</summary>
public partial class DetailWindow : Window
{
    private readonly Func<UpdateRow, Task<UpdateRow?>> _refresh;
    private readonly Func<UpdateRow, bool, UpdateRow> _setReviewed;
    private readonly Func<string, bool> _isInstalled;
    private readonly UninstallService? _uninstall;
    private readonly Action? _onUninstalled;
    private UninstallPlan? _plan;
    private readonly ColorSettings _colors;
    private readonly ILog _log;
    private UpdateRow _row;

    public DetailWindow(UpdateRow row, ColorSettings colors, Func<UpdateRow, Task<UpdateRow?>> refresh,
        Func<UpdateRow, bool, UpdateRow> setReviewed, Func<string, bool> isInstalled, ILog log,
        UninstallService? uninstall = null, Action? onUninstalled = null)
    {
        InitializeComponent();
        _row = row;
        _colors = colors;
        _refresh = refresh;
        _setReviewed = setReviewed;
        _isInstalled = isInstalled;
        _uninstall = uninstall;
        _onUninstalled = onUninstalled;
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
        if (_row.FixAvailableNotInstalled)
            chips.Children.Add(Chip("⬆ Fix available: " + string.Join(", ", _row.FixesNotInstalled.Select(f => f.Kb).Distinct()), SeverityBrush(Severity.Orange), Brushes.White));
        foreach (var k in _row.Result.MatchedKeywords)
            chips.Children.Add(Chip(k, new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), null));
        Body.Children.Add(chips);

        // The Uninstall button exists only when the feature is on, the user is an administrator, and removal is really possible.
        _plan = _uninstall?.PlanIfOffered(_row.Update);
        UninstallButton.Visibility = _plan is null ? Visibility.Collapsed : Visibility.Visible;
        if (_plan is not null) { UninstallButton.Background = SeverityBrush(Severity.Red); UninstallButton.Foreground = Brushes.White; }

        // A reviewed update whose article has since changed must not look settled.
        ReviewButton.Content = _row.Review == ReviewStatus.None ? "Mark reviewed" : "Mark not reviewed";
        ReviewButton.IsEnabled = _row.Review != ReviewStatus.None || _row.Kb is { Status: KbStatus.Ok };
        if (_row.Review == ReviewStatus.ChangedSinceReviewed)
        {
            Body.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(48, 224, 138, 30)),
                BorderBrush = SeverityBrush(Severity.Orange), BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 8, 0, 0),
                Child = Text("Changed since reviewed" + (_row.ReviewedUtc is { } at ? $" on {at.ToLocalTime():d MMM yyyy}" : "") +
                             ". Microsoft has edited this article. Read it again, then mark it reviewed.", 12, FontWeights.SemiBold),
            });
            if (_row.ChangeSinceReview is { IsEmpty: false } diff) Body.Children.Add(DiffView(diff));
        }
        if (_row.Review == ReviewStatus.Reviewed && _row.ReviewedUtc is { } reviewedAt)
        {
            Body.Children.Add(Text($"Reviewed {reviewedAt.ToLocalTime():d MMM yyyy HH:mm}", 11, null, Brushes.Gray, new Thickness(0, 4, 0, 0)));
        }


        var kb = _row.Kb;
        if (kb is null || kb.Status != KbStatus.Ok)
        {
            Body.Children.Add(Heading(kb?.Status == KbStatus.NotFound ? "No KB article" : "KB data unavailable"));
            Body.Children.Add(Text(kb?.LastError ?? "Not fetched yet. Use Refresh or Scan now.", 12));
            if (kb?.NextRetryUtc is { } next)
                Body.Children.Add(Text($"Next automatic retry: {next.ToLocalTime():d MMM HH:mm}", 11, null, Brushes.Gray));
            return;
        }

        // At a glance: what is wrong, and is there a way out?
        Body.Children.Add(AtAGlance(kb));

        // Known issues: one collapsible card each, active ones open, resolved ones closed.
        Body.Children.Add(Heading($"Known issues ({_row.Result.ActiveIssues.Count} active)"));
        if (kb.KnownIssues.Count == 0) Body.Children.Add(Text("Microsoft lists no known issues for this update.", 12, null, Brushes.Gray));
        foreach (var i in kb.KnownIssues) Body.Children.Add(IssueCard(i));

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
        if (!string.IsNullOrWhiteSpace(kb.Notice))
            Body.Children.Add(Text(kb.Notice, 11, null, Brushes.Gray, new Thickness(0, 10, 0, 0)));
        Body.Children.Add(Text($"Source: {kb.Source} · fetched {kb.FetchedUtc.ToLocalTime():d MMM HH:mm}", 11, null, Brushes.Gray, new Thickness(0, 12, 0, 0)));
    }



    // ---- before and after ------------------------------------------------------------------------

    private Expander DiffView(KbDiffResult diff)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 6, 0, 0) };
        foreach (var c in diff.Changes)
        {
            var sign = c.Kind switch { ChangeKind.Added => "+ ADDED", ChangeKind.Removed => "− REMOVED", _ => "~ CHANGED" };
            var brush = c.Kind switch { ChangeKind.Added => SeverityBrush(Severity.Green), ChangeKind.Removed => SeverityBrush(Severity.Red), _ => SeverityBrush(Severity.Orange) };
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var head = Text("", 12, FontWeights.SemiBold);
            head.Inlines.Add(new Run(sign + "  ") { Foreground = brush });
            head.Inlines.Add(new Run($"{c.Area}: {c.Title}"));
            box.Children.Add(head);

            if (c.Fields is { Count: > 0 })
                foreach (var f in c.Fields)
                {
                    box.Children.Add(Label(f.Field.ToUpperInvariant()));
                    box.Children.Add(BeforeAfter(f.Before, f.After));
                }
            else if (c.Kind == ChangeKind.Changed) box.Children.Add(BeforeAfter(c.Before ?? "", c.After ?? ""));
            else box.Children.Add(Text(Shorten(c.After ?? c.Before ?? "", 500), 12, null, Brushes.Gray));
            panel.Children.Add(box);
        }
        return new Expander
        {
            Header = new TextBlock { Text = $"What changed since you reviewed it ({diff.Summary})", FontSize = 13, FontWeight = FontWeights.SemiBold },
            Content = panel, IsExpanded = true, Margin = new Thickness(0, 8, 0, 0),
        };
    }

    private Border BeforeAfter(string before, string after)
    {
        var g = new StackPanel();
        g.Children.Add(Side("BEFORE", before.Length == 0 ? "(nothing)" : Shorten(before, 600), SeverityBrush(Severity.Red)));
        g.Children.Add(Side("AFTER", after.Length == 0 ? "(nothing)" : Shorten(after, 600), SeverityBrush(Severity.Green)));
        return new Border { Child = g };
    }

    private static Border Side(string label, string text, Brush accent)
    {
        var sp = new StackPanel();
        sp.Children.Add(Text(label, 10, FontWeights.Bold, accent, new Thickness(0, 0, 0, 1)));
        sp.Children.Add(Text(text, 12));
        return new Border
        {
            BorderBrush = accent, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 2, 4, 2),
            Margin = new Thickness(0, 0, 0, 4), Child = sp,
        };
    }

    // ---- known issue presentation ------------------------------------------------------------

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    private Border AtAGlance(KbData kb)
    {
        var active = kb.KnownIssues.Where(i => i.Status == IssueStatus.Active).ToList();
        var stack = new StackPanel();
        stack.Children.Add(Text("At a glance", 13, FontWeights.SemiBold, null, new Thickness(0, 0, 0, 4)));

        if (active.Count == 0)
        {
            stack.Children.Add(Text(kb.KnownIssues.Count == 0 ? "No known issues listed by Microsoft." : "All listed known issues are resolved.", 12));
        }
        else
        {
            stack.Children.Add(Text($"{Plural(active.Count, "active known issue", "active known issues")}.", 12, FontWeights.SemiBold));
            var withFix = active.Where(i => i.ResolvedInKb is not null).ToList();
            var withWorkaround = active.Where(i => i.Workaround.Length > 0 || i.Resolution.Length > 0).ToList();
            var nothing = active.Where(i => i.ResolvedInKb is null && i.Workaround.Length == 0 && i.Resolution.Length == 0).ToList();

            // (fix lines for every issue, active or resolved, are added below the active-issue summary)
            var wonly = withWorkaround.Where(i => i.ResolvedInKb is null).ToList();
            if (wonly.Count > 0) stack.Children.Add(Text($"✔ {Plural(wonly.Count, "issue has", "issues have")} a workaround or mitigation from Microsoft (see below).", 12));
            if (nothing.Count > 0) stack.Children.Add(Text($"✖ {Plural(nothing.Count, "issue has", "issues have")} no fix or workaround published yet.", 12, null, SeverityBrush(Severity.Orange)));
        }

        // Fixes: one line per fixing update, with whether this PC already has it. Covers resolved issues too,
        // because "resolved in KB..." only helps a machine that has installed that KB.
        foreach (var g in _row.Fixes.GroupBy(f => f.Kb))
        {
            var first = g.First();
            var installed = g.All(f => f.Installed);
            var partial = g.All(f => f.Partial);
            var line = Text("", 12);
            line.Inlines.Add(new Run($"{(installed ? "✔" : "⬆")} {Plural(g.Count(), "issue", "issues")} {(partial ? "partly " : "")}fixed by "));
            line.Inlines.Add(KbLink(g.Key));
            line.Inlines.Add(new Run(first.OutOfBand ? " (out-of-band). " : ". "));
            line.Inlines.Add(new Run(installed ? "Installed on this PC." : "NOT installed on this PC.")
                { FontWeight = FontWeights.SemiBold, Foreground = installed ? SeverityBrush(Severity.Green) : SeverityBrush(Severity.Orange) });
            if (!installed && _row.NewerCumulativeInstalled)
                line.Inlines.Add(new Run(" A newer cumulative update is installed and probably already includes it."){ Foreground = Brushes.Gray });
            stack.Children.Add(line);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 12, 0, 0),
            Child = stack,
        };
    }

    private Inline KbLink(string kb)
    {
        var link = new Hyperlink(new Run(kb)) { ToolTip = UrlGuard.KbUrl(kb) };
        link.Click += (_, _) => UrlGuard.OpenInBrowser(UrlGuard.KbUrl(kb), _log);
        return link;
    }

    private static TextBlock Label(string text) => Text(text, 11, FontWeights.Bold, Brushes.Gray, new Thickness(0, 8, 0, 2));

    private Expander IssueCard(KbIssue i)
    {
        var resolved = i.Status == IssueStatus.Resolved;
        var title = string.IsNullOrWhiteSpace(i.Title) ? Shorten(i.Symptom, 90) : i.Title!;
        var fg = resolved ? Brushes.Gray : null;

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var fixMissing = i.ResolvedInKb is not null && !_isInstalled(i.ResolvedInKb);
        var tag = resolved && fixMissing ? "FIX NOT INSTALLED" : resolved ? "RESOLVED" : i.PartialFix ? "PARTIAL FIX" : i.ResolvedInKb is not null ? "FIX AVAILABLE" : i.Workaround.Length + i.Resolution.Length > 0 ? "WORKAROUND" : "ACTIVE";
        var tagBrush = resolved && fixMissing ? SeverityBrush(Severity.Orange) : resolved ? SeverityBrush(Severity.Grey) : i.ResolvedInKb is not null ? SeverityBrush(Severity.Green) : i.Workaround.Length + i.Resolution.Length > 0 ? SeverityBrush(Severity.Orange) : SeverityBrush(Severity.Red);
        header.Children.Add(Chip(tag, tagBrush, Brushes.White));
        var titleBlock = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center };
        if (fg is not null) titleBlock.Foreground = fg;      // null would override the theme colour with "no brush"
        header.Children.Add(titleBlock);

        var body = new StackPanel { Margin = new Thickness(4, 0, 0, 4) };
        if (!string.IsNullOrWhiteSpace(i.AppliesTo)) body.Children.Add(Text("Applies to: " + i.AppliesTo, 11, null, Brushes.Gray));

        body.Children.Add(Label("SYMPTOM"));
        body.Children.Add(Highlighted(i.Symptom, fg));

        if (i.ResolvedInKb is not null)
        {
            body.Children.Add(Label(i.PartialFix ? "PARTIAL FIX" : "FIX"));
            var fix = Text("", 12);
            fix.Inlines.Add(new Run((i.OutOfBandFix ? "Out-of-band update " : "Update ")));
            fix.Inlines.Add(KbLink(i.ResolvedInKb));
            var inst = _isInstalled(i.ResolvedInKb);
            fix.Inlines.Add(new Run(inst ? "  ✔ installed on this PC" : "  ✖ not installed on this PC")
                { FontWeight = FontWeights.SemiBold, Foreground = inst ? SeverityBrush(Severity.Green) : SeverityBrush(Severity.Orange) });
            body.Children.Add(fix);
        }
        AddSection(body, "WORKAROUND", i.Workaround, fg);
        AddSection(body, "RESOLUTION (PER MICROSOFT)", i.Resolution, fg);
        AddSection(body, "NEXT STEPS", i.NextSteps, fg);
        if (i.ResolvedInKb is null && i.Workaround.Length == 0 && i.Resolution.Length == 0 && !resolved)
        {
            body.Children.Add(Label("FIX / WORKAROUND"));
            body.Children.Add(Text("None published yet. Use Refresh later; Microsoft updates these articles.", 12, null, Brushes.Gray));
        }

        return new Expander
        {
            Header = header, Content = body, IsExpanded = !resolved || fixMissing,
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    /// <summary>Long guidance (registry steps, policy walkthroughs) folds away so the window stays scannable.</summary>
    private void AddSection(StackPanel body, string label, string text, Brush? fg)
    {
        if (text.Length == 0) return;
        body.Children.Add(Label(label));
        if (text.Length <= 450) { body.Children.Add(Highlighted(text, fg)); return; }
        body.Children.Add(Highlighted(Shorten(text, 300), fg));
        body.Children.Add(new Expander
        {
            Header = new TextBlock { Text = "Show full text", FontSize = 11 },
            Content = Highlighted(text, fg), IsExpanded = false, Margin = new Thickness(0, 2, 0, 0),
        });
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

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

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstall is null || _plan is null) return;
        var confirm = new UninstallConfirmWindow(_row, _plan, Environment.UserName) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        UninstallButton.IsEnabled = false;
        Title = $"{_row.Update.Kb} - removing…";
        try
        {
            var result = await _uninstall.ExecuteAsync(_row.Update, confirm.Reason, CancellationToken.None);
            MessageBox.Show(this, result.Message, result.Succeeded ? "Update removed" : "Update not removed",
                MessageBoxButton.OK, result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (result.Succeeded) _onUninstalled?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("Uninstall failed", ex);
            MessageBox.Show(this, ex.Message, "Update not removed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UninstallButton.IsEnabled = true;
            Title = $"{_row.Update.Kb} - PatchRadar";
        }
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        try { Update(_setReviewed(_row, _row.Review == ReviewStatus.None)); }
        catch (Exception ex) { _log.Error("Mark reviewed failed", ex); }
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
