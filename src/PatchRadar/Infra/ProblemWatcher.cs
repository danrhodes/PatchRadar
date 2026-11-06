using System.IO;

namespace PatchRadar.Infra;

/// <summary>
/// Looks through the log for new errors with a stack trace and tells the user about one at a time.
/// It only raises a notice; reporting happens when the user opens it, reads the text and clicks through.
/// </summary>
public sealed class ProblemWatcher(string logDir, ProblemStore store, Func<bool> enabled, Action<string, string, Action> toast,
    Action<ProblemReport> openReport, string version, string os, ILog? log = null)
{
    private readonly ILog _log = log ?? NullLog.Instance;
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(7);

    /// <summary>
    /// For a manual scan from the Advanced menu: every distinct problem in the log (one per hash, newest first), including ones
    /// offered or dismissed before, whether or not the automatic check is switched on.
    /// </summary>
    public IReadOnlyList<ProblemGroup> Groups(DateTime nowUtc)
    {
        try
        {
            return LogScanner.Find(logDir, nowUtc.ToLocalTime() - LookBack)
                .GroupBy(e => ProblemHash.Compute(e.Text))
                .Select(g =>
                {
                    var newest = g.OrderByDescending(x => x.Time).First();
                    return new ProblemGroup(ProblemIssue.Build(newest, ProblemScrubber.ScrubCurrent, version, os), g.Count(), newest.Time, store.Get(g.Key)?.Status);
                })
                .OrderByDescending(g => g.Last)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not scan the log for problems: " + ex.Message);
            return Array.Empty<ProblemGroup>();
        }
    }

    /// <summary>Returns the report that was offered, or null when there was nothing new (or the feature is off).</summary>
    public ProblemReport? CheckNow(DateTime nowUtc)
    {
        if (!enabled()) return null;
        try
        {
            var entries = LogScanner.Find(logDir, nowUtc.ToLocalTime() - LookBack);
            // Newest first; one entry per hash. Only the newest is raised now; the rest wait their turn, and the notice says so.
            var due = entries.OrderByDescending(x => x.Time)
                .Select(e => (Entry: e, Hash: ProblemHash.Compute(e.Text)))
                .DistinctBy(x => x.Hash)
                .Where(x => store.ShouldOffer(x.Hash, nowUtc))
                .ToList();
            if (due.Count > 0)
            {
                var (e, hash) = due[0];
                var report = ProblemIssue.Build(e, ProblemScrubber.ScrubCurrent, version, os);
                store.Set(hash, ProblemStatus.Offered);
                _log.Info($"Problem {hash} found in the log; offering to report it ({due.Count - 1} more waiting)");
                var more = due.Count > 1 ? $" {due.Count - 1} more {(due.Count == 2 ? "is" : "are")} waiting." : "";
                toast("PatchRadar logged a problem", "Click to see what happened and report it on GitHub if you want to." + more, () => openReport(report));
                return report;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not scan the log for problems: " + ex.Message);
        }
        return null;
    }
}
