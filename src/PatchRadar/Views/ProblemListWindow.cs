using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PatchRadar.Infra;

namespace PatchRadar.Views;

/// <summary>Every distinct problem found in the log. Pick one to review and report.</summary>
public sealed class ProblemListWindow : Window
{
    private sealed record Row(ProblemGroup Group)
    {
        public string Last => Group.Last.ToString("d MMM HH:mm");
        public string Problem => Group.Report.Title;
        public int Times => Group.Count;
        public string State => Group.Status switch
        {
            ProblemStatus.Reported => "Reported",
            ProblemStatus.Dismissed => "Dismissed",
            ProblemStatus.Offered => "Offered",
            _ => "New",
        };
    }

    public ProblemListWindow(IReadOnlyList<ProblemGroup> groups, Action<ProblemReport> review)
    {
        Title = "PatchRadar - errors found in the log";
        Width = 760; Height = 420; MinWidth = 520; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false, Margin = new Thickness(16, 0, 16, 0),
            ItemsSource = groups.Select(g => new Row(g)).ToList(),
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Last seen", Binding = new System.Windows.Data.Binding(nameof(Row.Last)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Problem", Binding = new System.Windows.Data.Binding(nameof(Row.Problem)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Times", Binding = new System.Windows.Data.Binding(nameof(Row.Times)), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new System.Windows.Data.Binding(nameof(Row.State)), Width = 90 });
        if (groups.Count > 0) grid.SelectedIndex = 0;

        var intro = new TextBlock
        {
            Text = $"{groups.Count} different errors with a stack trace were logged in the last {ProblemWatcher.LookBack.TotalDays:0} days. " +
                   "Select one and choose Review to see exactly what would be reported.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 10),
        };
        var open = new Button { Content = "Review and report…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        void Review() { if (grid.SelectedItem is Row r) review(r.Group.Report); }
        open.Click += (_, _) => Review();
        grid.MouseDoubleClick += (_, _) => Review();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        buttons.Children.Add(close); buttons.Children.Add(open);

        var root = new DockPanel();
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(intro); root.Children.Add(buttons); root.Children.Add(grid);
        Content = root;
    }
}
