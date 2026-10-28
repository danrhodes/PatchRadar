using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PatchRadar.AppUpdate;
using PatchRadar.Infra;

namespace PatchRadar.Views;

/// <summary>
/// A real window rather than a MessageBox: a MessageBox raised from a tray menu click can be dismissed
/// together with the closing popup. This one stays until closed (OK or Esc).
/// </summary>
public sealed class AboutWindow : Window
{
    public AboutWindow(ILog log)
    {
        Title = "About PatchRadar";
        Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        ShowInTaskbar = true;
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) Close(); };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "PatchRadar", FontSize = 24, FontWeight = FontWeights.Bold });
        stack.Children.Add(new TextBlock { Text = $"Version {AppVersion.Running()} ({AppVersion.Flavour()} build)", Margin = new Thickness(0, 2, 0, 12), Opacity = 0.7 });
        stack.Children.Add(new TextBlock
        {
            Text = "Flags risky Windows updates by reading Microsoft's KB articles: known issues, workarounds, fixes and behaviour changes that can break things.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        stack.Children.Add(new TextBlock { Text = "No telemetry. Contacts only Microsoft (KB and release-health pages) and GitHub (app updates). MIT licence.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 12) });

        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        links.Children.Add(Link("GitHub", UpdateRepo.HtmlBase, log));
        links.Children.Add(Link("Releases", UpdateRepo.HtmlBase + "/releases", log));
        stack.Children.Add(links);

        var ok = new Button { Content = "OK", Padding = new Thickness(24, 4, 24, 4), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += (_, _) => Close();
        stack.Children.Add(ok);
        Content = stack;
    }

    private static Button Link(string text, string url, ILog log)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) =>
        {
            if (!UpdateRepo.IsAllowedReleasePage(url)) { log.Warn("Refused to open link: " + url); return; }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { log.Error("Could not open link", ex); }
        };
        return b;
    }
}
