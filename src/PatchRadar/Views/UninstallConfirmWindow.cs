using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PatchRadar.Core;
using PatchRadar.Scan;
using PatchRadar.Uninstall;

namespace PatchRadar.Views;

/// <summary>The rule for enabling the Uninstall button. Pure, so it is tested.</summary>
public static class UninstallConfirmation
{
    /// <summary>The user must tick the box and type the KB number (with or without "KB").</summary>
    public static bool IsConfirmed(string typed, string kb, bool acknowledged)
    {
        if (!acknowledged) return false;
        var t = (typed ?? "").Trim().ToUpperInvariant();
        if (t.Length > 0 && char.IsDigit(t[0])) t = "KB" + t;
        return t == kb.ToUpperInvariant();
    }

    /// <summary>Every warning shown before the user may continue. Returned as data so it can be tested and reused.</summary>
    public static IReadOnlyList<(bool Severe, string Text)> Warnings(UpdateRow row, UninstallPlan plan, string user)
    {
        var w = new List<(bool, string)>();
        if (row.FixAvailableNotInstalled)
            w.Add((false, $"A fix is available: {string.Join(", ", row.FixesNotInstalled.Select(f => f.Kb).Distinct())} is not installed on this PC. Installing the fix is usually safer than removing this update."));
        if (plan.IsSecurityUpdate)
            w.Add((true, "This is a SECURITY update. Removing it leaves this PC exposed to the vulnerabilities it fixes. Only continue to resolve a confirmed fault, and reinstall as soon as a fix is available."));
        w.Add((row.Severity is Severity.Green or Severity.Grey,
            row.Severity is Severity.Red or Severity.Orange
                ? $"PatchRadar flagged this update ({row.Severity}): {row.Result.Summary ?? "see its known issues"}."
                : "PatchRadar has NOT flagged a problem with this update. Make sure it really is the cause before removing it."));
        if (plan.Method == UninstallMethod.DismRemovePackage)
            w.Add((false, "Only the newest cumulative update can be removed. The servicing stack stays, and older cumulative updates cannot be removed separately."));
        w.Add((false, "A restart may be required. Save your work first."));
        w.Add((false, "Windows Update may install it again automatically. Pause updates afterwards if it must stay removed."));
        w.Add((false, "You will be asked to approve a Windows permission prompt (UAC)."));
        w.Add((false, $"This action is recorded: your user name ({user}), this computer, the date and time, and the reason you enter below."));
        return w;
    }
}

/// <summary>
/// The warning every removal goes through. Cancel is the default button; Uninstall stays disabled until the
/// user ticks the acknowledgement and types the KB number.
/// </summary>
public sealed class UninstallConfirmWindow : Window
{
    private readonly TextBox _typed = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _reason = new() { Margin = new Thickness(0, 4, 0, 10), MaxLength = 300 };
    private readonly CheckBox _ack = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _go;
    private readonly string _kb;

    public string Reason => _reason.Text.Trim();

    public UninstallConfirmWindow(UpdateRow row, UninstallPlan plan, string user)
    {
        _kb = row.Update.Kb;
        Title = $"Uninstall {_kb}?";
        Width = 600; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 40); ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeMode = Application.Current?.ThemeMode ?? System.Windows.ThemeMode.None;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = $"Uninstall {_kb}?", FontSize = 20, FontWeight = FontWeights.Bold });
        stack.Children.Add(new TextBlock { Text = row.Update.Title, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 12) });

        foreach (var (severe, text) in UninstallConfirmation.Warnings(row, plan, user))
        {
            var accent = severe ? new SolidColorBrush(Color.FromRgb(214, 58, 58)) : new SolidColorBrush(Color.FromRgb(224, 138, 30));
            stack.Children.Add(new Border
            {
                BorderBrush = accent, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6),
                Background = new SolidColorBrush(Color.FromArgb(severe ? (byte)40 : (byte)22, accent.Color.R, accent.Color.G, accent.Color.B)),
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            });
        }

        stack.Children.Add(new TextBlock { Text = "Command that will run (elevated):", Margin = new Thickness(0, 8, 0, 2), Opacity = 0.7, FontSize = 11 });
        stack.Children.Add(new TextBox { Text = plan.Command, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        stack.Children.Add(new TextBlock { Text = "Reason or ticket reference (recorded, optional)" });
        stack.Children.Add(_reason);

        _ack.Content = "I understand the risks and that this is recorded against my account.";
        stack.Children.Add(_ack);
        stack.Children.Add(new TextBlock { Text = $"Type {_kb} to confirm" });
        stack.Children.Add(_typed);

        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, Padding = new Thickness(20, 5, 20, 5), Margin = new Thickness(0, 0, 8, 0) };
        _go = new Button
        {
            Content = "Uninstall", Padding = new Thickness(20, 5, 20, 5), IsEnabled = false,
            Background = new SolidColorBrush(Color.FromRgb(214, 58, 58)), Foreground = Brushes.White,
        };
        _go.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 16) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_go);

        void Recheck() => _go.IsEnabled = UninstallConfirmation.IsConfirmed(_typed.Text, _kb, _ack.IsChecked == true);
        _typed.TextChanged += (_, _) => Recheck();
        _ack.Click += (_, _) => Recheck();

        // The buttons stay visible however tall the warnings are; only the warnings scroll.
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack });
        Content = root;
        Loaded += (_, _) => _typed.Focus();
    }
}
