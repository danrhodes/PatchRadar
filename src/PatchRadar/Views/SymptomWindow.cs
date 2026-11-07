using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Scan;
using PatchRadar.Settings;

namespace PatchRadar.Views;

/// <summary>"Something broke": describe the problem and see which recent update most likely caused it.</summary>
public sealed class SymptomWindow : Window
{
    private readonly Func<IReadOnlyList<UpdateRow>> _rows;
    private readonly ColorSettings _colours;
    private readonly Action<UpdateRow> _openDetail;
    private readonly ComboBox _category = new();
    private readonly ComboBox _when = new();
    private readonly TextBox _text = new() { AcceptsReturn = false, MinWidth = 300 };
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 10, 8, 0) };

    private static readonly (string Label, int? Days)[] Onsets =
    {
        ("Today", 0), ("In the last few days", 3), ("About a week ago", 7), ("About two weeks ago", 14), ("About a month ago", 30), ("Not sure", null),
    };

    public SymptomWindow(Func<IReadOnlyList<UpdateRow>> rows, ColorSettings colours, Action<UpdateRow> openDetail)
    {
        _rows = rows; _colours = colours; _openDetail = openDetail;
        Title = "PatchRadar - something broke?";
        Width = 640; Height = 680; MinWidth = 460; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        foreach (var c in SymptomCorrelator.Categories) _category.Items.Add(c.Name);
        _category.SelectedIndex = 0;
        foreach (var o in Onsets) _when.Items.Add(o.Label);
        _when.SelectedIndex = 1;

        var find = new Button { Content = "Find the likely update", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        find.Click += (_, _) => Run();

        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "What went wrong?", FontWeight = FontWeights.SemiBold });
        form.Children.Add(_category);
        form.Children.Add(new TextBlock { Text = "Describe it (optional, a few words)", Margin = new Thickness(0, 8, 0, 0), Opacity = 0.8 });
        form.Children.Add(_text);
        form.Children.Add(new TextBlock { Text = "When did it start?", Margin = new Thickness(0, 8, 0, 0), Opacity = 0.8 });
        form.Children.Add(_when);
        form.Children.Add(find);
        form.Children.Add(new TextBlock
        {
            Text = "PatchRadar compares your symptom with the known issues Microsoft published for the updates installed on this PC, " +
                   "and with when each was installed. It is a lead, not proof.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(form, Dock.Top);
        root.Children.Add(form);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _results });
        Content = root;
    }

    private void Run()
    {
        var category = SymptomCorrelator.Categories[Math.Max(0, _category.SelectedIndex)];
        var keywords = SymptomCorrelator.Keywords(category, _text.Text);
        var now = DateTime.UtcNow;
        var days = Onsets[Math.Max(0, _when.SelectedIndex)].Days;
        var result = SymptomCorrelator.Find(_rows(), keywords, days is { } d ? now.AddDays(-d) : null, now);

        _results.Children.Clear();
        if (keywords.Count == 0)
        {
            _results.Children.Add(Note("Pick a category or type a few words so there is something to match."));
            return;
        }
        if (result.Suspects.Count == 0)
            _results.Children.Add(Note("No installed update has a published known issue that matches. That does not clear them: Microsoft does not document every problem."));

        foreach (var s in result.Suspects.Take(8)) _results.Children.Add(Card(s));

        if (result.RecentOthers.Count > 0)
        {
            _results.Children.Add(new TextBlock { Text = "Also installed recently (no known link to this symptom)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            foreach (var r in result.RecentOthers)
            {
                var b = new Button
                {
                    Content = $"{r.Update.Label}  {Shorten(r.Update.Title, 60)}  ({r.Update.InstalledOn.LocalDateTime:d MMM})",
                    HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 3),
                };
                var captured = r;
                b.Click += (_, _) => _openDetail(captured);
                _results.Children.Add(b);
            }
        }
    }

    private UIElement Card(Suspect s)
    {
        var row = s.Row;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Background = SeverityBrushes.For(row.Severity, _colours), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = row.Severity.ToString().ToUpperInvariant(), FontSize = 11, Foreground = Brushes.White, Margin = new Thickness(8, 2, 8, 2) },
        });
        head.Children.Add(new TextBlock { Text = row.Update.Label, FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = "  " + Shorten(row.Update.Title, 56), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel { Margin = new Thickness(4, 4, 0, 6) };
        foreach (var reason in s.Reasons)
            body.Children.Add(new TextBlock { Text = "• " + reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        foreach (var issue in s.Issues.Take(2))
        {
            var fix = issue.ResolvedInKb is not null ? $"Fix: {issue.ResolvedInKb}"
                : issue.Workaround.Length > 0 ? "Workaround: " + Shorten(issue.Workaround, 220)
                : issue.Resolution.Length > 0 ? "Resolution: " + Shorten(issue.Resolution, 220)
                : "Microsoft has not published a fix or workaround yet.";
            body.Children.Add(new TextBlock { Text = fix, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Opacity = 0.85, Margin = new Thickness(14, 2, 0, 0) });
        }
        var open = new Button { Content = "Open details", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => _openDetail(row);
        body.Children.Add(open);

        return new Expander { Header = head, Content = body, IsExpanded = true, Margin = new Thickness(0, 0, 0, 8) };
    }

    private static TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
