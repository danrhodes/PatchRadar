using System.Net;
using System.Text.Json;
using PatchRadar.Core;
using PatchRadar.Export;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;

namespace PatchRadar.Tests;

internal static class Rows
{
    public static UpdateRow FromFixture(string fixture, string kb, string title, int daysAgo = 3, UpdateCategory cat = UpdateCategory.Cumulative)
    {
        var d = Fx.Parse(fixture);
        d.Kb = kb;
        var u = new InstalledUpdate(kb, title, DateTimeOffset.UtcNow.AddDays(-daysAgo), UpdateState.Installed, cat);
        return new UpdateRow(u, d, SeverityEngine.Default().Evaluate(d));
    }

    public static UpdateRow Grey(string kb, string title, int daysAgo = 3) =>
        new(new InstalledUpdate(kb, title, DateTimeOffset.UtcNow.AddDays(-daysAgo), UpdateState.Installed, UpdateCategory.Other),
            null, SeverityEngine.Default().Evaluate(null));
}

public class UpdateFilterTests
{
    private static readonly UpdateRow Golden = Rows.FromFixture("kb5127648", "KB5127648", "2026-10 Cumulative Update for Windows 11");
    private static readonly UpdateRow Other = Rows.FromFixture("kb5044293", "KB5044293", "2024-10 Cumulative Update", 40);
    private static readonly UpdateRow NoData = Rows.Grey("KB1111111", "Some driver", 10);

    private static IReadOnlyList<string> Kbs(UpdateFilterCriteria c) =>
        UpdateFilter.Apply(new[] { Golden, Other, NoData }, c).Select(r => r.Update.Kb).ToList();

    [Theory]
    [InlineData("NTLM")]
    [InlineData("wifi")]      // KB text says "Wi-Fi"
    [InlineData("Wi-Fi")]
    [InlineData("mschapv2")]  // KB text says "MS-CHAPv2"
    [InlineData("BlockNtlmv1SSO")]
    public void SymptomSearch_FindsGoldenKb(string query) =>
        Assert.Contains("KB5127648", Kbs(new UpdateFilterCriteria { Symptom = query }));

    [Fact]
    public void SymptomSearch_NoMatch_ReturnsNothing() =>
        Assert.Empty(Kbs(new UpdateFilterCriteria { Symptom = "zzzqqqxx" }));

    [Fact]
    public void SymptomSearch_IgnoresRowsWithoutKbData() =>
        Assert.DoesNotContain("KB1111111", Kbs(new UpdateFilterCriteria { Symptom = "update" }));

    [Fact]
    public void SeverityFilter() =>
        Assert.Equal(new[] { "KB1111111" }, Kbs(new UpdateFilterCriteria { Severity = Severity.Grey }));

    [Fact]
    public void CategoryFilter() =>
        Assert.Equal(new[] { "KB1111111" }, Kbs(new UpdateFilterCriteria { Category = UpdateCategory.Other }));

    [Fact]
    public void DateRange_IsInclusive()
    {
        var from = DateTime.Now.AddDays(-12).Date;
        var to = DateTime.Now.AddDays(-8).Date;
        Assert.Equal(new[] { "KB1111111" }, Kbs(new UpdateFilterCriteria { From = from, To = to }));
    }

    [Fact]
    public void TextFilter_MatchesKbOrTitle()
    {
        Assert.Equal(new[] { "KB5044293" }, Kbs(new UpdateFilterCriteria { Text = "5044293" }));
        Assert.Equal(new[] { "KB1111111" }, Kbs(new UpdateFilterCriteria { Text = "driver" }));
    }

    [Fact]
    public void Filters_Combine()
    {
        Assert.Empty(Kbs(new UpdateFilterCriteria { Severity = Severity.Red, Text = "driver" }));
    }
}

public class ExporterTests
{
    private static readonly UpdateRow Golden = Rows.FromFixture("kb5127648", "KB5127648", "2026-10 Cumulative Update, \"quoted\" | pipe");
    private static readonly UpdateRow Grey = Rows.Grey("KB1111111", "=HYPERLINK(\"x\")");

