using System.Text.RegularExpressions;
using PatchRadar.Core;

namespace PatchRadar.Kb;

/// <summary>The parts of an article that matter for change detection. Small enough to keep per reviewed KB.</summary>
public sealed class KbSnapshot
{
    public DateTime CapturedUtc { get; set; }
    public string ContentHash { get; set; } = "";
    public List<KbImprovement> Improvements { get; set; } = new();
    public List<KbIssue> KnownIssues { get; set; } = new();

    public static KbSnapshot Of(KbData d, DateTime nowUtc) => new()
    {
        CapturedUtc = nowUtc,
        ContentHash = d.ContentHash,
        Improvements = d.Improvements.Select(i => new KbImprovement { Category = i.Category, Text = i.Text }).ToList(),
        KnownIssues = d.KnownIssues.Select(Copy).ToList(),
    };

    private static KbIssue Copy(KbIssue i) => new()
    {
        Title = i.Title, AppliesTo = i.AppliesTo, Symptom = i.Symptom, Workaround = i.Workaround, Resolution = i.Resolution,
        NextSteps = i.NextSteps, ResolvedInKb = i.ResolvedInKb, OutOfBandFix = i.OutOfBandFix, PartialFix = i.PartialFix, Status = i.Status,
    };

    public KbData AsKbData(string kb) => new()
    {
        Kb = kb, Status = KbStatus.Ok, FetchedUtc = CapturedUtc, ContentHash = ContentHash, Source = "snapshot",
        Improvements = Improvements, KnownIssues = KnownIssues,
    };
}

public enum ChangeKind { Added, Removed, Changed }

/// <summary>One change. For <see cref="ChangeKind.Changed"/> issues, <see cref="Fields"/> lists what moved.</summary>
public sealed record KbChange(ChangeKind Kind, string Area, string Title, string? Before, string? After, IReadOnlyList<FieldChange>? Fields = null);

public sealed record FieldChange(string Field, string Before, string After);

public sealed class KbDiffResult
{
    public IReadOnlyList<KbChange> Changes { get; init; } = Array.Empty<KbChange>();
    public bool IsEmpty => Changes.Count == 0;
    public int Added => Changes.Count(c => c.Kind == ChangeKind.Added);
    public int Removed => Changes.Count(c => c.Kind == ChangeKind.Removed);
    public int Modified => Changes.Count(c => c.Kind == ChangeKind.Changed);

