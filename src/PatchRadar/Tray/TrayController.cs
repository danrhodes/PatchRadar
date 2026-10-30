using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using PatchRadar.AppUpdate;
using PatchRadar.Core;
using PatchRadar.Export;
using PatchRadar.Infra;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Uninstall;
using PatchRadar.Views;

namespace PatchRadar.Tray;

public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ScanService _scan;
    private readonly ScanScheduler _scheduler;
    private readonly SettingsHost _host;
    private readonly ToastTracker _toasts;
    private readonly DigestStore _digest;
    private readonly UninstallService? _uninstall;
    private DigestWindow? _digestWindow;
    private readonly UpdateCoordinator? _updates;
    private readonly ILog _log;
    private readonly Dictionary<string, DetailWindow> _details = new();
    private AllUpdatesWindow? _allWindow;
    private SettingsWindow? _settingsWindow;
    private Action? _toastClick;

    private ScanResult _last = ScanResult.Empty;
    private (int Done, int Total)? _progress;
    private bool _scanning;
    private bool _restart;
    private readonly PendingSeenStore? _seen;
    private readonly RebootReminder? _reboot;
    private readonly bool _isServer = Kb.OsInfo.Current().IsServer;
    private System.Windows.Threading.DispatcherTimer? _rebootTimer;

    private int PendingCount => _last.Pending.Count(r => !r.Ignored);

    /// <summary>Pending updates the user has not looked at yet: what the icon badge counts.</summary>
    private int UnseenCount => _last.Pending.Count(r => !r.Ignored && _seen?.IsSeen(r.Update.Kb) != true);

    private Severity IconSeverity() => _scanning ? Severity.Grey : _last.Worst(Settings.Updates.MenuItemCount);

    private void UpdateIcon()
    {
        // An icon problem must never take the app down: keep the current icon and carry on.
        try { _icon.Icon = IconFactory.For(IconSeverity(), Settings.General.ShowIconBadge ? UnseenCount : 0); }
        catch (Exception ex) { _log.Warn("Could not update the tray icon: " + ex.Message); }
    }

    /// <summary>Opening the menu is the user looking: record it and drop the badge.</summary>
    private void OnMenuOpened()
    {
        if (_seen is null) return;
        if (_seen.MarkSeen(_last.Pending.Where(r => !r.Ignored).Select(r => r.Update.Kb))) UpdateIcon();
    }

    private AppSettings Settings => _host.Current;

    public TrayController(ScanService scan, SettingsHost host, ToastTracker toasts, DigestStore digest, UpdateCoordinator? updates, ILog log, UninstallService? uninstall = null, PendingSeenStore? seen = null, RebootReminder? reboot = null)
    {
        _seen = seen;
        _reboot = reboot;
        _uninstall = uninstall;
        _digest = digest;
        _scan = scan;
        _host = host;
        _toasts = toasts;
        _updates = updates;
        _log = log;
        _scheduler = new ScanScheduler(ct => RunScanAsync(force: false), () => host.Current.Scanning, log);

        _icon = new TaskbarIcon
        {
            ToolTipText = "PatchRadar",
            Icon = IconFactory.For(Severity.Grey),
        };
        _icon.TrayBalloonTipClicked += (_, _) => { var c = _toastClick; _toastClick = null; c?.Invoke(); };
        if (_updates is not null) _updates.Changed += () => Dispatch(Refresh);
        _host.Changed += OnSettingsChanged;
        ApplySettings();
        RebuildMenu();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Start()
    {
        _ = LoadCachedAsync();
        _scheduler.Reschedule();
        // Check shortly after start-up, then hourly; the reminder itself decides whether one is due.
        _rebootTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _rebootTimer.Tick += (_, _) => { _rebootTimer.Interval = TimeSpan.FromHours(1); CheckRebootReminder(); };
        _rebootTimer.Start();
    }

    private void CheckRebootReminder()
    {
        try
        {
            var up = Uptime.Current();
            if (_reboot is null || !_reboot.TryTake(Settings.RebootReminder, up, _isServer)) return;
            var extra = _restart ? " Windows is also waiting for a restart to finish installing updates." : "";
            ShowToast("Time to restart", $"This PC has been running for {Uptime.Describe(up)}.{extra}");
        }
        catch (Exception ex) { _log.Error("Restart reminder failed", ex); }
    }

    /// <summary>Re-read menu click behaviour and timers after a settings change.</summary>
    public void ApplySettings()
    {
        _icon.MenuActivation = Settings.General.LeftClickOpensMenu
            ? PopupActivationMode.LeftOrRightClick
            : PopupActivationMode.RightClick;
        _scheduler.Reschedule();
    }

    private void OnSettingsChanged(AppSettings _) => Dispatch(() =>
    {
        ApplySettings();
        Refresh();
    });

    private async Task LoadCachedAsync()
    {
        try
        {
            var rows = await _scan.ListCachedAsync(allTime: false, CancellationToken.None);
            var pending = await _scan.ListPendingCachedAsync(CancellationToken.None);
            _last = new ScanResult(rows, DateTime.MinValue, 0, 0) { Pending = pending };
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
            Dispatch(() => { NotifyNewRed(result); NotifyPendingRed(result); });
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

    // ---- toasts ---------------------------------------------------------------------------

    private void NotifyNewRed(ScanResult result)
    {
        try
        {
            // Always record, even when toasts are off, so switching them on later does not replay old reds.
            var fresh = _toasts.TakeNew(result.Rows.Take(Settings.Updates.MenuItemCount).Where(r => !r.Ignored), Settings.Scanning.RecentWindowDays);
            if (!Settings.General.ToastOnNewRed || fresh.Count == 0) return;

            if (fresh.Count == 1)
            {
                var r = fresh[0];
                ShowToast($"{r.Update.Kb} looks risky", r.Result.Summary ?? r.Update.Title, () => ShowDetail(r));
            }
            else
            {
                var kbs = string.Join(", ", fresh.Take(4).Select(r => r.Update.Kb)) + (fresh.Count > 4 ? $" and {fresh.Count - 4} more" : "");
                ShowToast($"{fresh.Count} new risky updates", kbs, ShowAllUpdates);
            }
        }
        catch (Exception ex) { _log.Error("Toast failed", ex); }
    }

    /// <summary>Warn before install: a red update is waiting. Recorded like installed ones, so it is not repeated once installed.</summary>
    private void NotifyPendingRed(ScanResult result)
    {
        try
        {
            var fresh = _toasts.TakeNew(result.Pending.Where(r => !r.Ignored), Settings.Scanning.RecentWindowDays);
            if (!Settings.General.ToastOnNewRed || fresh.Count == 0) return;

            if (fresh.Count == 1)
            {
                var r = fresh[0];
                ShowToast($"Pending update {r.Update.Label} looks risky", r.Result.Summary ?? r.Update.Title, () => ShowDetail(r));
            }
            else
            {
                var kbs = string.Join(", ", fresh.Take(4).Select(r => r.Update.Kb)) + (fresh.Count > 4 ? $" and {fresh.Count - 4} more" : "");
                ShowToast($"{fresh.Count} pending updates look risky", kbs, ShowAllUpdates);
            }
        }
        catch (Exception ex) { _log.Error("Toast failed", ex); }
    }

    public void ShowToast(string title, string message, Action? onClick = null)
    {
        Dispatch(() =>
        {
            try
            {
                _toastClick = onClick;
                _icon.ShowNotification(title: Trim(title, 63), message: Trim(message, 250), icon: NotificationIcon.Info);
            }
            catch (Exception ex) { _log.Warn("Could not show notification: " + ex.Message); }
        });
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- icon, tooltip, menu --------------------------------------------------------------

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
        var take = Settings.Updates.MenuItemCount;
        _restart = _scan.IsRestartPending() || _last.RestartPending;
        UpdateIcon();
        _icon.ToolTipText = BuildTooltip(take);
        RebuildMenu();
    }

    private string BuildTooltip(int take)
    {
        var rows = _last.Rows.Take(take).ToList();
        var s = $"PatchRadar — {rows.Count} updates · {rows.Count(r => r.Severity == Severity.Red)} red · {rows.Count(r => r.Severity == Severity.Orange)} orange";
        if (PendingCount > 0) s += $" · {PendingCount} pending";
        if (_restart) s += " · restart needed";
        s += $" · up {Uptime.Describe(Uptime.Current())}";
        if (_last.ScannedAt != DateTime.MinValue) s += $" · scanned {_last.ScannedAt:HH:mm}";
        return s.Length > 127 ? s[..127] : s; // NotifyIcon tooltip limit
    }

    private void RebuildMenu()
    {
        var settings = Settings;
        var colors = settings.Appearance.Colors;
        var menu = new ContextMenu();
        if (settings.Appearance.FontSize > 0) menu.FontSize = settings.Appearance.FontSize;

        string header;
        if (_scanning) header = _progress is { Total: > 0 } p ? $"Scanning… ({p.Done}/{p.Total})" : "Scanning…";
        else if (_last.ScannedAt == DateTime.MinValue) header = "Not scanned yet" + NextText();
        else header = $"Last scan {_last.ScannedAt:HH:mm}" + NextText();
        menu.Items.Add(new MenuItem { Header = header, IsEnabled = false });
        menu.Items.Add(new Separator());

        MenuItem UpdateItem(UpdateRow row)
        {
            var brush = SeverityBrushes.For(row.Severity, colors);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = "● ", Foreground = brush });
            panel.Children.Add(new TextBlock { Text = SummaryFormatter.MenuText(row), Foreground = brush });
            if (row.FixAvailableNotInstalled)
                panel.Children.Add(new TextBlock { Text = " · ⬆ fix available", Foreground = SeverityBrushes.For(Severity.Orange, colors) });
            if (row.Review == ReviewStatus.Reviewed)
            {
                panel.Children.Insert(0, new TextBlock { Text = "✓ " });
                if (settings.Severity.DimReviewed) panel.Opacity = 0.55;
            }
            else if (row.Review == ReviewStatus.ChangedSinceReviewed)
                panel.Children.Add(new TextBlock { Text = " · changed since reviewed", Foreground = SeverityBrushes.For(Severity.Orange, colors) });
            if (row.Ignored)
            {
                panel.Children.Add(new TextBlock { Text = " · ignored" });
                panel.Opacity = 0.55;
            }
            var item = new MenuItem { Header = panel };
            if (!string.IsNullOrWhiteSpace(row.Result.Summary)) item.ToolTip = row.Result.Summary;
            var captured = row;
            item.Click += (_, _) => Later(() => ShowDetail(captured));
            return item;
        }

        var up = Uptime.Current();
        var longUp = RebootReminder.Applies(settings.RebootReminder, _isServer) && Uptime.IsLong(up, settings.RebootReminder.AfterDaysFor(_isServer));
        menu.Items.Add(new MenuItem
        {
            Header = $"Up {Uptime.Describe(up)}" + (longUp ? " · consider restarting" : ""),
            IsEnabled = false,
            Foreground = longUp ? SeverityBrushes.For(Severity.Orange, colors) : System.Windows.SystemColors.GrayTextBrush,
        });
        if (_restart)
            menu.Items.Add(new MenuItem { Header = "⟳ Restart needed to finish installing updates", IsEnabled = false, Foreground = SeverityBrushes.For(Severity.Orange, colors) });

        if (_last.Pending.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = $"Pending – not installed yet ({_last.Pending.Count})", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var row in _last.Pending.Take(settings.Updates.MenuItemCount)) menu.Items.Add(UpdateItem(row));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Installed", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        }

        var rows = _last.Rows.Take(settings.Updates.MenuItemCount).ToList();
        if (rows.Count == 0) menu.Items.Add(new MenuItem { Header = "No updates in the selected window", IsEnabled = false });
        foreach (var row in rows) menu.Items.Add(UpdateItem(row));

        menu.Items.Add(new Separator());
        AddItem(menu, "Scan now", () => _ = RunScanAsync(force: true), enabled: !_scanning);
        AddItem(menu, "View all updates…", ShowAllUpdates);
        AddItem(menu, PendingCount > 0 ? $"Pending briefing ({PendingCount})…" : "Pending briefing…", ShowBriefing, enabled: _last.Pending.Count > 0);
        var changes = DigestItems();
        var changedCount = changes.Select(c => c.Row.Update.Kb).Distinct().Count();
        AddItem(menu, changedCount > 0 ? $"What's changed ({changedCount})…" : "What's changed…", ShowDigest);

        var export = new MenuItem { Header = "Export…" };
        void AddExport(string text, Action click)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => Later(click);
            export.Items.Add(mi);
        }
        AddExport("CSV…", () => ExportActions.SaveAs(ExportFormat.Csv, _last.Rows, null, _log));
        AddExport("JSON…", () => ExportActions.SaveAs(ExportFormat.Json, _last.Rows, null, _log));
        AddExport("Markdown…", () => ExportActions.SaveAs(ExportFormat.Markdown, _last.Rows, null, _log));
        AddExport("Copy as Markdown", () => ExportActions.CopyMarkdown(_last.Rows, _log));
        menu.Items.Add(export);

        AddItem(menu, "Settings…", ShowSettings);
        if (_uninstall is { IsAvailable: true })
            AddItem(menu, "Uninstall history…", () => new UninstallHistoryWindow(_uninstall.Audit, _log).Show());
        menu.Items.Add(new Separator());
        AddItem(menu, "Open log folder", OpenLogFolder);
        AddUpdateItems(menu);
        AddItem(menu, "About", ShowAbout);
        AddItem(menu, "Exit", () => Application.Current.Shutdown());
        menu.Opened += (_, _) => OnMenuOpened();
        _icon.ContextMenu = menu;
    }

    private void AddUpdateItems(ContextMenu menu)
    {
        if (_updates is null) return;
        var last = _updates.Last;
        switch (last?.Kind)
        {
            case UpdateOutcomeKind.Staged:
                AddItem(menu, $"Restart to update to v{last.Version}", () => _updates.ApplyStaged());
                return;
            case UpdateOutcomeKind.Available when last.ReleaseUrl is not null && UpdateRepo.IsAllowedReleasePage(last.ReleaseUrl):
                AddItem(menu, $"Update available: v{last.Version}…", () => OpenReleasePage(last.ReleaseUrl));
                AddItem(menu, $"Skip v{last.Version}", () => _updates.Skip(last.Version!));
                break;
        }
        AddItem(menu, "Check for app update", () => _ = _updates.CheckNowAsync());
    }

    private void OpenReleasePage(string url)
    {
        if (!UpdateRepo.IsAllowedReleasePage(url)) { _log.Warn("Refused to open release page: " + url); return; }
        if (UrlGuard.Launch(url, _log)) return;
        _log.Error("Could not open the release page", new InvalidOperationException(url));
        UrlGuard.ShowLinkFallback(url);
    }

    private string NextText() =>
        _scheduler.NextScan is { } n ? $" · next {n:HH:mm}" : "";

    /// <summary>
    /// Menu clicks run after the popup has finished closing. Opening a window or dialog inside the click
    /// itself can lose focus and be dismissed together with the menu.
    /// </summary>
    private static void Later(Action a) =>
        Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, a);

    private static void AddItem(ContextMenu m, string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => Later(click);
        m.Items.Add(item);
    }

    // ---- windows --------------------------------------------------------------------------

    private void ShowDetail(UpdateRow row)
    {
        if (_details.TryGetValue(row.Update.Kb, out var existing) && existing.IsLoaded)
        {
            existing.Update(row);
            existing.Activate();
            return;
        }
        var win = new DetailWindow(row, Settings.Appearance.Colors, RefreshRowAsync, SetReviewed, _scan.IsInstalled, _log, _uninstall, () => _ = RunScanAsync(force: true), SetIgnored, ShowDetailByKb);
        _details[row.Update.Kb] = win;
        win.Closed += (_, _) => _details.Remove(row.Update.Kb);
        UiScale.Apply(win, Settings.Appearance.FontSize);
        win.Show();
        win.Activate();
        win.Topmost = true; win.Topmost = false;
    }

    /// <summary>Open the PatchRadar info window for a KB (for example the update a servicing stack update came with).</summary>
    private async void ShowDetailByKb(string kb)
    {
        try
        {
            var row = _last.Pending.Concat(_last.Rows).FirstOrDefault(r => r.Update.Kb.Equals(kb, StringComparison.OrdinalIgnoreCase));
            if (row is null && _scan.InstalledUpdates.FirstOrDefault(u => u.Kb.Equals(kb, StringComparison.OrdinalIgnoreCase)) is { } installed)
                row = await Task.Run(() => _scan.RefreshOneAsync(installed, CancellationToken.None));
            if (row is not null) { ShowDetail(row); return; }
            _log.Info($"{kb} is not in the update list; opening its Microsoft article instead");
            UrlGuard.OpenInBrowser(UrlGuard.KbUrl(kb), _log);
        }
        catch (Exception ex) { _log.Error("Could not open " + kb, ex); }
    }

    private void ShowAllUpdates()
    {
        if (_allWindow is { IsLoaded: true }) { _allWindow.Activate(); return; }
        _allWindow = new AllUpdatesWindow(_scan, _host, ShowDetail, _log);
        _allWindow.Closed += (_, _) => _allWindow = null;
        UiScale.Apply(_allWindow, Settings.Appearance.FontSize);
        _allWindow.Show();
        _allWindow.Activate();
    }

    private void ShowBriefing()
    {
        var md = Briefing.Render(_last.Pending, _restart, DateTime.Now, Environment.MachineName);
        var briefing = new BriefingWindow(_last.Pending, _restart, Environment.MachineName, Settings.Appearance.Colors, md, ShowDetail, _log);
        UiScale.Apply(briefing, Settings.Appearance.FontSize);
        briefing.Show();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_host, _log);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        UiScale.Apply(_settingsWindow, Settings.Appearance.FontSize);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ReplaceRow(UpdateRow updated)
    {
        UpdateRow Swap(UpdateRow r) => r.Update.Kb == updated.Update.Kb ? updated : r;
        _last = _last with { Rows = _last.Rows.Select(Swap).ToList(), Pending = _last.Pending.Select(Swap).ToList() };
    }

    private UpdateRow SetIgnored(UpdateRow row, bool ignored)
    {
        var updated = _scan.SetIgnored(row, ignored);
        ReplaceRow(updated);
        Refresh();
        return updated;
    }

    /// <summary>Detail window button: flip the flag and keep the menu in step.</summary>
    private UpdateRow SetReviewed(UpdateRow row, bool reviewed)
    {
        var updated = _scan.SetReviewed(row, reviewed);
        ReplaceRow(updated);
        Refresh();
        return updated;
    }

    private async Task<UpdateRow?> RefreshRowAsync(UpdateRow row)
    {
        try
        {
            var updated = await Task.Run(() => _scan.RefreshOneAsync(row.Update, CancellationToken.None));
            // replace in the cached result so the menu reflects it
            ReplaceRow(updated);
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

    private IReadOnlyList<DigestItem> DigestItems() =>
        DigestBuilder.Build(_last.Rows, _digest.LastSeenUtc, new SeverityEngine(Settings.ToSeverityOptions()));

    private void ShowDigest()
    {
        if (_digestWindow is { IsLoaded: true }) { _digestWindow.Activate(); return; }
        var since = _digest.LastSeenUtc;
        _digestWindow = new DigestWindow(DigestItems(), since, Settings.Appearance.Colors, ShowDetail, () => { _digest.MarkSeen(); Refresh(); });
        _digestWindow.Closed += (_, _) => _digestWindow = null;
        UiScale.Apply(_digestWindow, Settings.Appearance.FontSize);
        _digestWindow.Show();
        _digestWindow.Activate();
        _digestWindow.Topmost = true; _digestWindow.Topmost = false;
    }

    private AboutWindow? _about;

    private void ShowAbout()
    {
        if (_about is { IsLoaded: true }) { _about.Activate(); return; }
        _about = new AboutWindow(_log);
        _about.Closed += (_, _) => _about = null;
        UiScale.Apply(_about, Settings.Appearance.FontSize);
        _about.Show();
        _about.Activate();
    }

    public void Dispose()
    {
        _host.Changed -= OnSettingsChanged;
        _rebootTimer?.Stop();
        _scheduler.Dispose();
        _icon.Dispose();
    }
}
