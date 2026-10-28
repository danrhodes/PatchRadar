using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Updates;

namespace PatchRadar.Scan;

/// <summary>An update that Microsoft names as the fix for an issue, and whether this PC has it.</summary>
public sealed record FixInfo(string Kb, bool Installed, bool OutOfBand, bool Partial, string IssueTitle, bool IssueResolved);

public sealed record UpdateRow(InstalledUpdate Update, KbData? Kb, SeverityResult Result)
{
    public Severity Severity => Result.Severity;
    public ReviewStatus Review { get; init; }
    public DateTime? ReviewedUtc { get; init; }

    /// <summary>What changed in the article since the user reviewed it (null unless it changed).</summary>
    public KbDiffResult? ChangeSinceReview { get; init; }

    /// <summary>Every fix Microsoft names for this update's issues (active or already resolved), with install state.</summary>
    public IReadOnlyList<FixInfo> Fixes { get; init; } = Array.Empty<FixInfo>();

    /// <summary>A newer cumulative or security update is installed, which usually already contains earlier fixes.</summary>
    public bool NewerCumulativeInstalled { get; init; }

    /// <summary>How many of the user's critical keywords this update matched (used to rank).</summary>
    public int KeywordHits => Result.MatchedKeywords.Count;

    public IReadOnlyList<FixInfo> FixesNotInstalled => Fixes.Where(f => !f.Installed).ToList();

    /// <summary>A fix is published but not installed here, and no newer cumulative update probably covers it.</summary>
    public bool FixAvailableNotInstalled => FixesNotInstalled.Count > 0;

    /// <summary>The cumulative update whose article says it included this servicing stack update (null if unknown).</summary>
    public string? SsuParentKb { get; init; }

    /// <summary>A servicing stack update, by its title or because another article names it as the SSU it shipped with.</summary>
    public bool IsServicingStack => Update.Category == UpdateCategory.ServicingStack || SsuParentKb is not null;

    /// <summary>Offered by Windows Update but not installed yet.</summary>
    public bool IsPending => Update.State == UpdateState.Pending;

    /// <summary>The user chose to stop worrying about this update: still listed, but no icon colour or toast.</summary>
    public bool Ignored { get; init; }

    /// <summary>How a pending driver compares with the version already installed (null for non-drivers).</summary>
    public string? DriverNote { get; init; }

    /// <summary>Plain-English reason a failed install failed, when Windows gave a code.</summary>
    public string? FailureText => Update.State == UpdateState.Failed && Update.HResult != 0 ? WuErrors.Describe(Update.HResult) : null;

    /// <summary>One-line advice on whether to install a pending update now.</summary>
    public string? Advice => DeferAdvice.For(this);
}

public sealed record ScanResult(IReadOnlyList<UpdateRow> Rows, DateTime ScannedAt, int Fetched, int Failed)
{
    public static readonly ScanResult Empty = new(Array.Empty<UpdateRow>(), DateTime.MinValue, 0, 0);

    /// <summary>Updates Windows has found but not installed yet, with their KB findings.</summary>
    public IReadOnlyList<UpdateRow> Pending { get; init; } = Array.Empty<UpdateRow>();

    /// <summary>Windows has finished installing something that needs a restart to take effect.</summary>
    public bool RestartPending { get; init; }

