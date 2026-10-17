using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.Views;

namespace PatchRadar.Tray;

public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ScanService _scan;
    private readonly ScanScheduler _scheduler;
    private readonly Func<AppSettings> _settings;
    private readonly ILog _log;
    private readonly Dictionary<string, DetailWindow> _details = new();

    private ScanResult _last = ScanResult.Empty;
    private (int Done, int Total)? _progress;
    private bool _scanning;

    public TrayController(ScanService scan, Func<AppSettings> settings, ILog log)
    {
        _scan = scan;
        _settings = settings;
        _log = log;
        _scheduler = new ScanScheduler(ct => RunScanAsync(force: false), () => settings().Scanning, log);

        _icon = new TaskbarIcon
        {
            ToolTipText = "PatchRadar",
            Icon = IconFactory.For(Severity.Grey),
        };
        ApplySettings();
        RebuildMenu();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Start()
    {
        _ = LoadCachedAsync();
        _scheduler.Reschedule();
    }

    /// <summary>Re-read menu click behaviour and timers after a settings change.</summary>
    public void ApplySettings()
    {
        _icon.MenuActivation = _settings().General.LeftClickOpensMenu
            ? PopupActivationMode.LeftOrRightClick
            : PopupActivationMode.RightClick;
        _scheduler.Reschedule();
    }

    private async Task LoadCachedAsync()
    {
        try
        {
            var rows = await _scan.ListCachedAsync(allTime: false, CancellationToken.None);
            _last = new ScanResult(rows, DateTime.MinValue, 0, 0);
            Dispatch(Refresh);
        }
        catch (Exception ex) { _log.Error("Initial list failed", ex); }
    }

    public async Task RunScanAsync(bool force)
    {
        if (_scan.IsScanning) return;
        _scanning = true;
        _progress = (0, 0);
        Dispatch(Refresh);
        try
        {
            var result = await Task.Run(() => _scan.ScanAsync(force, new DispatcherProgress(p => Dispatch(() => { _progress = p; Refresh(); })), CancellationToken.None));
            _last = result;
            _log.Info($"Scan complete: {result.Rows.Count} rows, {result.Fetched} fetched, {result.Failed} failed");
        }
        catch (Exception ex) { _log.Error("Scan failed", ex); }
        finally
        {
            _scanning = false;
            _progress = null;
            _scheduler.NotifyManualScan();
            Dispatch(Refresh);
        }
    }

    private sealed class DispatcherProgress(Action<(int, int)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value);
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    private void Refresh()
    {
        var take = _settings().Updates.MenuItemCount;
        var worst = _scanning ? Severity.Grey : _last.Worst(take);
        _icon.Icon = IconFactory.For(worst);
        _icon.ToolTipText = BuildTooltip(take);
        RebuildMenu();
    }

    private string BuildTooltip(int take)
    {
        var rows = _last.Rows.Take(take).ToList();
        var s = $"PatchRadar — {rows.Count} updates · {rows.Count(r => r.Severity == Severity.Red)} red · {rows.Count(r => r.Severity == Severity.Orange)} orange";
        if (_last.ScannedAt != DateTime.MinValue) s += $" · scanned {_last.ScannedAt:HH:mm}";
        return s.Length > 127 ? s[..127] : s; // NotifyIcon tooltip limit
    }

    private void RebuildMenu()
    {
        var settings = _settings();
        var colors = settings.Appearance.Colors;
        var menu = new ContextMenu();

        string header;
        if (_scanning) header = _progress is { Total: > 0 } p ? $"Scanning… ({p.Done}/{p.Total})" : "Scanning…";
        else if (_last.ScannedAt == DateTime.MinValue) header = "Not scanned yet" + NextText();
        else header = $"Last scan {_last.ScannedAt:HH:mm}" + NextText();
        menu.Items.Add(new MenuItem { Header = header, IsEnabled = false });
        menu.Items.Add(new Separator());

        var rows = _last.Rows.Take(settings.Updates.MenuItemCount).ToList();
        if (rows.Count == 0) menu.Items.Add(new MenuItem { Header = "No updates in the selected window", IsEnabled = false });
        foreach (var row in rows)
        {
            var brush = Brush(row.Severity, colors);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = "● ", Foreground = brush });
            panel.Children.Add(new TextBlock { Text = SummaryFormatter.MenuText(row), Foreground = brush });
            var item = new MenuItem { Header = panel };
            if (!string.IsNullOrWhiteSpace(row.Result.Summary)) item.ToolTip = row.Result.Summary;
            var captured = row;
            item.Click += (_, _) => ShowDetail(captured);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        AddItem(menu, "Scan now", () => _ = RunScanAsync(force: true), enabled: !_scanning);
        menu.Items.Add(new Separator());
        AddItem(menu, "Open log folder", OpenLogFolder);
        AddItem(menu, "About", ShowAbout);
        AddItem(menu, "Exit", () => Application.Current.Shutdown());
        _icon.ContextMenu = menu;
    }

    private string NextText() =>
        _scheduler.NextScan is { } n ? $" · next {n:HH:mm}" : "";

    private static void AddItem(ContextMenu m, string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        m.Items.Add(item);
    }

    private static Brush Brush(Severity s, ColorSettings c)
    {
        var hex = s switch { Severity.Red => c.Red, Severity.Orange => c.Orange, Severity.Green => c.Green, _ => c.Grey };
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return Brushes.Gray; }
    }

    private void ShowDetail(UpdateRow row)
    {
        if (_details.TryGetValue(row.Update.Kb, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }
        var win = new DetailWindow(row, _settings().Appearance.Colors, RefreshRowAsync, _log);
        _details[row.Update.Kb] = win;
        win.Closed += (_, _) => _details.Remove(row.Update.Kb);
        win.Show();
        win.Activate();
    }

    private async Task<UpdateRow?> RefreshRowAsync(UpdateRow row)
    {
        try
        {
            var updated = await Task.Run(() => _scan.RefreshOneAsync(row.Update, CancellationToken.None));
            // replace in the cached result so the menu reflects it
            var rows = _last.Rows.Select(r => r.Update.Kb == updated.Update.Kb ? updated : r).ToList();
            _last = _last with { Rows = rows };
            Refresh();
            return updated;
        }
        catch (Exception ex) { _log.Error($"Refresh {row.Update.Kb} failed", ex); return null; }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(FileLog.DefaultDirectory);
            Process.Start(new ProcessStartInfo(FileLog.DefaultDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Error("Open log folder failed", ex); }
    }

    private static void ShowAbout()
    {
        var v = typeof(TrayController).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev";
        MessageBox.Show($"PatchRadar {v.Split('+')[0]}\n\nFlags risky Windows updates from Microsoft KB articles.\nhttps://github.com/danrhodes/PatchRadar\nMIT licence.",
            "About PatchRadar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _icon.Dispose();
    }
}
