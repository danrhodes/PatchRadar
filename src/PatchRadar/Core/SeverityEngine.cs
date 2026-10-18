namespace PatchRadar.Core;

public sealed record SeverityOptions(
    IReadOnlyList<string> CriticalKeywords,
    IReadOnlyList<string> ChangePhrases,
    bool TreatBehaviourChangesAsIssues = true)
{
    /// <summary>Empty lists mean "use built-in defaults".</summary>
    public static SeverityOptions Create(IEnumerable<string>? critical, IEnumerable<string>? phrases, bool treat = true)
    {
        var c = critical?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();
        var p = phrases?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new();
        return new SeverityOptions(c.Count > 0 ? c : DefaultKeywords.Critical, p.Count > 0 ? p : DefaultKeywords.ChangePhrases, treat);
    }
}

public sealed class SeverityEngine
{
    private readonly KeywordMatcher _critical;
    private readonly KeywordMatcher _phrases;
    private readonly bool _treatChanges;

    public SeverityEngine(SeverityOptions options)
    {
        _critical = new KeywordMatcher(options.CriticalKeywords);
        _phrases = new KeywordMatcher(options.ChangePhrases, wordBoundary: false);
        _treatChanges = options.TreatBehaviourChangesAsIssues;
    }

    public static SeverityEngine Default() => new(SeverityOptions.Create(null, null));

    public SeverityResult Evaluate(KbData? kb)
    {
        if (kb is null || kb.Status != KbStatus.Ok)
            return new SeverityResult { Severity = Severity.Grey, Summary = kb?.LastError ?? "Not yet fetched" };

        var matched = new List<string>();
        void AddAll(IEnumerable<string> hits)
        {
            foreach (var h in hits) if (!matched.Contains(h, StringComparer.OrdinalIgnoreCase)) matched.Add(h);
        }

        var active = kb.KnownIssues.Where(i => i.Status == IssueStatus.Active).ToList();
        var redIssue = false;
        foreach (var issue in active)
        {
            var hits = _critical.Match($"{issue.Title} {issue.Symptom} {issue.Workaround} {issue.Resolution}");
            if (hits.Count > 0) redIssue = true;
            AddAll(hits);
        }

        var changes = new List<KbImprovement>();
        foreach (var imp in kb.Improvements)
        {
            if (!_phrases.IsMatch(imp.Text)) continue;
            var hits = _critical.Match(imp.Text);
            if (hits.Count == 0) continue;
            changes.Add(imp);
            if (_treatChanges) AddAll(hits);
        }

        var redChange = _treatChanges && changes.Count > 0;
        var sev = redIssue || redChange ? Severity.Red : active.Count > 0 ? Severity.Orange : Severity.Green;

        string? summary = null;
        if (redChange) summary = Trim(changes[0].Text);
        else if (active.Count > 0) summary = Trim(active[0].Title ?? active[0].Symptom);

        return new SeverityResult
        {
            Severity = sev,
            MatchedKeywords = matched,
            BehaviourChanges = changes,
            ActiveIssues = active,
            Summary = summary,
        };
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "\u2026";
}