    [Fact]
    public void Csv_HasHeaderAndEscapesCommasQuotes()
    {
        var csv = CsvExporter.Render(new[] { Golden });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Severity,KB,Title", lines[0]);
        Assert.Contains("\"2026-10 Cumulative Update, \"\"quoted\"\" | pipe\"", lines[1]);
        Assert.StartsWith("Red,KB5127648,", lines[1]);
    }

    [Fact]
    public void Csv_NeutralisesFormulas() =>
        Assert.Equal("'=HYPERLINK(\"\"x\"\")", CsvExporter.Escape("=HYPERLINK(\"x\")").Trim('"'));

    [Fact]
    public void Json_IsValidAndCarriesDetail()
    {
        using var doc = JsonDocument.Parse(JsonExporter.Render(new[] { Golden, Grey }));
        var arr = doc.RootElement;
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("Red", arr[0].GetProperty("severity").GetString());
        Assert.Contains("NTLMv1", arr[0].GetProperty("matchedKeywords").EnumerateArray().Select(e => e.GetString()));
        Assert.True(arr[0].GetProperty("behaviourChanges").GetArrayLength() > 0);
        Assert.Equal("NotFetched", arr[1].GetProperty("kbStatus").GetString());
    }

    [Fact]
    public void Markdown_IsATableWithEscapedPipesAndLinks()
    {
        var md = MarkdownExporter.Render(new[] { Golden });
        var lines = md.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("| Risk | KB | Installed | Title | Notes |", lines[0]);
        Assert.Equal("|---|---|---|---|---|", lines[1]);
        Assert.Contains("[KB5127648](https://support.microsoft.com/help/5127648)", lines[2]);
        Assert.Contains("\\| pipe", lines[2]);
        // every data row has the same number of unescaped cells as the header
        Assert.Equal(6, lines[2].Replace("\\|", "").Count(c => c == '|'));
    }

    [Fact]
    public void Render_DispatchesByFormat()
    {
        Assert.Equal(CsvExporter.Render(new[] { Golden }), Exporters.Render(ExportFormat.Csv, new[] { Golden }));
        Assert.Equal(".md", Exporters.Extension(ExportFormat.Markdown));
    }
}

public class ReleaseHealthTests
{
    private static string Html => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rh", "status-windows-11-24h2.html"));

    [Fact]
    public void Parser_ReadsKnownIssuesTable()
    {
        var page = ReleaseHealthParser.Parse(Html, "u", DateTime.UtcNow);
        Assert.Contains("24H2", page.Heading);
        Assert.True(page.Issues.Count >= 5);
        var rdp = page.Issues.Single(i => i.Title.StartsWith("Remote Desktop connections might fail"));
        Assert.Equal("KB5124010", rdp.OriginatingKb);
        Assert.Equal("Mitigated", rdp.Status);
        Assert.False(rdp.IsResolved);
        var black = page.Issues.Single(i => i.Title.StartsWith("Devices might experience a black screen"));
        Assert.True(black.IsResolved);
        Assert.Equal("KB5127648", black.ResolvedInKb);
        Assert.StartsWith("Microsoft has received reports", black.Description);
    }

    [Theory]
    [InlineData(26100, false, "24H2", "status-windows-11-24h2")]
    [InlineData(26300, false, "26H2", "status-windows-11-26h2")]
    [InlineData(22631, false, null, "status-windows-11-23h2")]
    [InlineData(26100, true, "24H2", "status-windows-server-2025")]
    [InlineData(20348, true, "21H2", "status-windows-server-2022")]
    [InlineData(17763, true, null, "status-windows-10-1809-and-windows-server-2019")]
    [InlineData(14393, true, null, "status-windows-10-1607-and-windows-server-2016")]
    public void Pages_MapOsToStatusPage(int build, bool server, string? dv, string slug) =>
        Assert.Equal("https://learn.microsoft.com/en-us/windows/release-health/" + slug,
            Assert.Single(ReleaseHealthPages.UrlsFor(new OsInfo(build, server, dv))));

