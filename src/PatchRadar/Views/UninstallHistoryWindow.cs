using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PatchRadar.Infra;
using PatchRadar.Uninstall;

namespace PatchRadar.Views;

/// <summary>Every removal attempt on this machine: when, who, which update, how, and the outcome.</summary>
public sealed class UninstallHistoryWindow : Window
{
    private sealed record Row(string When, string User, string Machine, string Kb, string Stage, string Method, string Result, string Reason);

    public UninstallHistoryWindow(UninstallAudit audit, ILog log)
    {
        Title = "PatchRadar - uninstall history";
        Width = 1000; Height = 480; MinWidth = 700; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var rows = audit.ReadAll().OrderByDescending(e => e.TimestampUtc)
            .Select(e => new Row(e.TimestampLocal, e.User, e.Machine, e.Kb, e.Stage, e.Method, e.Message, e.Reason)).ToList();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = rows, Margin = new Thickness(12, 12, 12, 0),
            HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        void Col(string h, string p, double w) => grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p), Width = w });
        Col("When", nameof(Row.When), 170); Col("User", nameof(Row.User), 150); Col("Computer", nameof(Row.Machine), 110);
        Col("KB", nameof(Row.Kb), 90); Col("Stage", nameof(Row.Stage), 150); Col("Method", nameof(Row.Method), 130);
        Col("Result", nameof(Row.Result), 280); Col("Reason", nameof(Row.Reason), 200);

        var note = new TextBlock
        {
            Text = rows.Count == 0 ? "No updates have been uninstalled with PatchRadar on this machine." :
                "A local record for accountability. Each removal has a Started line, then its outcome. The file can be edited by its owner, so it is not tamper-proof.",
            Margin = new Thickness(12, 8, 12, 8), Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        };
        var open = new Button { Content = "Open log file", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = rows.Count > 0 };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{audit.FilePath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { log.Error("Could not open the audit file", ex); }
        };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(open);
        buttons.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(note);
        root.Children.Add(grid);
        Content = root;
    }
}
