using System.Net;
using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Settings;

namespace PatchRadar.Tests;

public class KbCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ScanningSettings S = new();

    private static KbData Cached(TimeSpan age, int failures = 0, DateTime? next = null) =>
        new() { Kb = "KB1", Status = KbStatus.Ok, FetchedUtc = Now - age, ConsecutiveFailures = failures, NextRetryUtc = next };

    private static DateTimeOffset Installed(int daysAgo) => new(Now.AddDays(-daysAgo), TimeSpan.Zero);

    [Fact] public void NoCache_NeedsRefresh() => Assert.True(KbCache.NeedsRefresh(null, Installed(1), Now, S, false));

    [Theory]
    [InlineData(5, 11, false)]  // recent, inside 12 h TTL
    [InlineData(5, 13, true)]   // recent, past 12 h TTL
    [InlineData(60, 24 * 6, false)]  // older, inside 7 d TTL
    [InlineData(60, 24 * 8, true)]   // older, past 7 d TTL
    public void Ttl(int installedDaysAgo, int ageHours, bool expected) =>
        Assert.Equal(expected, KbCache.NeedsRefresh(Cached(TimeSpan.FromHours(ageHours)), Installed(installedDaysAgo), Now, S, false));

    [Fact]
    public void Force_OnlyAppliesInsideRecentWindow()
    {
        var fresh = Cached(TimeSpan.FromMinutes(5));
        Assert.True(KbCache.NeedsRefresh(fresh, Installed(5), Now, S, force: true));
        Assert.False(KbCache.NeedsRefresh(fresh, Installed(60), Now, S, force: true));
    }

    [Fact]
    public void FailedFetch_WaitsForNextRetry()
    {
        var c = Cached(TimeSpan.FromDays(30), failures: 1, next: Now.AddMinutes(30));
        Assert.False(KbCache.NeedsRefresh(c, Installed(5), Now, S, false));
        Assert.True(KbCache.NeedsRefresh(c, Installed(5), Now.AddMinutes(31), S, false));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 6)] [InlineData(3, 24)] [InlineData(9, 24)]
    public void BackoffSchedule(int failures, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), KbCache.RetryDelay(failures));

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        using var t = new TempDir();
        var cache = new KbCache(t.Path);
        var d = Fx.Parse("kb5127648");
        cache.Save(d);
        var back = cache.Load("KB5127648")!;
        Assert.Equal(d.ContentHash, back.ContentHash);
        Assert.Equal(d.KnownIssues.Count, back.KnownIssues.Count);
        Assert.Single(cache.LoadAll(), k => k.Kb == "KB5127648");
    }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public int Calls { get; private set; }
    public FakeHandler Enqueue(HttpStatusCode code, string body = "") { _responses.Enqueue(() => new HttpResponseMessage(code) { Content = new StringContent(body) }); return this; }
    public FakeHandler Throw() { _responses.Enqueue(() => throw new HttpRequestException("boom")); return this; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        var next = _responses.Count > 0 ? _responses.Dequeue() : () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var r = next();
        r.RequestMessage = request;
        return Task.FromResult(r);
    }
}

public class SupportSourceTests
{
    private static (SupportSiteSource src, List<TimeSpan> delays) Make(FakeHandler h)
    {
        var delays = new List<TimeSpan>();
        Task Delay(TimeSpan d, CancellationToken _) { delays.Add(d); return Task.CompletedTask; }
        var client = SupportSiteSource.CreateClient(new NetworkSettings(), h);
        var gate = new RequestGate(2, TimeSpan.Zero, delay: Delay);
        return (new SupportSiteSource(client, gate, delay: Delay), delays);
    }

