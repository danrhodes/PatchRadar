using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Settings;
using PatchRadar.Updates;

namespace PatchRadar.Scan;

public sealed record UpdateRow(InstalledUpdate Update, KbData? Kb, SeverityResult Result)
{
    public Severity Severity => Result.Severity;
}

public sealed record ScanResult(IReadOnlyList<UpdateRow> Rows, DateTime ScannedAt, int Fetched, int Failed)
{
    public static readonly ScanResult Empty = new(Array.Empty<UpdateRow>(), DateTime.MinValue, 0, 0);

    public Severity Worst(int take = int.MaxValue)
    {
        var sev = Rows.Take(take).Select(r => r.Severity).ToList();
        if (sev.Count == 0) return Severity.Grey;
        if (sev.Contains(Severity.Red)) return Severity.Red;
        if (sev.Contains(Severity.Orange)) return Severity.Orange;
        if (sev.Contains(Severity.Grey)) return Severity.Grey;
        return Severity.Green;
    }
}

public sealed class ScanService
{
    private readonly IUpdateHistoryProvider _history;
    private readonly KbFetcher _fetcher;
    private readonly Func<AppSettings> _settings;
    private readonly ILog _log;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public ScanService(IUpdateHistoryProvider history, KbFetcher fetcher, Func<AppSettings> settings, ILog? log = null)
    {
        _history = history;
        _fetcher = fetcher;
        _settings = settings;
        _log = log ?? NullLog.Instance;
    }

    public bool IsScanning => _scanLock.CurrentCount == 0;

    /// <summary>Full list (history window or all time) with severity from whatever is cached. No network.</summary>
    public async Task<IReadOnlyList<UpdateRow>> ListCachedAsync(bool allTime, CancellationToken ct)
    {
        var s = _settings();
        var all = await _history.GetAsync(ct).ConfigureAwait(false);
        var filtered = UpdateMerger.Filter(all, s.Updates, DateTimeOffset.UtcNow, allTime);
        var engine = new SeverityEngine(s.ToSeverityOptions());
        return filtered.Select(u => { var kb = _fetcher.GetCached(u.Kb); return new UpdateRow(u, kb, engine.Evaluate(kb)); }).ToList();
    }

    /// <summary>Force-refresh one update's KB data (Detail window Refresh/Retry).</summary>
    public async Task<UpdateRow> RefreshOneAsync(InstalledUpdate update, CancellationToken ct)
    {
        var s = _settings();
        var kb = await _fetcher.GetAsync(update, s.Scanning, force: true, ct).ConfigureAwait(false);
        // force only bypasses TTL inside the recent window; honour an explicit user request for older ones too
        var engine = new SeverityEngine(s.ToSeverityOptions());
        return new UpdateRow(update, kb, engine.Evaluate(kb));
    }

    /// <summary>
    /// Collect updates, fetch KB data for the newest <c>menuItemCount</c> (plus any in the recent window,
    /// capped), then compute severity. Reports (done, total).
    /// </summary>
    public async Task<ScanResult> ScanAsync(bool force, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _settings();
            var all = await _history.GetAsync(ct).ConfigureAwait(false);
            var updates = UpdateMerger.Filter(all, s.Updates, DateTimeOffset.UtcNow);
            _log.Info($"Scan: {all.Count} updates in history, {updates.Count} after filters (force={force})");

            // Menu-visible updates are always fetched; older ones in the history window are fetched
            // lazily by the cache TTL on later scans to keep request volume low.
            var targets = updates.Take(Math.Max(s.Updates.MenuItemCount, 1)).ToList();
            var done = 0;
            var fetched = 0;
            var failed = 0;
            progress?.Report((0, targets.Count));

            var results = new Dictionary<string, KbData?>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var before = _fetcher.GetCached(u.Kb);
                    var kb = await _fetcher.GetAsync(u, s.Scanning, force, ct).ConfigureAwait(false);
                    results[u.Kb] = kb;
                    if (!ReferenceEquals(before, kb) && kb?.Status == KbStatus.Ok) fetched++;
                    if (kb?.Status != KbStatus.Ok) failed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Error($"Fetch {u.Kb} failed", ex);
                    failed++;
                }
                progress?.Report((++done, targets.Count));
            }

            var engine = new SeverityEngine(s.ToSeverityOptions());
            var rows = updates.Select(u =>
            {
                var kb = results.TryGetValue(u.Kb, out var k) ? k : _fetcher.GetCached(u.Kb);
                return new UpdateRow(u, kb, engine.Evaluate(kb));
            }).ToList();
            return new ScanResult(rows, DateTime.Now, fetched, failed);
        }
        finally { _scanLock.Release(); }
    }
}