    public Severity Worst(int take = int.MaxValue)
    {
        var sev = Rows.Where(r => !r.Ignored).Take(take).Select(r => r.Severity).ToList();
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
    private readonly ReviewStore? _reviews;
    private volatile HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<InstalledUpdate> _installedList = Array.Empty<InstalledUpdate>();
    private volatile IReadOnlyList<InstalledUpdate> _pendingList = Array.Empty<InstalledUpdate>();

    /// <summary>The installed updates from the last history read (for the uninstall planner and the digest).</summary>
    public IReadOnlyList<InstalledUpdate> InstalledUpdates => _installedList;

    /// <summary>True when this KB is installed on this machine (from the last history read).</summary>
    public bool IsInstalled(string kb) => _installed.Contains(kb);

    private void Remember(IEnumerable<InstalledUpdate> all)
    {
        var installed = all.Where(u => u.State == UpdateState.Installed).ToList();
        _installedList = installed;
        _installed = new HashSet<string>(installed.Select(u => u.Kb), StringComparer.OrdinalIgnoreCase);
    }

    private readonly IPendingUpdateProvider? _pending;
    private readonly IgnoreStore? _ignores;
    private readonly IRestartDetector? _restart;

    public ScanService(IUpdateHistoryProvider history, KbFetcher fetcher, Func<AppSettings> settings, ILog? log = null, ReviewStore? reviews = null,
        IPendingUpdateProvider? pending = null, IgnoreStore? ignores = null, IRestartDetector? restart = null)
    {
        _pending = pending;
        _ignores = ignores;
        _restart = restart;
        _reviews = reviews;
        _history = history;
        _fetcher = fetcher;
        _settings = settings;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>Upper bound on pending articles fetched in one scan (a machine that was off for months can have dozens).</summary>
    private const int MaxPendingFetched = 20;

    public bool IsScanning => _scanLock.CurrentCount == 0;

    /// <summary>Full list (history window or all time) with severity from whatever is cached. No network.</summary>
    public async Task<IReadOnlyList<UpdateRow>> ListCachedAsync(bool allTime, CancellationToken ct)
    {
        var s = _settings();
        var all = await _history.GetAsync(ct).ConfigureAwait(false);
        Remember(all);
        var filtered = UpdateMerger.Filter(all, s.Updates, DateTimeOffset.UtcNow, allTime);
        var engine = new SeverityEngine(s.ToSeverityOptions());
        var snap = Snapshot();
        return filtered.Select(u => MakeRow(u, _fetcher.GetCached(u.Kb), engine, snap)).ToList();
    }

    /// <summary>Pending updates (offered, not installed) after filters. Empty when the feature is off or Windows Update cannot be read.</summary>
    private async Task<IReadOnlyList<InstalledUpdate>> GetPendingAsync(bool online, CancellationToken ct)
    {
        var s = _settings();
        if (_pending is null || !s.Updates.ShowPending) return Array.Empty<InstalledUpdate>();
        try
        {
            IReadOnlyList<InstalledUpdate> found;
            try { found = await _pending.GetAsync(online, ct).ConfigureAwait(false); }
            catch (Exception ex) when (online && ex is not OperationCanceledException)
            {
                // Offline or Windows Update unreachable: fall back to what Windows last found.
                _log.Warn($"Online pending search failed ({ex.Message}); using the local store");
                found = await _pending.GetAsync(false, ct).ConfigureAwait(false);
            }
            var filtered = UpdateMerger.FilterPending(found, s.Updates, IsInstalled);
            _pendingList = filtered;
            return filtered;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"Pending update search failed: {ex.Message}");
            return Array.Empty<InstalledUpdate>();
        }
    }

    /// <summary>Pending updates with severity from whatever is cached. No KB network calls.</summary>
    public async Task<IReadOnlyList<UpdateRow>> ListPendingCachedAsync(CancellationToken ct)
    {
        var s = _settings();
        var pending = await GetPendingAsync(online: false, ct).ConfigureAwait(false);
        var engine = new SeverityEngine(s.ToSeverityOptions());
        var snap = Snapshot();
        return pending.Select(u => MakeRow(u, _fetcher.GetCached(u.Kb), engine, snap)).ToList();
    }

    /// <summary>Force-refresh one update's KB data (Detail window Refresh/Retry).</summary>
    public async Task<UpdateRow> RefreshOneAsync(InstalledUpdate update, CancellationToken ct)
    {
        var s = _settings();
        var kb = await _fetcher.GetAsync(update, s.Scanning, force: true, ct).ConfigureAwait(false);
        // force only bypasses TTL inside the recent window; honour an explicit user request for older ones too
        var engine = new SeverityEngine(s.ToSeverityOptions());
        return MakeRow(update, kb, engine, Snapshot());
    }

    private ReviewSnapshot Snapshot() => _reviews?.Snapshot() ?? ReviewSnapshot.Empty;

    private UpdateRow MakeRow(InstalledUpdate u, KbData? kb, SeverityEngine engine, ReviewSnapshot reviews)
    {
        if (!u.HasKb)
            return new UpdateRow(u, null, new SeverityResult { Severity = Severity.Grey, Summary = "No KB article is published for this update, so there are no known issues to read." })
            {
                Ignored = _ignores?.IsIgnored(u.Kb) == true,
                DriverNote = u.State == UpdateState.Pending ? DriverInfo.Compare(u, _installedList) : null,
            };

        var ok = kb is { Status: KbStatus.Ok };
        var status = reviews.StatusOf(u.Kb, ok ? kb!.ContentHash : null);
        KbDiffResult? diff = null;
        if (status == ReviewStatus.ChangedSinceReviewed && ok && _reviews?.LoadSnapshot(u.Kb) is { } baseline)
        {
            // Compare the text, not the hash: a parser upgrade changes hashes without Microsoft editing anything.
            diff = KbDiff.Compare(baseline, kb!);
            if (diff.IsEmpty) { status = ReviewStatus.Reviewed; diff = null; }
        }

        var fixes = new List<FixInfo>();
        if (ok)
            foreach (var i in kb!.KnownIssues.Where(i => i.ResolvedInKb is not null))
                fixes.Add(new FixInfo(i.ResolvedInKb!, IsInstalled(i.ResolvedInKb!), i.OutOfBandFix, i.PartialFix,
                    string.IsNullOrWhiteSpace(i.Title) ? i.Symptom : i.Title!, i.Status == IssueStatus.Resolved));

        var newer = u.State != UpdateState.Pending && _installedList.Any(x => x.Kb != u.Kb && x.InstalledOn > u.InstalledOn &&
                                            x.Category is UpdateCategory.Cumulative or UpdateCategory.Security);
        return new UpdateRow(u, kb, engine.Evaluate(kb))
        {
            SsuParentKb = FindSsuParent(u),
            Review = status,
            ReviewedUtc = reviews.ReviewedUtc(u.Kb),
            ChangeSinceReview = diff,
            Fixes = fixes,
            NewerCumulativeInstalled = newer,
            Ignored = _ignores?.IsIgnored(u.Kb) == true,
            DriverNote = u.State == UpdateState.Pending ? DriverInfo.Compare(u, _installedList) : null,
        };
    }

    /// <summary>
    /// Which update an SSU shipped with: look at the cached articles of cumulative/security updates installed or pending
    /// around the same time (an SSU arrives with its cumulative update, so only nearby ones are read).
    /// </summary>
    private string? FindSsuParent(InstalledUpdate u)
    {
        // Not Security/Cumulative: an SSU is often titled "Security Update (KB…)", so those must still be checked.
        if (u.Category is UpdateCategory.Definition or UpdateCategory.Driver or UpdateCategory.Office) return null;
        var near = _installedList.Concat(_pendingList)
            .Where(x => x.Kb != u.Kb && x.HasKb && (x.Category is UpdateCategory.Cumulative or UpdateCategory.Security or UpdateCategory.Other) &&
                        Math.Abs((x.InstalledOn - u.InstalledOn).TotalDays) <= 10)
            .Select(x => x.Kb).Distinct(StringComparer.OrdinalIgnoreCase).Take(12);
        return ServicingStackLinker.ParentOf(u.Kb, near.Select(k => (k, _fetcher.GetCached(k) is { Status: KbStatus.Ok } d ? d.RawText : null)));
    }

    /// <summary>Ignore or stop ignoring one update and return the updated row.</summary>
    public UpdateRow SetIgnored(UpdateRow row, bool ignored)
    {
        _ignores?.Set(row.Update.Kb, ignored);
        return row with { Ignored = ignored };
    }

    /// <summary>True when Windows needs a restart to finish installing updates.</summary>
    public bool IsRestartPending()
    {
        try { return _restart?.IsRestartPending() == true; }
        catch (Exception ex) { _log.Warn("Restart check failed: " + ex.Message); return false; }
    }

    /// <summary>Flip one row's reviewed flag and return the updated row.</summary>
    public UpdateRow SetReviewed(UpdateRow row, bool reviewed)
    {
        if (_reviews is null) return row;
        if (reviewed && row.Kb is { Status: KbStatus.Ok } kb) _reviews.Mark(row.Update.Kb, kb);
        else if (!reviewed) _reviews.Unmark(row.Update.Kb);
        return MakeRow(row.Update, row.Kb, new SeverityEngine(_settings().ToSeverityOptions()), _reviews.Snapshot());
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
            Remember(all);
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

            // Pending updates: read the article for each so the user can prepare before installing.
            var pending = await GetPendingAsync(online: true, ct).ConfigureAwait(false);
            _log.Info($"Scan: {pending.Count} pending updates");
            foreach (var u in pending.Where(p => p.HasKb).Take(MaxPendingFetched))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var kb = await _fetcher.GetAsync(u, s.Scanning, force, ct).ConfigureAwait(false);
                    results[u.Kb] = kb;
                    if (kb?.Status != KbStatus.Ok) failed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Error($"Fetch pending {u.Kb} failed", ex);
                    failed++;
                }
            }

            var engine = new SeverityEngine(s.ToSeverityOptions());
            var snap = Snapshot();
            UpdateRow Row(InstalledUpdate u)
            {
                var kb = results.TryGetValue(u.Kb, out var k) ? k : _fetcher.GetCached(u.Kb);
                return MakeRow(u, kb, engine, snap);
            }
            return new ScanResult(updates.Select(Row).ToList(), DateTime.Now, fetched, failed)
            {
                Pending = pending.Select(Row).ToList(),
                RestartPending = IsRestartPending(),
            };
        }
        finally { _scanLock.Release(); }
    }
}
