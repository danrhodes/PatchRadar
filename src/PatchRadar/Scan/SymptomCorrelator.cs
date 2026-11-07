using System.Text.RegularExpressions;
using PatchRadar.Core;

namespace PatchRadar.Scan;

public sealed record SymptomCategory(string Name, string[] Keywords);

/// <summary>An installed update that may explain a problem, with the evidence for it.</summary>
public sealed record Suspect(UpdateRow Row, int Score, IReadOnlyList<string> Reasons, IReadOnlyList<KbIssue> Issues);

public sealed record Correlation(IReadOnlyList<Suspect> Suspects, IReadOnlyList<UpdateRow> RecentOthers);

/// <summary>
/// "Something broke": ranks recently installed updates by how well their published known issues and changes match the
/// symptom, and by how close their install date is to when the problem started. An update installed after the
/// problem began cannot have caused it.
/// </summary>
public static class SymptomCorrelator
{
    public static readonly IReadOnlyList<SymptomCategory> Categories = new SymptomCategory[]
    {
        new("Printing or scanning", new[] { "print", "spooler", "scanner" }),
        new("Network, Wi-Fi or VPN", new[] { "network", "wi-fi", "wifi", "wireless", "vpn", "dns", "dhcp", "ethernet", "internet", "adapter" }),
        new("File sharing or mapped drives", new[] { "smb", "file share", "shared folder", "network drive", "mapped drive" }),
        new("Sign-in or passwords", new[] { "sign-in", "sign in", "login", "log in", "password", "authenticat", "kerberos", "ntlm", "credential", "windows hello" }),
        new("Sound", new[] { "audio", "sound", "speaker", "microphone", "headset", "headphone" }),
        new("Display or graphics", new[] { "display", "graphics", "gpu", "monitor", "black screen", "resolution", "flicker", "hdr" }),
        new("Bluetooth or USB devices", new[] { "bluetooth", "usb", "peripheral", "device" }),
        new("Crashes, blue screen or boot", new[] { "blue screen", "bsod", "stop error", "crash", "boot", "restart loop", "stops responding", "unresponsive", "bitlocker", "recovery" }),
        new("Slow or freezing", new[] { "slow", "performance", "freez", "hang", "high cpu", "memory leak", "latency" }),
        new("Remote Desktop", new[] { "remote desktop", "rdp", "remote app", "terminal services" }),
        new("Office, Outlook or Edge", new[] { "outlook", "office", "excel", "edge", "teams", "onedrive" }),
        new("Start menu, taskbar or Explorer", new[] { "start menu", "taskbar", "explorer", "widgets" }),
        new("Something else (use the description)", Array.Empty<string>()),
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "has", "have", "not", "with", "this", "that", "but", "after", "since", "now", "stopped", "working",
        "does", "doesn", "won", "can", "cannot", "when", "from", "into", "just", "its", "was", "are", "isn", "keeps", "again",
    };

    public static IReadOnlyList<string> Keywords(SymptomCategory? category, string? freeText)
    {
        var list = new List<string>(category?.Keywords ?? Array.Empty<string>());
        foreach (Match m in Regex.Matches((freeText ?? "").ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}\-]{2,}"))
            if (!Stop.Contains(m.Value) && !list.Contains(m.Value)) list.Add(m.Value);
        return list;
    }

    /// <param name="onsetUtc">When the problem started; null when the user is not sure (then the last 30 days are considered).</param>
    public static Correlation Find(IEnumerable<UpdateRow> rows, IReadOnlyList<string> keywords, DateTime? onsetUtc, DateTime nowUtc)
    {
        var rx = keywords.Count == 0 ? null
            : new Regex(@"(?<!\w)(?:" + string.Join("|", keywords.OrderByDescending(k => k.Length).Select(Regex.Escape)) + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        var suspects = new List<Suspect>();
        var others = new List<UpdateRow>();
        foreach (var row in rows.Where(r => r.Update.State == UpdateState.Installed))
        {
            var installed = row.Update.InstalledOn.UtcDateTime;
            if (installed > nowUtc) continue;
            if (onsetUtc is { } onset)
            {
                if (installed > onset.AddDays(1)) continue;       // installed after the problem began
                if (installed < onset.AddDays(-14)) continue;     // too long before to be the trigger
            }
            else if ((nowUtc - installed).TotalDays > 30) continue;

            var reasons = new List<string>();
            var issues = new List<KbIssue>();
            var score = 0;
            if (rx is not null)
            {
                foreach (var issue in row.Kb?.KnownIssues ?? new List<KbIssue>())
                {
                    var hits = Hits(rx, $"{issue.Title} {issue.Symptom}");
                    if (hits.Count == 0) continue;
                    issues.Add(issue);
                    score += 6 + 2 * Math.Min(2, hits.Count - 1) + (issue.Status == IssueStatus.Active ? 2 : 0);
                    reasons.Add($"Known issue ({string.Join(", ", hits)}): " + (string.IsNullOrWhiteSpace(issue.Title) ? Cut(issue.Symptom, 110) : issue.Title));
                }
                var change = (row.Kb?.Improvements ?? new List<KbImprovement>()).FirstOrDefault(i => !FixWording.IsMatch(i.Text) && IsMatch(rx, i.Text));
                if (change is not null) { score += 2; reasons.Add("Changes this area: " + Cut(change.Text, 110)); }
                var titleHits = Hits(rx, row.Update.Title);
                if (titleHits.Count > 0) { score += 4; reasons.Add($"Its title mentions {string.Join(", ", titleHits)}"); }
            }

            if (score == 0) { if ((nowUtc - installed).TotalDays <= 7) others.Add(row); continue; }

            var anchor = onsetUtc ?? nowUtc;
            var gap = Math.Abs((anchor - installed).TotalDays);
            score += gap <= 2 ? 4 : gap <= 7 ? 3 : gap <= 14 ? 2 : 1;
            reasons.Add(onsetUtc is null
                ? $"Installed {Ago(nowUtc - installed)}"
                : gap < 1 ? "Installed the day the problem started"
                : $"Installed {gap:0} days {(installed <= anchor ? "before" : "after")} it started");
            if (row.FixAvailableNotInstalled)
                reasons.Add("Microsoft names a fix you do not have yet: " + string.Join(", ", row.FixesNotInstalled.Select(f => f.Kb).Distinct()));
            suspects.Add(new Suspect(row, score, reasons, issues));
        }
        return new Correlation(
            suspects.OrderByDescending(s => s.Score).ThenByDescending(s => s.Row.Update.InstalledOn).ToList(),
            others.OrderByDescending(r => r.Update.InstalledOn).Take(5).ToList());
    }

    /// <summary>"This update resolves an issue where printing could fail" is a fix for the symptom, not a cause of it.</summary>
    private static readonly Regex FixWording = new(@"\b(resolves?|resolved|fix(es|ed)?|addresses|addressed|corrects?|corrected|repairs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsMatch(Regex rx, string text)
    {
        try { return rx.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static List<string> Hits(Regex rx, string text)
    {
        try { return rx.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList(); }
        catch (RegexMatchTimeoutException) { return new(); }
    }

    private static string Ago(TimeSpan t) => t.TotalHours < 1 ? "just now" : t.TotalDays < 1 ? $"{t.TotalHours:0} hours ago" : $"{t.TotalDays:0} days ago";
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
