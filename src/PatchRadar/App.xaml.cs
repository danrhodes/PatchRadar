using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using PatchRadar.AppUpdate;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Tray;
using PatchRadar.Uninstall;
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
            var rest = args.Skip(diagIdx + 2).ToArray();
            await RunDiagAsync(outPath, rest.Where(a => a != "--webview").ToArray(), rest.Contains("--webview"));
            Shutdown();
            return;
        }

        // Headless report for scheduled tasks and RMM tools: PatchRadar.exe --report out.(json|csv|md) [--briefing]
        var reportIdx = Array.IndexOf(args, "--report");
        if (reportIdx >= 0 && reportIdx + 1 < args.Length)
        {
            var code = await RunReportAsync(args[reportIdx + 1], args.Contains("--briefing"));
            Shutdown(code);
            return;
        }

        // Developer tool: renders every window with sample data into a folder (README screenshots).
        var shotsIdx = Array.IndexOf(args, "--screenshots");
        if (shotsIdx >= 0 && shotsIdx + 1 < args.Length)
        {
            try { await ScreenshotRunner.RunAsync(args[shotsIdx + 1]); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[shotsIdx + 1], "screenshots-error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }

        // Prints the Authenticode verdict for a file (used on a clean machine before enabling RequireSignatureOnStable).
        var verifyIdx = Array.IndexOf(args, "--verify");
        if (verifyIdx >= 0 && verifyIdx + 2 < args.Length)
        {
            var r = new WinTrustVerifier().Verify(args[verifyIdx + 2]);
            File.WriteAllText(args[verifyIdx + 1], $"{r.Status}|{r.Signer}|{r.Detail}");
            Shutdown();
            return;
        }

        // Logging and crash handlers first, so every early exit below leaves a trace.
        _log = new FileLog(FileLog.DefaultDirectory);
        DispatcherUnhandledException += (_, ex) => { _log.Error("Unhandled UI exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            _log.Error($"Unhandled exception (terminating={ex.IsTerminating})", ex.ExceptionObject as Exception);
            if (ex.IsTerminating) CrashRestart.TryRestart(Environment.ProcessPath, _log);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { _log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        // After a self-update the old process may still be exiting; give it time to release the mutex.
        var postUpdate = args.Contains("--post-update") || args.Contains(CrashRestart.Argument);

        // Run from Downloads: move into AppData so self-updates are not lost when Downloads is cleaned, then delete the download.
        if (!postUpdate && !args.Contains(SelfInstall.RelocatedArgument)
            && Environment.ProcessPath is { } self && SelfInstall.ShouldRelocate(self, SelfInstall.DownloadFolders()))
        {
            var moved = SelfInstall.Relocate(self, _log);
            if (moved.Outcome is SelfInstall.Outcome.Relocated or SelfInstall.Outcome.UsedExisting)
            {
                MessageBox.Show(
                    "PatchRadar has installed itself to:\n" + SelfInstall.InstallDirectory +
                    "\n\nThis downloaded copy will now be deleted, and PatchRadar will keep updating itself from the new location. " +
                    "You can start it from the tray icon, or it will start with Windows.",
                    "PatchRadar installed", MessageBoxButton.OK, MessageBoxImage.Information);
                SelfInstall.RemoveOriginal(self, _log);
                Shutdown();
                return;
            }
            // Could not move (for example a locked-down profile): carry on from Downloads rather than not start.
        }

        var relocated = args.Contains(SelfInstall.RelocatedArgument);
        postUpdate |= relocated;   // the downloaded copy is still exiting: wait for the mutex and stay quiet
        var wait = postUpdate ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        try { _single = new SingleInstance(wait: wait); }
        catch (Exception ex)
        {
            // Mutex could not be opened (for example owned by an elevated or other-session instance): carry on rather than die silently.
            _log.Error("Single-instance check failed; starting anyway", ex);
        }
        if (_single is { IsFirst: false })
        {
            _log.Warn($"Another PatchRadar instance is already running; exiting (args: {string.Join(' ', args)})");
            if (!postUpdate)
                MessageBox.Show("PatchRadar is already running. Look for its icon in the system tray (click the ^ arrow if it is hidden).",
                    "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try { StartUp(); }
        catch (Exception ex)
        {
            _log.Error("Startup failed", ex);
            MessageBox.Show("PatchRadar could not start. Details were written to " + FileLog.DefaultDirectory + "\n\n" + ex.Message,
                "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartUp()
    {
        _log ??= new FileLog(FileLog.DefaultDirectory);   // already set by OnStartup; lets the compiler see it is non-null
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
            try { Process.Start(new ProcessStartInfo(installer.LaunchPath, "--post-update") { UseShellExecute = false }); }
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

        var state = new StateStore(Paths.StateFile, _log);
        var ignores = new IgnoreStore(state);
        var scan = BuildScanService(host, _supportClient, _log, new ReviewStore(state, null, Paths.ReviewSnapshotDir), ignores);
        var toasts = new ToastTracker(state);

        var updater = new AppUpdater(() => host.Current.AppUpdate,
            new GitHubReleaseSource(_githubClient.Get, AppVersion.Running().ToString(), Paths.UpdatesDir, _log),
            new AllowlistedDownloader(_downloadClient.Get, AppVersion.Running().ToString()),
            new WinTrustVerifier(), installer, new ExitingLauncher(), Paths.UpdatesDir,
            AppVersion.Running(), AppVersion.Flavour(), failed => SaveSkipped(host, failed), log: _log);
        TrayController? tray = null;
        _updates = new UpdateCoordinator(updater, () => host.Current.AppUpdate,
            (title, msg, url) => tray?.ShowToast(title, msg, url is null ? null : () => OpenReleasePage(url)), _log);

        var uninstall = new UninstallService(new UninstallPlanner(new RegistryCbsPackageSource()), new UacRunner(), new WindowsPrivilegeInfo(),
            new UninstallAudit(Paths.UninstallAuditFile, _log), () => host.Current, () => scan.InstalledUpdates, _log);
        tray = _tray = new TrayController(scan, host, toasts, new DigestStore(state), _updates, _log, uninstall, new PendingSeenStore(state), new RebootReminder(state));
        _tray.Start();
        _updates.Start();
        StartProblemWatcher(host, tray);

        // Tell the user what the last start did.
        if (startup.Action == StartupAction.JustUpdated) _tray.ShowToast("PatchRadar updated", $"Updated to v{startup.Version}.");
        else if (startup.Action == StartupAction.RollbackFailed) _tray.ShowToast("PatchRadar update problem", "An update failed and could not be rolled back automatically. See the log folder.");
        if (pending.TakeNotice() is { } notice) _tray.ShowToast("PatchRadar", notice);

        // 60 s of life = the new build is good: remove .old and pending.json.
        _healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateHealth.HealthyAfter };
        _healthTimer.Tick += (_, _) => { _healthTimer.Stop(); health.MarkHealthy(); };
        _healthTimer.Start();
    }

    /// <summary>Looks through the log now and then for errors and offers (never forces) a GitHub report. Development builds stay quiet.</summary>
    private void StartProblemWatcher(SettingsHost host, TrayController? tray)
    {
        if (_log is null || _githubClient is null) return;
        var store = new ProblemStore(Path.Combine(Paths.LocalAppData, "problems.json"), _log);
        var lookup = new ProblemLookup(_githubClient.Get, _log);
        var os = $"{Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";

        void OpenReport(ProblemReport report) => Dispatcher.BeginInvoke(() =>
        {
            // A screenshot is only offered when a PatchRadar window is already open, and only of that window.
            BitmapSource? shot = null;
            if (Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is not ProblemReportWindow) is { } open)
                try { shot = WindowCapture.ToBitmap(open); } catch (Exception ex) { _log.Warn("Could not capture a window: " + ex.Message); }
            new ProblemReportWindow(report, lookup, store, _log, shot).Show();
        });

        var watcher = new ProblemWatcher(FileLog.DefaultDirectory, store, () => host.Current.Advanced.OfferProblemReports,
            (title, msg, click) => tray?.ShowToast(title, msg, click), OpenReport,
            $"{AppVersion.Running()} ({AppVersion.Flavour()})", os, _log);

        // Advanced menu (Shift or Alt): scan the log now and open the newest error for review, in any build.
        if (tray is not null)
            tray.CheckProblemsNow = () =>
            {
                var found = watcher.Groups(DateTime.UtcNow);
                if (found.Count == 0) tray.ShowToast("PatchRadar", "No errors with a stack trace in the last 7 days of logs.");
                else if (found.Count == 1) OpenReport(found[0].Report);
                else new ProblemListWindow(found, OpenReport).Show();
            };

        if (AppVersion.Running().IsDevBuild) return;      // the automatic check stays off in development builds
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timer.Tick += (_, _) => { timer.Interval = TimeSpan.FromHours(6); watcher.CheckNow(DateTime.UtcNow); };
        timer.Start();
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
        if (UrlGuard.Launch(url, _log)) return;
        _log?.Error("Could not open the release page", new InvalidOperationException(url));
        UrlGuard.ShowLinkFallback(url);
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

    private static ScanService BuildScanService(SettingsHost host, ClientHolder support, ILog log, ReviewStore reviews, IgnoreStore? ignores = null)
    {
        var s = host.Current;
        var gate = new RequestGate(s.Network.MaxConcurrent, TimeSpan.FromMilliseconds(500));
        var source = new SupportSiteSource(support.Get, gate, log);
        var releaseHealth = new ReleaseHealthSource(support.Get, gate, OsInfo.Current,
            () => host.Current.Advanced.EnableReleaseHealthFallback, Paths.ReleaseHealthCacheDir, log);
        var cache = new KbCache(Paths.KbCacheDir, log);
        // Order: plain request, then (opt-in) a real browser render for the full article, then release health.
        var webView = new WebViewSource(new WebView2Renderer(Paths.WebViewDir, log), gate, () => host.Current.Advanced.EnableWebView2Fallback, log);
        var fetcher = new KbFetcher(new IKbSource[] { source, webView, releaseHealth }, cache, log, enrichers: new IKbEnricher[] { releaseHealth });
        var history = new UpdateCollector(new IUpdateHistoryProvider[] { new WuaHistoryProvider(), new QfeProvider() }, log);
        return new ScanService(history, fetcher, () => host.Current, log, reviews, new WuaPendingProvider(), ignores, new RegistryRestartDetector());
    }

    /// <summary>
    /// Scans once with no UI and writes the result. Exit code: 0 nothing red, 1 a red update is pending,
    /// 2 a red update is installed (none pending), 3 the scan failed.
    /// </summary>
    private static async Task<int> RunReportAsync(string path, bool briefing)
    {
        try
        {
            var log = NullLog.Instance;
            var store = new SettingsStore(Paths.SettingsFile, log);
            var host = new SettingsHost(store, store.Load());
            using var client = new ClientHolder(n => SupportSiteSource.CreateClient(n), () => host.Current.Network);
            var state = new StateStore(Paths.StateFile, log);
            var scan = BuildScanService(host, client, log, new ReviewStore(state, null, Paths.ReviewSnapshotDir), new IgnoreStore(state));
            var result = await scan.ScanAsync(force: false, progress: null, CancellationToken.None);

            var rows = result.Pending.Concat(result.Rows).ToList();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var text = briefing
                ? Export.Briefing.Render(result.Pending, result.RestartPending, DateTime.Now, Environment.MachineName)
                : Export.Exporters.Render(ext switch { ".json" => Export.ExportFormat.Json, ".csv" => Export.ExportFormat.Csv, _ => Export.ExportFormat.Markdown }, rows);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(ext == ".csv"));

            if (result.Pending.Any(r => r.Severity == Severity.Red && !r.Ignored)) return 1;
            if (result.Rows.Any(r => r.Severity == Severity.Red && !r.Ignored)) return 2;
            return 0;
        }
        catch (Exception) { return 3; }
    }

    /// <summary>
    /// Headless check used for acceptance tests: times the update list and live-fetches KBs.
    /// Usage: PatchRadar.exe --diag out.txt [KB5127648 ...] [--webview]
    /// --webview also renders the first KB through the hidden WebView2 (reports whether the runtime exists).
    /// </summary>
    private static async Task RunDiagAsync(string outPath, string[] kbs, bool webview)
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

            var swp = Stopwatch.StartNew();
            try
            {
                var pend = await new WuaPendingProvider().GetAsync(true, default);
                L($"Pending: {pend.Count} updates in {swp.ElapsedMilliseconds} ms");
                foreach (var u in pend) L($"  {u.Kb}  available {u.InstalledOn.LocalDateTime:yyyy-MM-dd}  {u.Category,-14} {u.Title}");
            }
            catch (Exception ex) { L($"Pending: FAILED {ex.GetType().Name}: {ex.Message}"); }

            var sw = Stopwatch.StartNew();
            var all = await history.GetAsync(default);
            var filtered = UpdateMerger.Filter(all, settings.Updates, DateTimeOffset.UtcNow);
            L($"Collector: {all.Count} merged, {filtered.Count} after default filters, {sw.ElapsedMilliseconds} ms");
            foreach (var u in filtered.Take(15)) L($"  {u.Kb}  {u.InstalledOn.LocalDateTime:yyyy-MM-dd}  {u.Category,-14} {u.Source}  {u.Title}");

            // What the Uninstall button would be offered for (read-only: nothing is ever run here).
            var planner = new UninstallPlanner(new RegistryCbsPackageSource());
            L($"Uninstall planner (admin-capable: {new WindowsPrivilegeInfo().IsAdminCapable}):");
            foreach (var u in filtered.Take(15))
            {
                var plan = planner.Plan(u);
                L($"  {u.Kb,-10} {plan.Method,-18} {(plan.CanUninstall ? plan.Command : plan.Reason)}");
            }

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

            if (webview)
            {
                var renderer = new WebView2Renderer(Paths.WebViewDir);
                L($"WebView2 runtime available: {renderer.IsAvailable}");
                if (renderer.IsAvailable)
                {
                    var kb = (kbs.Length > 0 ? kbs : new[] { "KB5127648" })[0];
                    var sw4 = Stopwatch.StartNew();
                    var d = await new WebViewSource(renderer, gate, () => true).FetchAsync(kb, default);
                    L($"  WebView2 {kb}: status={d.Status} err={d.LastError} improvements={d.Improvements.Count} issues={d.KnownIssues.Count} {sw4.ElapsedMilliseconds} ms");
                }
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
        _log?.Info($"PatchRadar exiting (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }
}
