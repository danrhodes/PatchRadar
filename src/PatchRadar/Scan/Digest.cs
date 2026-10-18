using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.State;

namespace PatchRadar.Scan;

public enum DigestKind
{
    /// <summary>An update was installed after the last look.</summary>
    NewUpdate,
    /// <summary>Microsoft edited the article (known issues, workarounds, improvements).</summary>
    ArticleChanged,
    /// <summary>The edit made the update riskier (for example Green to Red).</summary>
    BecameRisky,
    /// <summary>A fix is now named for an issue that had none.</summary>
    FixNowNamed,
    /// <summary>The user had reviewed it, and it has changed since.</summary>
    ReviewedChanged,
}

public sealed record DigestItem(DigestKind Kind, UpdateRow Row, string Text, DateTime Utc, KbDiffResult? Diff = null);

public static class DigestBuilder
{
    private static int Rank(Severity s) => s switch { Severity.Red => 3, Severity.Orange => 2, Severity.Green => 1, _ => 0 };

    /// <summary>Everything that changed after <paramref name="sinceUtc"/>, newest first.</summary>
    public static IReadOnlyList<DigestItem> Build(IEnumerable<UpdateRow> rows, DateTime sinceUtc, SeverityEngine engine)
    {
        var items = new List<DigestItem>();
        foreach (var r in rows)
        {
            var installedUtc = r.Update.InstalledOn.UtcDateTime;
            if (installedUtc > sinceUtc)
                items.Add(new DigestItem(DigestKind.NewUpdate, r, $"Installed {r.Update.InstalledOn.LocalDateTime:d MMM HH:mm}. Risk: {r.Severity}.", installedUtc));

            var kb = r.Kb;
            if (kb is not { Status: KbStatus.Ok, Previous: { } before, ChangedUtc: { } changedUtc } || changedUtc <= sinceUtc) continue;

            var diff = KbDiff.Compare(before, kb);
            if (diff.IsEmpty) continue;
            items.Add(new DigestItem(DigestKind.ArticleChanged, r, $"Microsoft edited the article: {diff.Summary}.", changedUtc, diff));

            var was = engine.Evaluate(before.AsKbData(kb.Kb)).Severity;
            if (Rank(r.Severity) > Rank(was))
                items.Add(new DigestItem(DigestKind.BecameRisky, r, $"Risk went from {was} to {r.Severity}.", changedUtc, diff));

            foreach (var c in diff.Changes.Where(c => c.Fields is not null))
                foreach (var f in c.Fields!.Where(f => f.Field == "Fix" && f.After.Length > 0 && f.Before.Length == 0))
                    items.Add(new DigestItem(DigestKind.FixNowNamed, r, $"{c.Title}: {f.After}.", changedUtc, diff));

            if (r.Review == ReviewStatus.ChangedSinceReviewed)
                items.Add(new DigestItem(DigestKind.ReviewedChanged, r, "You had reviewed this update. It has changed since.", changedUtc, diff));
        }
        return items.OrderByDescending(i => i.Utc).ThenBy(i => i.Kind).ToList();
    }

    public static string Label(DigestKind k) => k switch
    {
        DigestKind.NewUpdate => "New update",
        DigestKind.ArticleChanged => "Article changed",
        DigestKind.BecameRisky => "Became riskier",
        DigestKind.FixNowNamed => "Fix now named",
        _ => "Reviewed, then changed",
    };
}

/// <summary>When the user last opened the digest. Defaults to a week ago the first time.</summary>
public sealed class DigestStore(StateStore store, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public DateTime LastSeenUtc => store.Load().LastDigestSeenUtc ?? _now().AddDays(-7);

    public void MarkSeen() => store.Update(s => s.LastDigestSeenUtc = _now());
}
