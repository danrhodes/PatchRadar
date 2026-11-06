using AppTheme = PatchRadar.Settings.ThemeMode;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Uninstall;
using PatchRadar.Updates;

namespace PatchRadar.Views;

/// <summary>
/// Developer tool: <c>PatchRadar.exe --screenshots &lt;folder&gt;</c> opens each window with built-in sample data
/// (nothing from this machine) in light and dark, and saves PNGs named as the README expects.
/// </summary>
public static class ScreenshotRunner
{
    public static async Task RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var app = Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var work = Path.Combine(Path.GetTempPath(), "patchradar-screens-" + Environment.ProcessId);
        Directory.CreateDirectory(work);
        try
        {
            var sample = Sample.Build(Path.Combine(work, "kb"));
            var settings = new AppSettings();
            var host = new SettingsHost(new SettingsStore(Path.Combine(work, "settings.json")), settings);
            var scan = new ScanService(new FakeHistory(sample.Installed), sample.Fetcher, () => host.Current,
                reviews: null, pending: new FakePending(sample.Pending));

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme);
                var suffix = theme == AppTheme.Dark ? "dark" : "light";
                string Path_(string n) => Path.Combine(outDir, $"{n}-{suffix}.png");

                await Shoot(new DetailWindow(sample.Main, settings.Appearance.Colors, r => Task.FromResult<UpdateRow?>(r), (r, _) => r, scan.IsInstalled, NullLog.Instance) { Height = 640 }, Path_("detail"));
                await Shoot(new DetailWindow(sample.Changed, settings.Appearance.Colors, r => Task.FromResult<UpdateRow?>(r), (r, _) => r, scan.IsInstalled, NullLog.Instance) { Height = 640 }, Path_("detail-changed"));

                var items = DigestBuilder.Build(sample.All, DateTime.UtcNow.AddDays(-7), SeverityEngine.Default());
                await Shoot(new DigestWindow(items, DateTime.UtcNow.AddDays(-7), settings.Appearance.Colors, _ => { }, () => { }), Path_("digest"));

                await Shoot(new AllUpdatesWindow(scan, host, _ => { }, NullLog.Instance), Path_("all-updates"), settle: 1500);
                var ranked = new AllUpdatesWindow(scan, host, _ => { }, NullLog.Instance);
                await Shoot(ranked, Path_("ranked"), settle: 1500, beforeShot: () => ranked.ApplyForScreenshot(null, rank: true));
                var symptom = new AllUpdatesWindow(scan, host, _ => { }, NullLog.Instance);
                await Shoot(symptom, Path_("symptom-search"), settle: 1500, beforeShot: () => symptom.ApplyForScreenshot("wifi", rank: false));

                await Shoot(new SettingsWindow(host, NullLog.Instance), Path_("settings"));

                var entry = new ProblemEntry(DateTime.Now.AddMinutes(-20),
                    string.Join("\n",
                        "2026-11-06 10:00:00.000 [Error] Scan failed: System.InvalidOperationException: Sequence contains no elements",
                        "   at System.Linq.ThrowHelper.ThrowNoElementsException()",
                        "   at PatchRadar.Scan.ScanService.MakeRow(InstalledUpdate u) in ScanService.cs:line 191",
                        "   at PatchRadar.Scan.ScanService.ScanAsync(Boolean force) in ScanService.cs:line 77"),
                    new[] { "2026-11-06 09:59:58.120 [Information] Scan started", "2026-11-06 09:59:59.480 [Information] 42 updates read" });
                var report = ProblemIssue.Build(entry, ProblemScrubber.ScrubCurrent, "1.3.0 (full)", "10.0.26300 (64-bit)");
                await Shoot(new ProblemReportWindow(report, new ProblemLookup(() => new System.Net.Http.HttpClient()),
                    new ProblemStore(Path.Combine(work, "p.json")), NullLog.Instance), Path_("problem-report"));