    public string Summary
    {
        get
        {
            if (IsEmpty) return "No changes";
            var parts = new List<string>();
            if (Added > 0) parts.Add($"{Added} added");
            if (Modified > 0) parts.Add($"{Modified} changed");
            if (Removed > 0) parts.Add($"{Removed} removed");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Before-and-after of two readings of the same article. Compares content, not hashes, so a parser upgrade
/// that changes the hash without changing any text is not reported as an edit.
/// </summary>
public static class KbDiff
{
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    public static KbDiffResult Compare(KbData before, KbData after) => Compare(before.Improvements, before.KnownIssues, after.Improvements, after.KnownIssues);

    public static KbDiffResult Compare(KbSnapshot before, KbData after) => Compare(before.Improvements, before.KnownIssues, after.Improvements, after.KnownIssues);

    private static KbDiffResult Compare(IReadOnlyList<KbImprovement> oldImp, IReadOnlyList<KbIssue> oldIss,
        IReadOnlyList<KbImprovement> newImp, IReadOnlyList<KbIssue> newIss)
    {
        var changes = new List<KbChange>();
        CompareIssues(oldIss, newIss, changes);
        CompareImprovements(oldImp, newImp, changes);
        return new KbDiffResult { Changes = changes };
    }

    // ---- known issues: matched by title (or, for untitled ones, by the start of the symptom) ----------------

    private static string IssueKey(KbIssue i) => Norm(string.IsNullOrWhiteSpace(i.Title) ? Cut(i.Symptom, 60) : i.Title!);

    private static void CompareIssues(IReadOnlyList<KbIssue> oldList, IReadOnlyList<KbIssue> newList, List<KbChange> changes)
    {
        var oldByKey = oldList.GroupBy(IssueKey).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>();
        foreach (var n in newList)
        {
            var key = IssueKey(n);
            if (!seen.Add(key)) continue;
            var label = Label(n);
            if (!oldByKey.TryGetValue(key, out var o))
            {
                changes.Add(new KbChange(ChangeKind.Added, "Known issue", label, null, Cut(n.Symptom, 400)));
                continue;
            }
            var fields = new List<FieldChange>();
            void F(string name, string a, string b) { if (Norm(a) != Norm(b)) fields.Add(new FieldChange(name, a, b)); }
            F("Symptom", o.Symptom, n.Symptom);
            F("Workaround", o.Workaround, n.Workaround);
            F("Resolution", o.Resolution, n.Resolution);
            F("Next steps", o.NextSteps, n.NextSteps);
            F("Fix", FixText(o), FixText(n));
            F("Status", o.Status.ToString(), n.Status.ToString());
            if (fields.Count > 0) changes.Add(new KbChange(ChangeKind.Changed, "Known issue", label, null, null, fields));
        }
        foreach (var (key, o) in oldByKey)
            if (!seen.Contains(key)) changes.Add(new KbChange(ChangeKind.Removed, "Known issue", Label(o), Cut(o.Symptom, 400), null));
    }

    private static string FixText(KbIssue i) =>
        i.ResolvedInKb is null ? "" : $"{(i.PartialFix ? "Partial fix" : "Fix")} {i.ResolvedInKb}{(i.OutOfBandFix ? " (out-of-band)" : "")}";

    private static string Label(KbIssue i) => string.IsNullOrWhiteSpace(i.Title) ? Cut(i.Symptom, 80) : i.Title!;

    // ---- improvements: exact text, then fuzzy pairing for edited wording ---------------------------------------

    private static void CompareImprovements(IReadOnlyList<KbImprovement> oldList, IReadOnlyList<KbImprovement> newList, List<KbChange> changes)
    {
        var oldMap = oldList.Where(i => i.Text.Length > 0).GroupBy(i => Norm(i.Text)).ToDictionary(g => g.Key, g => g.First());
        var newMap = newList.Where(i => i.Text.Length > 0).GroupBy(i => Norm(i.Text)).ToDictionary(g => g.Key, g => g.First());
        // Whatever matches word for word is unchanged; only the rest is compared.
        var oldRemaining = oldMap.Where(kv => !newMap.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var newRemaining = newMap.Where(kv => !oldMap.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();

        // Pair edited items: same category tag and similar wording.
        foreach (var n in newRemaining.ToList())
        {
            var best = oldRemaining
                .Select(o => (o, score: Similarity(o.Text, n.Text) + (string.Equals(o.Category, n.Category, StringComparison.OrdinalIgnoreCase) ? 0.1 : 0)))
                .OrderByDescending(x => x.score).FirstOrDefault();
            if (best.o is not null && best.score >= 0.6)
            {
                changes.Add(new KbChange(ChangeKind.Changed, "Improvement", Cut(n.Text, 90), best.o.Text, n.Text));
                oldRemaining.Remove(best.o);
                newRemaining.Remove(n);
            }
        }
        foreach (var n in newRemaining) changes.Add(new KbChange(ChangeKind.Added, "Improvement", Cut(n.Text, 90), null, n.Text));
        foreach (var o in oldRemaining) changes.Add(new KbChange(ChangeKind.Removed, "Improvement", Cut(o.Text, 90), o.Text, null));
    }

    private static double Similarity(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var inter = x.Intersect(y).Count();
        return (double)inter / x.Union(y).Count();
    }

    private static HashSet<string> Words(string s) =>
        NonWord.Split(s.ToLowerInvariant()).Where(w => w.Length > 2).ToHashSet();

    private static string Norm(string s) => Ws.Replace(s ?? "", " ").Trim().ToLowerInvariant();
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
