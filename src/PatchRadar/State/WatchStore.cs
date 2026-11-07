using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Scan;

namespace PatchRadar.State;

public sealed class WatchEntry
{
    public DateTime SinceUtc { get; set; }
    /// <summary>Issue keys (see <see cref="KbDiff.KeyOf"/>) that had no fix or workaround when the watch began.</summary>
    public List<string> Open { get; set; } = new();
}

/// <summary>A watched issue that now has a fix, a workaround, or has left the article.</summary>
public sealed record FixAlert(UpdateRow Row, IReadOnlyList<string> Lines);

/// <summary>"Wait for a fix": watch an update's unresolved known issues and say when Microsoft publishes something.</summary>
public sealed class WatchStore(StateStore store, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public static bool HasFix(KbIssue i) =>
        i.Status == IssueStatus.Resolved || i.Resolution.Length > 0 || i.ResolvedInKb is not null || i.Workaround.Length > 0;

    /// <summary>Issues with nothing published yet: the only thing worth waiting on.</summary>
    public static List<KbIssue> OpenIssues(KbData? kb) =>
        kb is { Status: KbStatus.Ok } ? kb.KnownIssues.Where(i => !HasFix(i)).ToList() : new();

    public bool IsWatched(string kb) => store.Load().Watched.ContainsKey(kb);

    public int Count => store.Load().Watched.Count;

    /// <summary>False when there is nothing to wait for (no unresolved issue).</summary>
    public bool Watch(string kb, KbData? data)
    {
        var open = OpenIssues(data).Select(KbDiff.KeyOf).Distinct().ToList();
        if (open.Count == 0) return false;
        store.Update(s => s.Watched[kb] = new WatchEntry { SinceUtc = _now(), Open = open });
        return true;
    }

    public void Unwatch(string kb) => store.Update(s => s.Watched.Remove(kb));

    /// <summary>What has been fixed since the watch began. Pure: does not touch the store.</summary>
    public static (List<string> Lines, List<string> Remaining) Evaluate(WatchEntry entry, KbData kb)
    {
        var lines = new List<string>();
        var remaining = new List<string>();
        if (kb.Status != KbStatus.Ok) return (lines, entry.Open.ToList());
        var byKey = kb.KnownIssues.GroupBy(KbDiff.KeyOf).ToDictionary(g => g.Key, g => g.First());
        foreach (var key in entry.Open)
        {
            if (!byKey.TryGetValue(key, out var issue)) { lines.Add($"{key}: no longer listed in the article"); continue; }
            if (!HasFix(issue)) { remaining.Add(key); continue; }
            var label = string.IsNullOrWhiteSpace(issue.Title) ? key : issue.Title!;
            var what = issue.ResolvedInKb is not null ? $"fix named: {issue.ResolvedInKb}{(issue.PartialFix ? " (partial)" : "")}"
                : issue.Resolution.Length > 0 ? "resolution published"
                : issue.Status == IssueStatus.Resolved ? "marked resolved"
                : "workaround published";
            lines.Add($"{label}: {what}");
        }
        return (lines, remaining);
    }

    /// <summary>Checks every watched update against the latest scan, records progress, and returns what is new.</summary>
    public IReadOnlyList<FixAlert> TakeAlerts(IEnumerable<UpdateRow> rows)
    {
        var watched = store.Load().Watched;
        if (watched.Count == 0) return Array.Empty<FixAlert>();
        var alerts = new List<FixAlert>();
        var done = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.GroupBy(r => r.Update.Kb, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            if (!watched.TryGetValue(row.Update.Kb, out var entry) || row.Kb is null) continue;
            var (lines, remaining) = Evaluate(entry, row.Kb);
            if (lines.Count == 0) continue;
            alerts.Add(new FixAlert(row, lines));
            done[row.Update.Kb] = remaining;
        }
        if (done.Count > 0)
            store.Update(s =>
            {
                foreach (var (kb, remaining) in done)
                    if (remaining.Count == 0) s.Watched.Remove(kb);
                    else if (s.Watched.TryGetValue(kb, out var e)) e.Open = remaining;
            });
        return alerts;
    }
}
