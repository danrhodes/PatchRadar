using System.Net;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.Kb;

public interface IKbSource
{
    string Name { get; }
    /// <summary>Fetch and parse one KB. Returns Status Blocked/Failed/NotFound instead of throwing on HTTP problems.</summary>
    Task<KbData> FetchAsync(string kb, CancellationToken ct);
    /// <summary>Sources can be switched off from settings without rebuilding the chain.</summary>
    bool IsEnabled => true;
}

/// <summary>Limits concurrency and enforces a minimum gap between request starts.</summary>
public sealed class RequestGate
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _gap;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private DateTime _nextStart = DateTime.MinValue;

    public RequestGate(int maxConcurrent, TimeSpan minGap, Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        _gap = minGap;
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TimeSpan wait;
            lock (_lock)
            {
                var n = _now();
                var start = n > _nextStart ? n : _nextStart;
                wait = start - n;
                _nextStart = start + _gap;
            }
            if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
            return await work().ConfigureAwait(false);
        }
        finally { _slots.Release(); }
    }
}

/// <summary>Source A: https://support.microsoft.com/help/&lt;KB&gt;</summary>
public sealed class SupportSiteSource : IKbSource
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly Func<HttpClient> _http;
    private readonly RequestGate _gate;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxAttempts;

    public string Name => "support.microsoft.com";

    public SupportSiteSource(HttpClient http, RequestGate gate, ILog? log = null,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null, int maxAttempts = 3)
        : this(() => http, gate, log, now, delay, maxAttempts) { }

    /// <summary>Client is resolved per request so proxy / user-agent changes in Settings apply without a restart.</summary>
    public SupportSiteSource(Func<HttpClient> http, RequestGate gate, ILog? log = null,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null, int maxAttempts = 3)
    {
        _http = http;
        _gate = gate;
        _log = log ?? NullLog.Instance;
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
        _maxAttempts = maxAttempts;
    }

    public static HttpClient CreateClient(NetworkSettings net, HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            var h = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.All };
            if (!string.IsNullOrWhiteSpace(net.Proxy)) { h.Proxy = new WebProxy(net.Proxy); h.UseProxy = true; }
            handler = h;
        }
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1, net.TimeoutSeconds)) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(net.UserAgent) ? DefaultUserAgent : net.UserAgent);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
        c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        return c;
    }

    public async Task<KbData> FetchAsync(string kb, CancellationToken ct)
    {
        var number = kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? kb[2..] : kb;
        var url = $"https://support.microsoft.com/help/{number}";
        string? lastError = null;
        var status = KbStatus.Failed;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (code, body, finalUrl) = await _gate.RunAsync(async () =>
                {
                    using var resp = await _http().GetAsync(url, ct).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return (resp.StatusCode, text, resp.RequestMessage?.RequestUri?.ToString());
                }, ct).ConfigureAwait(false);

                if (code == HttpStatusCode.OK)
                {
                    var data = KbParser.Parse(kb.ToUpperInvariant(), body, Name, finalUrl, _now());
                    if (data.Status == KbStatus.Ok) return data;
                    // 200 but an empty shell / challenge page: treat as blocked, retry is pointless.
                    _log.Warn($"{kb}: {data.LastError}");
                    return data;
                }
                if (code == HttpStatusCode.NotFound)
                    return Fail(kb, KbStatus.NotFound, "Microsoft has no KB article for this update (HTTP 404)");

                lastError = $"HTTP {(int)code}";
                status = code is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? KbStatus.Blocked : KbStatus.Failed;
                var retryable = code is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)code >= 500;
                if (!retryable) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = ex.Message;
                status = KbStatus.Failed;
            }

            if (attempt < _maxAttempts)
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2 s, 4 s, ...
                _log.Info($"{kb}: {lastError}; retrying in {backoff.TotalSeconds:0}s");
                await _delay(backoff, ct).ConfigureAwait(false);
            }
        }
        _log.Warn($"{kb}: fetch failed ({lastError})");
        return Fail(kb, status, lastError ?? "Unknown error");
    }

    private KbData Fail(string kb, KbStatus status, string reason) => new()
    {
        Kb = kb.ToUpperInvariant(), Source = Name, FetchedUtc = _now(), Status = status, LastError = reason,
    };
}

