using System.Text;
using PatchRadar.Core;
using PatchRadar.State;

namespace PatchRadar.Scan;

/// <summary>Criteria for the All Updates window. Every field is optional; empty means "don't filter on this".</summary>
public sealed record UpdateFilterCriteria
{
    public Severity? Severity { get; init; }
    public UpdateCategory? Category { get; init; }
    public DateTime? From { get; init; }          // inclusive, local date
    public DateTime? To { get; init; }            // inclusive, local date
    public string Text { get; init; } = "";       // KB or title
    public string Symptom { get; init; } = "";    // cached improvements, known issues, workarounds
    public ReviewStatus? Review { get; init; }
    /// <summary>Only updates with a published fix that is not installed on this PC.</summary>
    public bool FixNotInstalledOnly { get; init; }
}

public static class UpdateFilter
{
    public static IReadOnlyList<UpdateRow> Apply(IEnumerable<UpdateRow> rows, UpdateFilterCriteria c)
    {
        var text = c.Text.Trim();
        var symptom = c.Symptom.Trim();
        return rows.Where(r =>
        {
            if (c.Severity is { } s && r.Severity != s) return false;
            if (c.Category is { } cat && r.Update.Category != cat) return false;
            if (c.Review is { } rv && r.Review != rv) return false;
            if (c.FixNotInstalledOnly && !r.FixAvailableNotInstalled) return false;
            var local = r.Update.InstalledOn.LocalDateTime.Date;
            if (c.From is { } f && local < f.Date) return false;
            if (c.To is { } t && local > t.Date) return false;
            if (text.Length > 0 &&
                !r.Update.Kb.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !r.Update.Title.Contains(text, StringComparison.OrdinalIgnoreCase)) return false;
            if (symptom.Length > 0 && !MatchesSymptom(r, symptom)) return false;
            return true;
        }).ToList();
    }

    /// <summary>
    /// Most critical-keyword matches first; ties go to the more severe, then the newer update.
    /// </summary>
    public static IReadOnlyList<UpdateRow> RankByKeywordHits(IEnumerable<UpdateRow> rows) =>
        rows.OrderByDescending(r => r.KeywordHits)
            .ThenBy(r => r.Severity switch { Severity.Red => 0, Severity.Orange => 1, Severity.Green => 2, _ => 3 })
            .ThenByDescending(r => r.Update.InstalledOn)
            .ToList();

    /// <summary>
    /// Case-insensitive substring over the cached KB text, plus a punctuation-insensitive pass so
    /// "wifi" finds "Wi-Fi" and "mschapv2" finds "MS-CHAPv2". Hex codes like 0x80070 work as plain text.
    /// </summary>
    public static bool MatchesSymptom(UpdateRow row, string query)
    {
        var kb = row.Kb;
        if (kb is null || kb.Status != KbStatus.Ok) return false;
        var haystack = Haystack(kb);
        if (haystack.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var nq = Normalise(query);
        return nq.Length >= 3 && Normalise(haystack).Contains(nq, StringComparison.Ordinal);
    }

    private static string Haystack(KbData kb)
    {
        var sb = new StringBuilder();
        foreach (var i in kb.Improvements) sb.Append(i.Text).Append('\n');
        foreach (var k in kb.KnownIssues) sb.Append(k.Title).Append(' ').Append(k.Symptom).Append(' ').Append(k.Workaround).Append('\n');
        return sb.ToString();
    }

    private static string Normalise(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
