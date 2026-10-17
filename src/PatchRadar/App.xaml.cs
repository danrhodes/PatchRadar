using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.Tray;
using PatchRadar.Updates;

namespace PatchRadar;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayController? _tray;
    private FileLog? _log;
    private HttpClient? _http;

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
        var wait = args.Contains("--post-update") ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        _single = new SingleInstance(wait: wait);
        if (!_single.IsFirst) { Shutdown(); return; }

        _log = new FileLog(FileLog.DefaultDirectory);
        DispatcherUnhandledException += (_, ex) => { _log.Error("Unhandled UI exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => _log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { _log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        var store = new SettingsStore(Paths.SettingsFile, _log);
        var settings = store.Load();
        _log.MinLevel = Enum.TryParse<Infra.LogLevel>(settings.Advanced.LogLevel, true, out var lvl) ? lvl : Infra.LogLevel.Information;
        _log.Info("PatchRadar starting");

        var (scan, http) = BuildScanService(() => settings, _log);
        _http = http;
        _tray = new TrayController(scan, () => settings, _log);
        _tray.Start();
    }

    private static (ScanService, HttpClient) BuildScanService(Func<AppSettings> settings, ILog log)
    {
        var s = settings();
        var http = SupportSiteSource.CreateClient(s.Network);
        var gate = new RequestGate(s.Network.MaxConcurrent, TimeSpan.FromMilliseconds(500));
        var source = new SupportSiteSource(http, gate, log);
        var cache = new KbCache(Paths.KbCacheDir, log);
        var fetcher = new KbFetcher(new IKbSource[] { source }, cache, log);
        var history = new UpdateCollector(new IUpdateHistoryProvider[] { new WuaHistoryProvider(), new QfeProvider() }, log);
        return (new ScanService(history, fetcher, settings, log), http);
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
            var src = new SupportSiteSource(http, new RequestGate(2, TimeSpan.FromMilliseconds(500)));
            var engine = SeverityEngine.Default();
            foreach (var kb in kbs.Length > 0 ? kbs : new[] { "KB5127648" })
            {
                var sw2 = Stopwatch.StartNew();
                var d = await src.FetchAsync(kb, default);
                var r = engine.Evaluate(d);
                L($"Fetch {kb}: status={d.Status} err={d.LastError} improvements={d.Improvements.Count} issues={d.KnownIssues.Count} severity={r.Severity} keywords=[{string.Join(", ", r.MatchedKeywords)}] url={d.CanonicalUrl} {sw2.ElapsedMilliseconds} ms");
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
        _tray?.Dispose();
        _http?.Dispose();
        _single?.Dispose();
        _log?.Info("PatchRadar exiting");
        base.OnExit(e);
    }
}
