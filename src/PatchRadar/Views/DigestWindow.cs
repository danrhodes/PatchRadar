using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;

namespace PatchRadar.Views;

/// <summary>"What's changed since I last looked": new updates, edited articles, risk changes, newly named fixes.</summary>
public sealed class DigestWindow : Window
{
    public DigestWindow(IReadOnlyList<DigestItem> items, DateTime sinceUtc, ColorSettings colours, Action<UpdateRow> openDetail, Action markSeen)
    {
        Title = "PatchRadar - what's changed";
        Width = 600; Height = 640; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        list.Children.Add(new TextBlock
        {
            Text = $"Since {sinceUtc.ToLocalTime():d MMM yyyy HH:mm}",
            Opacity = 0.7, Margin = new Thickness(0, 0, 0, 10),
        });

        if (items.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Nothing has changed since you last looked.", FontSize = 14, Margin = new Thickness(0, 20, 0, 0) });
        }
        foreach (var group in items.GroupBy(i => i.Row.Update.Kb))
        {
            var row = group.First().Row;
            var sevBrush = SeverityBrushes.For(row.Severity, colours);

            // Header: severity chip + KB + title, like the cards in the briefing and the update info window.
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Background = sevBrush, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = row.Severity.ToString().ToUpperInvariant(), FontSize = 11, Foreground = Brushes.White, Margin = new Thickness(8, 2, 8, 2) },
            });
            head.Children.Add(new TextBlock { Text = row.Update.Kb, FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = "  " + Shorten(row.Update.Title, 60), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });

            var card = new StackPanel { Margin = new Thickness(4, 4, 0, 6) };
            foreach (var item in group)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                line.Inlines.Add(new System.Windows.Documents.Run(DigestBuilder.Label(item.Kind) + ": ") { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run(item.Text));
                card.Children.Add(line);

                // Every edit to the article, folded away so the card stays scannable.
                if (item.Kind == DigestKind.ArticleChanged && item.Diff is { Changes.Count: > 0 } d)
                {
                    var changes = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
                    foreach (var c in d.Changes)
                        changes.Children.Add(new Border
                        {
                            BorderBrush = ChangeBrush(c.Kind, colours), BorderThickness = new Thickness(2, 0, 0, 0),
                            Padding = new Thickness(8, 1, 4, 1), Margin = new Thickness(0, 0, 0, 5),
                            Child = new TextBlock { Text = $"{Sign(c.Kind)} {c.Area}: {Shorten(c.Title, 140)}", TextWrapping = TextWrapping.Wrap },
                        });
                    card.Children.Add(new Expander
                    {
                        Header = new TextBlock { Text = $"What changed in the article ({d.Changes.Count})", FontSize = 12, FontWeight = FontWeights.SemiBold },
                        Content = changes, IsExpanded = false, Margin = new Thickness(0, 4, 0, 0),
                    });
                }
            }

            var open = new Button { Content = "Open details", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) => openDetail(row);
            card.Children.Add(open);

            list.Children.Add(new Expander
            {
                Header = head, Content = card, Margin = new Thickness(0, 0, 0, 8),
                IsExpanded = row.Severity is Severity.Red or Severity.Orange,
            });
        }

        var seen = new Button { Content = "Mark all as seen", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = items.Count > 0 };
        seen.Click += (_, _) => { markSeen(); Close(); };
        var close = new Button { Content = "Close", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(seen);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        Content = root;
    }

    private static Brush ChangeBrush(ChangeKind k, ColorSettings c) =>
        SeverityBrushes.For(k switch { ChangeKind.Added => Severity.Green, ChangeKind.Removed => Severity.Red, _ => Severity.Orange }, c);

    private static string Sign(ChangeKind k) => k switch { ChangeKind.Added => "+", ChangeKind.Removed => "−", _ => "~" };
    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
