using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Settings;

namespace PatchRadar.Tests;

internal sealed class FakeRenderer(bool available, Func<string, string?> render, int status = 200) : IPageRenderer
{
    public int Calls;
    public List<string> Urls = new();
    public bool IsAvailable => available;
    public Task<RenderedPage?> RenderAsync(string url, CancellationToken ct)
    {
        Calls++;
        Urls.Add(url);
        var html = render(url);
        return Task.FromResult(html is null ? null : new RenderedPage(html, status));
    }
}

public class WebViewSourceTests
{
    private static RequestGate Gate() => new(1, TimeSpan.Zero);

    private static WebViewSource Make(FakeRenderer r, bool enabled = true) => new(r, Gate(), () => enabled);

    [Fact]
    public async Task RendersBlockedPage_AndParsesItLikeSourceA()
    {
        var r = new FakeRenderer(true, _ => Fx.Html("kb5127648"));
        var d = await Make(r).FetchAsync("KB5127648", default);

        Assert.Equal(KbStatus.Ok, d.Status);
        Assert.Contains("WebView2", d.Source);
        Assert.Equal("https://support.microsoft.com/help/5127648", r.Urls.Single());
        Assert.Contains(d.Improvements, i => i.Category == "Authentication" && i.Text.Contains("BlockNtlmv1SSO"));
        Assert.Equal(Severity.Red, SeverityEngine.Default().Evaluate(d).Severity);
    }

    [Fact]
    public async Task Http404_IsNotFound_NotAnArticle()
    {
        // The support site renders a friendly page (with an h1) for unknown KBs.
        var r = new FakeRenderer(true, _ => "<html><body><main><h1>Page not found</h1><p>Sorry.</p></main></body></html>", status: 404);
        var d = await Make(r).FetchAsync("KB1", default);
        Assert.Equal(KbStatus.NotFound, d.Status);
        Assert.Contains("404", d.LastError);
    }

    [Fact]
    public async Task Http404_WithEmptyBody_IsStillNotFound()
    {
        var d = await Make(new FakeRenderer(true, _ => "", status: 404)).FetchAsync("KB1", default);
        Assert.Equal(KbStatus.NotFound, d.Status);
    }

    [Fact]
    public void IsOffByDefault_AndSettingTurnsItOn()
    {
        Assert.False(new AdvancedSettings().EnableWebView2Fallback);
        Assert.False(Make(new FakeRenderer(true, _ => ""), enabled: false).IsEnabled);
        Assert.True(Make(new FakeRenderer(true, _ => "")).IsEnabled);
    }

    [Fact]
    public void RuntimeMissing_SkipsGracefully_AndLogsOnce()
    {
        var log = new CapturingLog();
        var src = new WebViewSource(new FakeRenderer(false, _ => null), Gate(), () => true, log);
        Assert.False(src.IsEnabled);
        Assert.False(src.IsEnabled);
        Assert.Single(log.Lines, l => l.Contains("not installed"));
    }

    [Fact]
    public async Task NothingRendered_IsAFailure_NotAGreen()
    {
        var d = await Make(new FakeRenderer(true, _ => null)).FetchAsync("KB1", default);
        Assert.Equal(KbStatus.Failed, d.Status);
        Assert.Contains("no usable content", d.LastError);
    }

    [Fact]
    public async Task ChallengePageStillShowing_IsNotTreatedAsAnArticle()
    {
        var d = await Make(new FakeRenderer(true, _ => "<html><body><script>challenge()</script>Checking your browser</body></html>")).FetchAsync("KB1", default);
        Assert.NotEqual(KbStatus.Ok, d.Status);
    }

    [Fact]
    public async Task RendererThrows_IsContained()
    {
        var r = new ThrowingRenderer();
        var d = await Make2(r).FetchAsync("KB1", default);
        Assert.Equal(KbStatus.Failed, d.Status);
        Assert.Contains("render failed", d.LastError);
    }

    private static WebViewSource Make2(IPageRenderer r) => new(r, Gate(), () => true);

    private sealed class ThrowingRenderer : IPageRenderer
    {
        public bool IsAvailable => true;
        public Task<RenderedPage?> RenderAsync(string url, CancellationToken ct) => throw new InvalidOperationException("browser crashed");
    }

    // ---- inside the chain ------------------------------------------------------------------

    private sealed class BlockedA : IKbSource
    {
        public string Name => "support.microsoft.com";
        public int Calls;
        public Task<KbData> FetchAsync(string kb, CancellationToken ct) { Calls++; return Task.FromResult(new KbData { Kb = kb, Status = KbStatus.Blocked, LastError = "HTTP 403" }); }
    }

    private static InstalledUpdate Upd(string kb) =>
        new(kb, "t", DateTimeOffset.UtcNow.AddDays(-2), UpdateState.Installed, UpdateCategory.Cumulative);