                var plan = new UninstallPlan(UninstallMethod.DismRemovePackage, "", "KB5127648",
                    "Package_for_RollupFix~31bf3856ad364e35~amd64~~26300.9711.1.8", IsSecurityUpdate: true);
                await Shoot(new UninstallConfirmWindow(sample.Main, plan, "alex"), Path_("uninstall-confirm"));
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task Shoot(Window w, string path, int settle = 700, Action? beforeShot = null)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = 80; w.Top = 40;
        w.ThemeMode = Application.Current.ThemeMode;
        w.Show();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await Task.Delay(settle);
        if (beforeShot is not null) { beforeShot(); await Task.Delay(500); }
        WindowCapture.SavePng(w, path);
        w.Close();
        await Task.Delay(150);
    }

    // ---- sample data ----

    private sealed class FakeHistory(IReadOnlyList<InstalledUpdate> items) : IUpdateHistoryProvider
    {
        public Task<IReadOnlyList<InstalledUpdate>> GetAsync(CancellationToken ct) => Task.FromResult(items);
    }

    private sealed class FakePending(IReadOnlyList<InstalledUpdate> items) : IPendingUpdateProvider
    {
        public Task<IReadOnlyList<InstalledUpdate>> GetAsync(bool online, CancellationToken ct) => Task.FromResult(items);
    }

    private sealed class Sample
    {
        public required IReadOnlyList<InstalledUpdate> Installed { get; init; }
        public required IReadOnlyList<InstalledUpdate> Pending { get; init; }
        public required KbFetcher Fetcher { get; init; }
        public required UpdateRow Main { get; init; }
        public required UpdateRow Changed { get; init; }
        public required IReadOnlyList<UpdateRow> All { get; init; }

        private static InstalledUpdate U(string kb, string title, int daysAgo, UpdateCategory cat, UpdateState state = UpdateState.Installed)
            => new(kb, title, DateTimeOffset.Now.AddDays(-daysAgo).AddHours(-3), state, cat);

        private static KbImprovement Imp(string cat, string text) => new() { Category = cat, Text = text };

        private static KbData Article(string kb, string title, string build, params KbIssue[] issues)
        {
            var d = new KbData
            {
                Kb = kb, Title = title, OsBuild = build, Status = KbStatus.Ok, Source = "support.microsoft.com", FetchedUtc = DateTime.UtcNow,
                ContentHash = kb + issues.Length, CanonicalUrl = "https://support.microsoft.com/help/" + kb.Substring(2),
                Improvements =
                {
                    Imp("Security", "This update makes quality improvements to the servicing stack and fixes security issues."),
                    Imp("Reliability", "Fixes an issue that could cause some apps to stop responding after sign-in."),
                },
                KnownIssues = issues.ToList(),
            };
            d.RawText = string.Join("\n", d.Improvements.Select(i => i.Text).Concat(issues.Select(i => i.Title + " " + i.Symptom + " " + i.Workaround)));
            return d;
        }

        public static Sample Build(string cacheDir)
        {
            var cache = new KbCache(cacheDir);
            var engine = SeverityEngine.Default();

            var sac = new KbIssue
            {
                Title = "You might be unable to turn on Smart App Control",
                AppliesTo = "Windows 11, version 24H2",
                Symptom = "After installing this update, some devices might be unable to turn on Smart App Control in the Windows Security app if it has been turned off.",
                Workaround = "Modify the registry value described in the Microsoft article, then restart the device.",
            };
            var rdp = new KbIssue
            {
                Title = "Remote Desktop connections might fail for non-admin users with error 0xC000000D",
                AppliesTo = "Windows 11, version 24H2",
                Symptom = "Remote Desktop connections using NTLMv1 and MS-CHAPv2 authentication might fail for standard users when Credential Guard is on.",
                Workaround = "Sign in with an administrator account or use Kerberos authentication.",
            };
            var wifi = new KbIssue
            {
                Title = "Wi-Fi and VPN connections might drop after resume",
                AppliesTo = "Windows 11, version 24H2",
                Symptom = "Some devices lose Wi-Fi and VPN connectivity after resuming from sleep.",
                Resolution = "This issue is partly fixed by the out-of-band update KB5129195.",
                ResolvedInKb = "KB5129195", OutOfBandFix = true, PartialFix = true,
            };
            var main = Article("KB5127648", "2026-10 Security Update (KB5127648) (26300.9711)", "26300.9711", sac, rdp, wifi);
            main.Improvements.Add(Imp("Networking", "Fixes an issue that caused Wi-Fi connections to drop after the device wakes from sleep."));
            main.RawText += " " + main.Improvements[^1].Text;

            // The same kind of article after Microsoft edited it: this is the "changed since reviewed" case.
            var ntlmBefore = new KbIssue { Title = "NTLM sign-in might fail after the update", AppliesTo = "Windows 11, version 24H2", Symptom = "Some apps that use NTLM sign-in might fail to authenticate." };
            var ntlmAfter = new KbIssue
            {
                Title = ntlmBefore.Title, AppliesTo = ntlmBefore.AppliesTo, Symptom = ntlmBefore.Symptom,
                Workaround = "Restart the affected app after signing in again.",
                Resolution = "Fixed in KB5129195.", ResolvedInKb = "KB5129195", OutOfBandFix = true,
            };
            var cum = Article("KB5044284", "2026-10 Cumulative Update for Windows 11 Version 24H2 (KB5044284)", "26100.9650", ntlmAfter);
            var cumBefore = Article("KB5044284", cum.Title!, cum.OsBuild!, ntlmBefore);
            cum.Previous = KbSnapshot.Of(cumBefore, DateTime.UtcNow.AddDays(-4));
            cum.ChangedUtc = DateTime.UtcNow.AddDays(-1);

            var server = Article("KB5044293", "2026-10 Cumulative Update for Windows Server 2016 (KB5044293)", "14393.9100");
            var dotnet = Article("KB5044033", "2026-10 .NET Framework Security Update (KB5044033)", "");
            dotnet.Improvements.Clear(); dotnet.Improvements.Add(Imp("Security", "Security improvements for .NET Framework."));
            dotnet.RawText = dotnet.Improvements[0].Text;
            var oob = Article("KB5129195", "2026-11 Out-of-band Update for Windows 11 Version 24H2 (KB5129195)", "26300.9730");

            foreach (var k in new[] { main, cum, server, dotnet, oob }) cache.Save(k);

            var installed = new List<InstalledUpdate>
            {
                U("KB5127648", main.Title!, 23, UpdateCategory.Security),
                U("KB5044284", cum.Title!, 12, UpdateCategory.Cumulative),
                U("KB5044293", server.Title!, 10, UpdateCategory.Cumulative),
                U("KB5044033", dotnet.Title!, 9, UpdateCategory.DotNet),
            };
            var pending = new List<InstalledUpdate> { U("KB5129195", oob.Title!, 2, UpdateCategory.Security, UpdateState.Pending) };

            var fetcher = new KbFetcher(Array.Empty<IKbSource>(), cache);

            UpdateRow Row(InstalledUpdate u, KbData k) => new(u, k, engine.Evaluate(k));
            
            var mainRow = new UpdateRow(installed[0], main, engine.Evaluate(main))
            {
                Fixes = new[] { new FixInfo("KB5129195", false, true, true, wifi.Title!, false) },
            };
            var cumRow = new UpdateRow(installed[1], cum, engine.Evaluate(cum))
            {
                Review = ReviewStatus.ChangedSinceReviewed, ReviewedUtc = DateTime.UtcNow.AddDays(-5),
                ChangeSinceReview = KbDiff.Compare(cum.Previous!, cum),
            };
            var all = new List<UpdateRow> { mainRow, cumRow, Row(installed[2], server), Row(installed[3], dotnet), Row(pending[0], oob) };
            return new Sample { Installed = installed, Pending = pending, Fetcher = fetcher, Main = mainRow, Changed = cumRow, All = all };
        }
    }
}
