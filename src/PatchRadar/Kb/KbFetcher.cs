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

    private readonly HttpClient _http;
    private readonly RequestGate _gate;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxAttempts;

    public string Name => "support.microsoft.com";

    public SupportSiteSource(HttpClient http, RequestGate gate, ILog? log = null,
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
                    using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
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
                    return Fail(kb, KbStatus.NotFound, "HTTP 404");

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
    private readonly KbCache _cache;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;

    public KbFetcher(IEnumerable<IKbSource> sources, KbCache cache, ILog? log = null, Func<DateTime>? now = null)
    {
        _sources = sources.ToList();
        _cache = cache;
        _log = log ?? NullLog.Instance;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public KbData? GetCached(string kb) => _cache.Load(kb);

    public async Task<KbData?> GetAsync(InstalledUpdate update, ScanningSettings scanning, bool force, CancellationToken ct)
    {
        var cached = _cache.Load(update.Kb);
        var now = _now();
        if (!KbCache.NeedsRefresh(cached, update.InstalledOn, now, scanning, force)) return cached;

        KbData? last = null;
        foreach (var src in _sources)
        {
            var d = await src.FetchAsync(update.Kb, ct).ConfigureAwait(false);
            last = d;
            if (d.Status == KbStatus.Ok)
            {
                d.ConsecutiveFailures = 0;
                d.NextRetryUtc = null;
                if (cached?.ContentHash == d.ContentHash && cached.Status == KbStatus.Ok) _log.Debug($"{update.Kb}: unchanged");
                _cache.Save(d);
                return d;
            }
            _log.Info($"{update.Kb}: {src.Name} -> {d.Status} ({d.LastError})");
        }

        // Every source failed. Keep previous good content if we have it; record the failure.
        var failures = (cached?.ConsecutiveFailures ?? 0) + 1;
        var keep = cached is { Status: KbStatus.Ok } ? cached : last ?? new KbData { Kb = update.Kb, Status = KbStatus.Failed, Source = "none" };
        keep.ConsecutiveFailures = failures;
        keep.NextRetryUtc = now + KbCache.RetryDelay(failures);
        keep.LastError = last?.LastError ?? "No source available";
        if (keep.Status != KbStatus.Ok) keep.FetchedUtc = now;
        _cache.Save(keep);
        return keep;
    }
}