    [Fact]
    public void Pages_UnknownOs_GivesNothing()
    {
        Assert.Empty(ReleaseHealthPages.UrlsFor(new OsInfo(19045, false, "22H2")));  // Windows 10 is out of scope
        Assert.Empty(ReleaseHealthPages.UrlsFor(new OsInfo(25398, true, "23H2")));
    }

    private static (ReleaseHealthSource src, FakeHandler h) Make(string? html = null, bool enabled = true)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, html ?? Html);
        var http = new HttpClient(h);
        var gate = new RequestGate(1, TimeSpan.Zero);
        return (new ReleaseHealthSource(() => http, gate, () => new OsInfo(26100, false, "24H2"), () => enabled), h);
    }

    [Fact]
    public async Task Fetch_ReturnsIssuesByOriginatingKb()
    {
        var (src, _) = Make();
        var d = await src.FetchAsync("KB5124010", default);
        Assert.Equal(KbStatus.Ok, d.Status);
        Assert.True(d.KnownIssues.Count >= 2);
        Assert.Contains(d.KnownIssues, i => i.Title!.StartsWith("Remote Desktop") && i.Status == IssueStatus.Active);
        Assert.Contains(d.KnownIssues, i => i.Title!.StartsWith("Applications which require AC-3") && i.Status == IssueStatus.Resolved && i.Workaround.Contains("KB5127648"));
        Assert.NotNull(d.Notice);
        Assert.NotEmpty(d.ContentHash);
    }

    [Fact]
    public async Task Fetch_UnlistedKb_IsAFailureNotAGreen()
    {
        var (src, _) = Make();
        var d = await src.FetchAsync("KB9999999", default);
        Assert.Equal(KbStatus.Failed, d.Status);
    }

    [Fact]
    public async Task Page_IsFetchedOnceThenServedFromMemory()
    {
        var (src, h) = Make();
        await src.FetchAsync("KB5124010", default);
        await src.FetchAsync("KB5120998", default);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task SourceAKilled_KnownIssueStillResolvesViaB()
    {
        using var t = new TempDir();
        var blocked = new BlockedSource();
        var (b, _) = Make();
        var f = new KbFetcher(new IKbSource[] { blocked, b }, new KbCache(t.Path));
        var upd = new InstalledUpdate("KB5124010", "t", DateTimeOffset.UtcNow.AddDays(-2), UpdateState.Installed, UpdateCategory.Cumulative);

        var kb = (await f.GetAsync(upd, new ScanningSettings(), false, default))!;

        Assert.Equal(KbStatus.Ok, kb.Status);
        Assert.Contains(kb.KnownIssues, i => i.Title!.StartsWith("Remote Desktop"));
        var r = SeverityEngine.Default().Evaluate(kb);
        Assert.Equal(Severity.Red, r.Severity);          // Remote Desktop is a critical keyword
        Assert.Contains("Remote Desktop", r.MatchedKeywords);
    }

    [Fact]
    public async Task BothSourcesFail_ErrorNamesBoth_AndKeepsPrimaryStatus()
    {
        using var t = new TempDir();
        var (b, _) = Make();
        var f = new KbFetcher(new IKbSource[] { new BlockedSource(), b }, new KbCache(t.Path));
        var upd = new InstalledUpdate("KB9999999", "t", DateTimeOffset.UtcNow.AddDays(-2), UpdateState.Installed, UpdateCategory.Cumulative);
        var kb = (await f.GetAsync(upd, new ScanningSettings(), false, default))!;
        Assert.NotEqual(KbStatus.Ok, kb.Status);
        Assert.Contains("HTTP 403", kb.LastError);
        Assert.Contains("release health", kb.LastError);
    }

    [Fact]
    public async Task Disabled_SourceIsSkipped()
    {
        using var t = new TempDir();
        var (b, h) = Make(enabled: false);
        var f = new KbFetcher(new IKbSource[] { new BlockedSource(), b }, new KbCache(t.Path));
        var upd = new InstalledUpdate("KB5124010", "t", DateTimeOffset.UtcNow.AddDays(-2), UpdateState.Installed, UpdateCategory.Cumulative);
        var kb = (await f.GetAsync(upd, new ScanningSettings(), false, default))!;
        Assert.NotEqual(KbStatus.Ok, kb.Status);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Enrichment_AddsMissingIssues_WithoutDuplicating()
    {
        var (b, _) = Make();
        var a = new KbData { Kb = "KB5124010", Status = KbStatus.Ok, Source = "fixture" };
        a.KnownIssues.Add(new KbIssue { Title = "Applications which require AC-3 audio decoding might close unexpectedly", Symptom = "x" });
        await b.EnrichAsync(a, default);
        Assert.Equal(1, a.KnownIssues.Count(i => i.Title!.Contains("AC-3")));
        Assert.Contains(a.KnownIssues, i => i.Title!.StartsWith("Remote Desktop"));
        Assert.NotEmpty(a.ContentHash);
    }

    [Fact]
    public async Task DiskCache_SurvivesRestart()
    {
        using var t = new TempDir();
        var h1 = new FakeHandler().Enqueue(HttpStatusCode.OK, Html);
        var s1 = new ReleaseHealthSource(() => new HttpClient(h1), new RequestGate(1, TimeSpan.Zero), () => new OsInfo(26100, false, "24H2"), null, t.Path);
        await s1.FetchAsync("KB5124010", default);

        var h2 = new FakeHandler();
        var s2 = new ReleaseHealthSource(() => new HttpClient(h2), new RequestGate(1, TimeSpan.Zero), () => new OsInfo(26100, false, "24H2"), null, t.Path);
        var d = await s2.FetchAsync("KB5124010", default);
        Assert.Equal(KbStatus.Ok, d.Status);
        Assert.Equal(0, h2.Calls);
    }

    private sealed class BlockedSource : IKbSource
    {
        public string Name => "support.microsoft.com";
        public Task<KbData> FetchAsync(string kb, CancellationToken ct) =>
            Task.FromResult(new KbData { Kb = kb, Status = KbStatus.Blocked, LastError = "HTTP 403" });
    }
}

public class AutostartTests
{
    private sealed class FakeKey : IRunKey
    {
        public Dictionary<string, string> Values = new();
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }

    [Fact]
    public void Enable_WritesQuotedPath_Disable_RemovesValue()
    {
        var k = new FakeKey();
        var a = new Autostart(k, @"C:\Apps\PatchRadar\PatchRadar.exe");
        a.Apply(true);
        Assert.Equal("\"C:\\Apps\\PatchRadar\\PatchRadar.exe\"", k.Values["PatchRadar"]);
        Assert.True(a.IsEnabled);
        a.Apply(false);
        Assert.Empty(k.Values);
        Assert.False(a.IsEnabled);
    }

    [Fact]
    public void Enable_RepairsStalePath()
    {
        var k = new FakeKey();
        k.Values["PatchRadar"] = "\"C:\\old\\PatchRadar.exe\"";
        new Autostart(k, @"C:\new\PatchRadar.exe").Apply(true);
        Assert.Equal("\"C:\\new\\PatchRadar.exe\"", k.Values["PatchRadar"]);
    }

    [Fact]
    public void Disable_WhenAbsent_IsHarmless() => new Autostart(new FakeKey(), "x").Apply(false);

    [Fact]
    public void RegistryKey_RoundTripsOnRealHkcu()
    {
        // Uses a throwaway value name so a real install's setting is never touched.
        var key = new RegistryRunKey();
        const string name = "PatchRadarTest_DeleteMe";
        try
        {
            key.Set(name, "\"C:\\x.exe\"");
            Assert.Equal("\"C:\\x.exe\"", key.Get(name));
            key.Delete(name);
            Assert.Null(key.Get(name));
        }
        finally { key.Delete(name); }
    }
}

public class ToastTrackerTests
{
    private static UpdateRow Red(string kb = "KB5127648", int daysAgo = 2) => Rows.FromFixture("kb5127648", kb, "t", daysAgo);

    [Fact]
    public void FirstTime_Toasts_ThenNever_EvenAfterRestart()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "state.json");
        var first = new ToastTracker(new StateStore(path)).TakeNew(new[] { Red() }, 30);
        Assert.Single(first);

        // "restart": brand-new tracker and store over the same file
        var again = new ToastTracker(new StateStore(path)).TakeNew(new[] { Red() }, 30);
        Assert.Empty(again);
    }

    [Fact]
    public void ChangedContentHash_ToastsAgain()
    {
        using var t = new TempDir();
        var tracker = new ToastTracker(new StateStore(Path.Combine(t.Path, "state.json")));
        tracker.TakeNew(new[] { Red() }, 30);
        var changed = Red();
        changed.Kb!.ContentHash = "DIFFERENT";
        Assert.Single(tracker.TakeNew(new[] { changed }, 30));
    }

    [Fact]
    public void OldUpdates_AreRecordedSilently()
    {
        using var t = new TempDir();
        var tracker = new ToastTracker(new StateStore(Path.Combine(t.Path, "state.json")));
        Assert.Empty(tracker.TakeNew(new[] { Red(daysAgo: 80) }, 30));
        Assert.Empty(tracker.TakeNew(new[] { Red(daysAgo: 80) }, 30));
    }

    [Fact]
    public void NonRed_IsIgnored()
    {
        using var t = new TempDir();
        var tracker = new ToastTracker(new StateStore(Path.Combine(t.Path, "state.json")));
        Assert.Empty(tracker.TakeNew(new[] { Rows.Grey("KB1", "t"), Rows.FromFixture("kb5044293", "KB5044293", "t") }, 30));
    }

    [Fact]
    public void StateFile_PreservesUnknownKeys()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "state.json");
        File.WriteAllText(path, "{\"futureThing\":{\"KB1\":\"abc\"}}");
        new ToastTracker(new StateStore(path)).TakeNew(new[] { Red() }, 30);
        var text = File.ReadAllText(path);
        Assert.Contains("futureThing", text);
        Assert.Contains("KB5127648", text);
    }

    [Fact]
    public void CorruptStateFile_StartsEmpty()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "state.json");
        File.WriteAllText(path, "{ not json");
        Assert.Single(new ToastTracker(new StateStore(path)).TakeNew(new[] { Red() }, 30));
    }
}

public class SettingsHostTests
{
    [Fact]
    public void Apply_SavesPublishesAndFixesInvalid()
    {
        using var t = new TempDir();
        var store = new SettingsStore(Path.Combine(t.Path, "settings.json"));
        var host = new SettingsHost(store, store.Load());
        AppSettings? seen = null;
        host.Changed += s => seen = s;

        var edited = SettingsHost.Clone(host.Current);
        edited.Updates.MenuItemCount = 500;       // out of range
        edited.Appearance.Theme = ThemeMode.Dark;
        var warnings = host.Apply(edited);

        Assert.Single(warnings);
        Assert.Same(edited, seen);
        Assert.Equal(15, host.Current.Updates.MenuItemCount);
        Assert.Equal(ThemeMode.Dark, store.Load().Appearance.Theme);
    }

    [Fact]
    public void Clone_KeepsUnknownKeys()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(path, "{\"futureThing\":{\"a\":1},\"general\":{\"futureFlag\":true}}");
        var store = new SettingsStore(path);
        var host = new SettingsHost(store, store.Load());
        host.Apply(SettingsHost.Clone(host.Current));
        var text = File.ReadAllText(path);
        Assert.Contains("futureThing", text);
        Assert.Contains("futureFlag", text);
    }
}