/// <summary>Cache-first KB retrieval across an ordered chain of sources.</summary>
public sealed class KbFetcher
{
    private readonly IReadOnlyList<IKbSource> _sources;
    private readonly IReadOnlyList<IKbEnricher> _enrichers;
    private readonly KbCache _cache;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;

    public KbFetcher(IEnumerable<IKbSource> sources, KbCache cache, ILog? log = null, Func<DateTime>? now = null,
        IEnumerable<IKbEnricher>? enrichers = null)
    {
        _sources = sources.ToList();
        _enrichers = enrichers?.ToList() ?? new List<IKbEnricher>();
        _cache = cache;
        _log = log ?? NullLog.Instance;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public KbData? GetCached(string kb) => _cache.Load(kb);

    /// <summary>
    /// Keep the article's edit history across fetches. A real edit (by content, not by hash) stores the old
    /// reading as <see cref="KbData.Previous"/>; an unchanged article carries the earlier history forward.
    /// </summary>
    private static void CarryHistory(KbData? cached, KbData fresh, DateTime now)
    {
        if (cached is not { Status: KbStatus.Ok }) return;
        if (!KbDiff.Compare(cached, fresh).IsEmpty)
        {
            fresh.Previous = KbSnapshot.Of(cached, cached.FetchedUtc);
            fresh.ChangedUtc = now;
        }
        else
        {
            fresh.Previous = cached.Previous;
            fresh.ChangedUtc = cached.ChangedUtc;
        }
    }

    public async Task<KbData?> GetAsync(InstalledUpdate update, ScanningSettings scanning, bool force, CancellationToken ct)
    {
        var cached = _cache.Load(update.Kb);
        var now = _now();
        if (!KbCache.NeedsRefresh(cached, update.InstalledOn, now, scanning, force)) return cached;

        KbData? first = null;
        var errors = new List<string>();
        foreach (var src in _sources)
        {
            if (!src.IsEnabled) continue;
            var d = await src.FetchAsync(update.Kb, ct).ConfigureAwait(false);
            first ??= d;
            if (d.Status == KbStatus.Ok)
            {
                // A full article (source A or the WebView2 render) is in hand: let enrichers add anything
                // it lacks (release health known issues). Release health itself is not enriched with itself.
                if (src is not IKbEnricher)
                {
                    foreach (var e in _enrichers.Where(e => e.IsEnabled))
                    {
                        try { await e.EnrichAsync(d, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn($"{update.Kb}: enrichment failed: {ex.Message}"); }
                    }
                }
                d.ConsecutiveFailures = 0;
                d.NextRetryUtc = null;
                CarryHistory(cached, d, now);
                if (cached?.ContentHash == d.ContentHash && cached.Status == KbStatus.Ok) _log.Debug($"{update.Kb}: unchanged");
                _cache.Save(d);
                return d;
            }
            errors.Add($"{src.Name}: {d.LastError ?? d.Status.ToString()}");
            _log.Info($"{update.Kb}: {src.Name} -> {d.Status} ({d.LastError})");
        }

        // Every source failed. Keep previous good content if we have it; record the failure.
        var failures = (cached?.ConsecutiveFailures ?? 0) + 1;
        var keep = cached is { Status: KbStatus.Ok } ? cached : first ?? new KbData { Kb = update.Kb, Status = KbStatus.Failed, Source = "none" };
        keep.ConsecutiveFailures = failures;
        // 404 on the support site means Microsoft has no article for this update: do not hammer it, look again in a week.
        var noArticle = first?.Status == KbStatus.NotFound;
        keep.NextRetryUtc = now + (noArticle ? TimeSpan.FromDays(7) : KbCache.RetryDelay(failures));
        keep.LastError = errors.Count > 0 ? string.Join("; ", errors) : "No source available";
        if (keep.Status != KbStatus.Ok) keep.FetchedUtc = now;
        _cache.Save(keep);
        return keep;
    }
}
