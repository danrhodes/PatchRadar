using PatchRadar.Core;

namespace PatchRadar.Scan;

/// <summary>What to do about a pending update, in one sentence. Null when there is nothing worth saying.</summary>
public static class DeferAdvice
{
    public static string? For(UpdateRow row)
    {
        if (!row.IsPending) return null;
        if (!row.Update.HasKb)
            return row.DriverNote is { } n && n.StartsWith("Older", StringComparison.Ordinal) ? "Check before installing: " + n : null;
        if (row.Kb is not { Status: KbStatus.Ok })
            return "Risk unknown: the article has not been read yet. Run Scan now before installing.";

        var active = row.Result.ActiveIssues;
        var unresolved = active.Where(i => i.Workaround.Length == 0 && i.Resolution.Length == 0 && i.ResolvedInKb is null).ToList();
        switch (row.Severity)
        {
            case Severity.Red when unresolved.Count > 0:
                return $"Consider deferring: {unresolved.Count} active known issue{(unresolved.Count == 1 ? "" : "s")} with no fix or workaround published yet.";
            case Severity.Red when active.Count > 0:
                return "Risky, but there is a workaround or fix for each known issue. Apply it before or straight after installing, and try it on one machine first.";
            case Severity.Red:
                return "Review the behaviour change before installing, and try it on one machine first.";
            case Severity.Orange:
                return "Read the known issues first; none is flagged as critical for your keywords.";
            case Severity.Green:
                return "No known problems published for this update.";
            default:
                return null;
        }
    }
}