    [Fact]
    public async Task Chain_ABlocked_WebViewGivesTheFullArticle_AndReleaseHealthIsNotNeeded()
    {
        using var t = new TempDir();
        var a = new BlockedA();
        var rh = new CountingSource();
        var web = Make(new FakeRenderer(true, _ => Fx.Html("kb5127648")));
        var f = new KbFetcher(new IKbSource[] { a, web, rh }, new KbCache(t.Path));

        var kb = (await f.GetAsync(Upd("KB5127648"), new ScanningSettings(), false, default))!;

        Assert.Equal(KbStatus.Ok, kb.Status);
        Assert.Contains("WebView2", kb.Source);
        Assert.NotEmpty(kb.Improvements);                 // full article, not just known issues
        Assert.Equal(0, rh.Calls);
    }

    [Fact]
    public async Task Chain_WebViewDisabled_FallsStraightToReleaseHealth()
    {
        using var t = new TempDir();
        var renderer = new FakeRenderer(true, _ => Fx.Html("kb5127648"));
        var web = Make(renderer, enabled: false);
        var rh = new CountingSource();
        var f = new KbFetcher(new IKbSource[] { new BlockedA(), web, rh }, new KbCache(t.Path));

        await f.GetAsync(Upd("KB1"), new ScanningSettings(), false, default);

        Assert.Equal(0, renderer.Calls);
        Assert.Equal(1, rh.Calls);
    }

    [Fact]
    public async Task Chain_WebViewFails_ReleaseHealthStillRuns()
    {
        using var t = new TempDir();
        var rh = new CountingSource(ok: true);
        var f = new KbFetcher(new IKbSource[] { new BlockedA(), Make(new FakeRenderer(true, _ => null)), rh }, new KbCache(t.Path));
        var kb = (await f.GetAsync(Upd("KB1"), new ScanningSettings(), false, default))!;
        Assert.Equal(KbStatus.Ok, kb.Status);
        Assert.Equal("counting", kb.Source);
    }

    [Fact]
    public async Task Chain_AFine_WebViewNeverRuns()
    {
        using var t = new TempDir();
        var renderer = new FakeRenderer(true, _ => Fx.Html("kb5127648"));
        var good = Fx.Parse("kb5044293");
        good.Kb = "KB1";
        var f = new KbFetcher(new IKbSource[] { new OkSource(good), Make(renderer) }, new KbCache(t.Path));
        await f.GetAsync(Upd("KB1"), new ScanningSettings(), false, default);
        Assert.Equal(0, renderer.Calls);
    }

    [Fact]
    public async Task WebViewArticle_IsEnrichedWithReleaseHealth()
    {
        using var t = new TempDir();
        var enricher = new EnrichingSource();
        var f = new KbFetcher(new IKbSource[] { new BlockedA(), Make(new FakeRenderer(true, _ => Fx.Html("kb5044293"))), enricher }, new KbCache(t.Path),
            enrichers: new IKbEnricher[] { enricher });
        var kb = (await f.GetAsync(Upd("KB5044293"), new ScanningSettings(), false, default))!;
        Assert.Contains(kb.KnownIssues, i => i.Title == "Added by release health");
    }

    private sealed class CountingSource(bool ok = false) : IKbSource
    {
        public int Calls;
        public string Name => "counting";
        public Task<KbData> FetchAsync(string kb, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ok
                ? new KbData { Kb = kb, Status = KbStatus.Ok, Source = "counting", ContentHash = "x" }
                : new KbData { Kb = kb, Status = KbStatus.Failed, LastError = "none" });
        }
    }

    private sealed class OkSource(KbData d) : IKbSource
    {
        public string Name => "ok";
        public Task<KbData> FetchAsync(string kb, CancellationToken ct) => Task.FromResult(d);
    }

    private sealed class EnrichingSource : IKbSource, IKbEnricher
    {
        public string Name => "rh";
        public bool IsEnabled => true;
        public Task<KbData> FetchAsync(string kb, CancellationToken ct) => Task.FromResult(new KbData { Kb = kb, Status = KbStatus.Failed });
        public Task EnrichAsync(KbData data, CancellationToken ct)
        {
            data.KnownIssues.Add(new KbIssue { Title = "Added by release health", Symptom = "x" });
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("https://support.microsoft.com/help/5127648", true)]
    [InlineData("https://learn.microsoft.com/en-us/windows/release-health/", true)]
    [InlineData("about:blank", true)]
    [InlineData("http://support.microsoft.com/help/1", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("https://microsoft.com.evil.example/", false)]
    [InlineData("file:///c:/windows/win.ini", false)]
    public void RendererNavigation_IsLimitedToMicrosoftHttps(string uri, bool allowed) =>
        Assert.Equal(allowed, WebView2Renderer.IsAllowedNavigation(uri));
}
