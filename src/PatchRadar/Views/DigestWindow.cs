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
            var card = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = "● ", Foreground = SeverityBrushes.For(row.Severity, colours), FontSize = 14 });
            head.Children.Add(new TextBlock { Text = row.Update.Kb, FontWeight = FontWeights.Bold, FontSize = 14 });
            head.Children.Add(new TextBlock { Text = "  " + Shorten(row.Update.Title, 70), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(head);

            foreach (var item in group)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 3, 0, 0) };
                line.Inlines.Add(new System.Windows.Documents.Run(DigestBuilder.Label(item.Kind) + ": ") { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run(item.Text));
                card.Children.Add(line);
                if (item.Kind == DigestKind.ArticleChanged && item.Diff is { } d)
                    foreach (var c in d.Changes.Take(4))
                        card.Children.Add(new TextBlock
                        {
                            Text = $"{Sign(c.Kind)} {c.Area}: {Shorten(c.Title, 90)}",
                            Margin = new Thickness(34, 1, 0, 0), Opacity = 0.8, TextWrapping = TextWrapping.Wrap,
                        });
            }

            var open = new Button { Content = "Open details", Margin = new Thickness(18, 6, 0, 0), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) => openDetail(row);
            card.Children.Add(open);

            list.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = card,
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

    private static string Sign(ChangeKind k) => k switch { ChangeKind.Added => "+", ChangeKind.Removed => "−", _ => "~" };
    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