    [Fact]
    public async Task Success_ParsesFixture()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, Fx.Html("kb5044281"));
        var (src, _) = Make(h);
        var d = await src.FetchAsync("KB5044281", default);
        Assert.Equal(KbStatus.Ok, d.Status);
        Assert.Single(d.KnownIssues);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Forbidden_RetriesWithExponentialBackoff_ThenBlocked()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.Forbidden).Enqueue(HttpStatusCode.Forbidden).Enqueue(HttpStatusCode.Forbidden);
        var (src, delays) = Make(h);
        var d = await src.FetchAsync("KB1", default);
        Assert.Equal(KbStatus.Blocked, d.Status);
        Assert.Equal(3, h.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task ServerError_ThenSuccess()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.ServiceUnavailable).Enqueue(HttpStatusCode.OK, Fx.Html("kb5044293"));
        var (src, _) = Make(h);
        Assert.Equal(KbStatus.Ok, (await src.FetchAsync("KB5044293", default)).Status);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task NotFound_DoesNotRetry()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound);
        var (src, _) = Make(h);
        Assert.Equal(KbStatus.NotFound, (await src.FetchAsync("KB1", default)).Status);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task ChallengePage_IsBlockedWithoutRetry()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, "<html><body><script>challenge()</script></body></html>");
        var (src, _) = Make(h);
        Assert.Equal(KbStatus.Blocked, (await src.FetchAsync("KB1", default)).Status);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Gate_EnforcesMinimumGap()
    {
        var waits = new List<TimeSpan>();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gate = new RequestGate(2, TimeSpan.FromMilliseconds(500), () => t, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        for (var i = 0; i < 3; i++) await gate.RunAsync(() => Task.FromResult(0), default);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }, waits);
    }
}

public class KbFetcherTests
{
    private sealed class FakeSource(params KbData[] results) : IKbSource
    {
        private readonly Queue<KbData> _q = new(results);
        public int Calls;
        public string Name => "fake";
        public Task<KbData> FetchAsync(string kb, CancellationToken ct) { Calls++; return Task.FromResult(_q.Dequeue()); }
    }

    private static KbData Fresh(string fixture) { var d = Fx.Parse(fixture); d.Kb = "KB1"; d.FetchedUtc = DateTime.UtcNow; return d; }

    private static InstalledUpdate Upd() =>
        new("KB1", "t", DateTimeOffset.UtcNow.AddDays(-2), UpdateState.Installed, UpdateCategory.Cumulative);

    [Fact]
    public async Task InsideTtl_DoesNotRefetch()
    {
        using var t = new TempDir();
        var src = new FakeSource(Fresh("kb5044293"), Fresh("kb5044293"));
        var f = new KbFetcher(new[] { src }, new KbCache(t.Path));
        await f.GetAsync(Upd(), new ScanningSettings(), false, default);
        await f.GetAsync(Upd(), new ScanningSettings(), false, default);
        Assert.Equal(1, src.Calls);
    }

    [Fact]
    public async Task Failure_KeepsOldContent_AndSchedulesRetry()
    {
        using var t = new TempDir();
        var good = Fresh("kb5044281");
        var bad = new KbData { Kb = "KB1", Status = KbStatus.Blocked, LastError = "HTTP 403" };
        var src = new FakeSource(good, bad);
        var f = new KbFetcher(new[] { src }, new KbCache(t.Path));
        await f.GetAsync(Upd(), new ScanningSettings(), false, default);
        var r = (await f.GetAsync(Upd(), new ScanningSettings(), force: true, default))!;
        Assert.Equal(KbStatus.Ok, r.Status);          // old content still usable
        Assert.Single(r.KnownIssues);
        Assert.Equal(1, r.ConsecutiveFailures);
        Assert.NotNull(r.NextRetryUtc);
    }

    [Fact]
    public async Task AllSourcesFail_StoresGreyDataWithReason()
    {
        using var t = new TempDir();
        var src = new FakeSource(new KbData { Kb = "KB1", Status = KbStatus.Blocked, LastError = "HTTP 403" });
        var f = new KbFetcher(new[] { src }, new KbCache(t.Path));
        var r = (await f.GetAsync(Upd(), new ScanningSettings(), false, default))!;
        Assert.Equal(KbStatus.Blocked, r.Status);
        Assert.Equal(Severity.Grey, SeverityEngine.Default().Evaluate(r).Severity);
        Assert.True(r.NextRetryUtc > DateTime.UtcNow);
    }
}
