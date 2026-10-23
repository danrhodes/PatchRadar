using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PatchRadar.Infra;

namespace PatchRadar.Views;

/// <summary>The pending-update briefing as Markdown, ready to copy into a ticket or save.</summary>
public sealed class BriefingWindow : Window
{
    private readonly ILog _log;
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Margin = new Thickness(12, 12, 12, 0),
    };

    public BriefingWindow(string markdown, ILog log)
    {
        _log = log;
        Title = "PatchRadar - pending update briefing";
        Width = 760; Height = 640; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        _text.Text = markdown;

        var copy = Button("Copy", (_, _) =>
        {
            try { Clipboard.SetText(_text.Text); }
            catch (Exception ex) { _log.Warn("Clipboard failed: " + ex.Message); }
        });
        var save = Button("Save as Markdown…", (_, _) => Save());
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        bar.Children.Add(copy);
        bar.Children.Add(save);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_text);
        Content = root;
    }

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
        try { File.WriteAllText(dlg.FileName, _text.Text, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Briefing save failed", ex);
            MessageBox.Show("Could not write the file: " + ex.Message, "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
