using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using PatchRadar.AppUpdate;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Tray;
using PatchRadar.Updates;
using PatchRadar.Views;

namespace PatchRadar;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayController? _tray;
    private FileLog? _log;
    private ClientHolder? _supportClient;
    private ClientHolder? _githubClient;
    private ClientHolder? _downloadClient;
    private UpdateCoordinator? _updates;
    private System.Windows.Threading.DispatcherTimer? _healthTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var diagIdx = Array.IndexOf(args, "--diag");
        if (diagIdx >= 0)
        {
            var outPath = diagIdx + 1 < args.Length ? args[diagIdx + 1] : Path.Combine(Path.GetTempPath(), "patchradar-diag.txt");
            await RunDiagAsync(outPath, args.Skip(diagIdx + 2).ToArray());
            Shutdown();
            return;
        }

        // After a self-update the old process may still be exiting.
        var postUpdate = args.Contains("--post-update");
        var wait = postUpdate ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        _single = new SingleInstance(wait: wait);
        if (!_single.IsFirst) { Shutdown(); return; }

        _log = new FileLog(FileLog.DefaultDirectory);
        DispatcherUnhandledException += (_, ex) => { _log.Error("Unhandled UI exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => _log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { _log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        var store = new SettingsStore(Paths.SettingsFile, _log);
        var host = new SettingsHost(store, store.Load());
        ApplyRuntimeSettings(host.Current);
        _log.Info($"PatchRadar {AppVersion.Running()} ({AppVersion.Flavour()}) starting");

        ThemeManager.Initialise(host.Current.Appearance.Theme);
        var autostart = new Autostart(new RegistryRunKey(), Environment.ProcessPath ?? "", _log);
        autostart.Apply(host.Current.General.RunAtLogin);

        // Self-update bookkeeping first: a failed update must roll back before anything else runs.
        var pending = new PendingStore(Paths.UpdatesDir, _log);
        var installer = new UpdateInstaller(new InstallFileOps(), pending, Environment.ProcessPath ?? "", _log);
        var health = new UpdateHealth(pending, installer, _log);
        var startup = health.OnStartup(AppVersion.Running(), failed => SaveSkipped(host, failed));
        if (startup.Action == StartupAction.RolledBack)
        {
            _log.Warn($"Rolled back to v{startup.Version}; restarting");
            try { Process.Start(new ProcessStartInfo(installer.InstalledPath, "--post-update") { UseShellExecute = false }); }
            catch (Exception ex) { _log.Error("Could not restart after rollback", ex); }
            Shutdown();
            return;
        }

        // Proxy, timeout and User-Agent changes apply to the next request: clients are rebuilt on demand.
        _supportClient = new ClientHolder(n => SupportSiteSource.CreateClient(n), () => host.Current.Network);
        _githubClient = new ClientHolder(n => NewPlainClient(n, TimeSpan.FromSeconds(Math.Max(1, n.TimeoutSeconds))), () => host.Current.Network);
        _downloadClient = new ClientHolder(n => AllowlistedDownloader.CreateClient(n.Proxy, TimeSpan.FromMinutes(5)), () => host.Current.Network);
        host.Changed += s =>
        {
            _supportClient.Reset(); _githubClient.Reset(); _downloadClient.Reset();
            ApplyRuntimeSettings(s);
            Dispatcher.BeginInvoke(() =>
            {
                ThemeManager.Apply(s.Appearance.Theme);
                autostart.Apply(s.General.RunAtLogin);
                _updates?.Reschedule();
            });
        };

        var scan = BuildScanService(host, _supportClient, _log);
        var toasts = new ToastTracker(new StateStore(Paths.StateFile, _log));

        var updater = new AppUpdater(() => host.Current.AppUpdate,
            new GitHubReleaseSource(_githubClient.Get, AppVersion.Running().ToString(), Paths.UpdatesDir, _log),
            new AllowlistedDownloader(_downloadClient.Get, AppVersion.Running().ToString()),
            new WinTrustVerifier(), installer, new ExitingLauncher(), Paths.UpdatesDir,
            AppVersion.Running(), AppVersion.Flavour(), failed => SaveSkipped(host, failed), log: _log);
        TrayController? tray = null;
        _updates = new UpdateCoordinator(updater, () => host.Current.AppUpdate,
            (title, msg, url) => tray?.ShowToast(title, msg, url is null ? null : () => OpenReleasePage(url)), _log);

        tray = _tray = new TrayController(scan, host, toasts, _updates, _log);
        _tray.Start();
        _updates.Start();

        // Tell the user what the last start did.
        if (startup.Action == StartupAction.JustUpdated) _tray.ShowToast("PatchRadar updated", $"Updated to v{startup.Version}.");
        else if (startup.Action == StartupAction.RollbackFailed) _tray.ShowToast("PatchRadar update problem", "An update failed and could not be rolled back automatically. See the log folder.");
        if (pending.TakeNotice() is { } notice) _tray.ShowToast("PatchRadar", notice);

        // 60 s of life = the new build is good: remove .old and pending.json.
        _healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateHealth.HealthyAfter };
        _healthTimer.Tick += (_, _) => { _healthTimer.Stop(); health.MarkHealthy(); };
        _healthTimer.Start();
    }

    private static void SaveSkipped(SettingsHost host, string version)
    {
        var s = SettingsHost.Clone(host.Current);
        s.AppUpdate.SkippedVersion = version;
        host.Apply(s);
    }

    private void ApplyRuntimeSettings(AppSettings s)
    {
        if (_log is not null)
            _log.MinLevel = Enum.TryParse<Infra.LogLevel>(s.Advanced.LogLevel, true, out var lvl) ? lvl : Infra.LogLevel.Information;
    }

    private void OpenReleasePage(string url)
    {
        if (!UpdateRepo.IsAllowedReleasePage(url)) { _log?.Warn("Refused to open release page: " + url); return; }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log?.Error("Could not open the release page", ex); }
    }

    private static HttpClient NewPlainClient(NetworkSettings n, TimeSpan timeout)
    {
        var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(n.Proxy)) { h.Proxy = new WebProxy(n.Proxy); h.UseProxy = true; }
        return new HttpClient(h) { Timeout = timeout };
    }

    /// <summary>Starts the new exe, then ends this process so the single-instance mutex is free for it.</summary>
    private sealed class ExitingLauncher : IProcessLauncher
    {
        public void StartAndExit(string exePath, string arguments)
        {
            Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = false });
            Current.Dispatcher.BeginInvoke(() => Current.Shutdown());
        }
    }

    private static ScanService BuildScanService(SettingsHost host, ClientHolder support, ILog log)
    {
        var s = host.Current;
        var gate = new RequestGate(s.Network.MaxConcurrent, TimeSpan.FromMilliseconds(500));
        var source = new SupportSiteSource(support.Get, gate, log);
        var releaseHealth = new ReleaseHealthSource(support.Get, gate, OsInfo.Current,
            () => host.Current.Advanced.EnableReleaseHealthFallback, Paths.ReleaseHealthCacheDir, log);
        var cache = new KbCache(Paths.KbCacheDir, log);
        var fetcher = new KbFetcher(new IKbSource[] { source, releaseHealth }, cache, log, enrichers: new IKbEnricher[] { releaseHealth });
        var history = new UpdateCollector(new IUpdateHistoryProvider[] { new WuaHistoryProvider(), new QfeProvider() }, log);
        return new ScanService(history, fetcher, () => host.Current, log);
    }

    /// <summary>
    /// Headless check used for acceptance tests: times the update list and live-fetches KBs.
    /// Usage: PatchRadar.exe --diag out.txt [KB5127648 ...]
    /// </summary>
    private static async Task RunDiagAsync(string outPath, string[] kbs)
    {
        var sb = new StringBuilder();
        void L(string s) { sb.AppendLine(s); }
        try
        {
            L($"PatchRadar diag {DateTime.Now:u}  user={Environment.UserName}  elevated={IsElevated()}  os={Environment.OSVersion.Version}");
            var settings = new AppSettings();
            var log = NullLog.Instance;
            var history = new UpdateCollector(new IUpdateHistoryProvider[] { new WuaHistoryProvider(), new QfeProvider() }, log);

            foreach (var (name, provider) in new (string, IUpdateHistoryProvider)[] { ("WUA", new WuaHistoryProvider()), ("QFE", new QfeProvider()) })
            {
                var sw0 = Stopwatch.StartNew();
                try { var r = await provider.GetAsync(default); L($"{name}: {r.Count} entries in {sw0.ElapsedMilliseconds} ms"); }
                catch (Exception ex) { L($"{name}: FAILED {ex.GetType().Name}: {ex.Message}"); }
            }

            var sw = Stopwatch.StartNew();
            var all = await history.GetAsync(default);
            var filtered = UpdateMerger.Filter(all, settings.Updates, DateTimeOffset.UtcNow);
            L($"Collector: {all.Count} merged, {filtered.Count} after default filters, {sw.ElapsedMilliseconds} ms");
            foreach (var u in filtered.Take(15)) L($"  {u.Kb}  {u.InstalledOn.LocalDateTime:yyyy-MM-dd}  {u.Category,-14} {u.Source}  {u.Title}");

            var http = SupportSiteSource.CreateClient(settings.Network);
            var gate = new RequestGate(2, TimeSpan.FromMilliseconds(500));
            var src = new SupportSiteSource(http, gate);
            var os = OsInfo.Current();
            L($"OS: build {os.Build}, server={os.IsServer}, version={os.DisplayVersion}; release health pages: {string.Join(", ", ReleaseHealthPages.UrlsFor(os))}");
            var rh = new ReleaseHealthSource(() => http, gate, () => os);
            var engine = SeverityEngine.Default();
            foreach (var kb in kbs.Length > 0 ? kbs : new[] { "KB5127648" })
            {
                var sw2 = Stopwatch.StartNew();
                var d = await src.FetchAsync(kb, default);
                var r = engine.Evaluate(d);
                L($"Fetch {kb}: status={d.Status} err={d.LastError} improvements={d.Improvements.Count} issues={d.KnownIssues.Count} severity={r.Severity} keywords=[{string.Join(", ", r.MatchedKeywords)}] url={d.CanonicalUrl} {sw2.ElapsedMilliseconds} ms");
                var sw3 = Stopwatch.StartNew();
                var b = await rh.FetchAsync(kb, default);
                L($"  release health {kb}: status={b.Status} issues={b.KnownIssues.Count} err={b.LastError} {sw3.ElapsedMilliseconds} ms");
                foreach (var i in b.KnownIssues) L($"    - [{i.Status}] {i.Title}");
            }
        }
        catch (Exception ex) { L("DIAG FAILED: " + ex); }
        File.WriteAllText(outPath, sb.ToString());
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _healthTimer?.Stop();
        _updates?.Dispose();
        _tray?.Dispose();
        _supportClient?.Dispose();
        _githubClient?.Dispose();
        _downloadClient?.Dispose();
        _single?.Dispose();
        _log?.Info("PatchRadar exiting");
        base.OnExit(e);
    }
}
