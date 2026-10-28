using System.Text;
using PatchRadar.Core;
using PatchRadar.Scan;

namespace PatchRadar.Export;

/// <summary>
/// A "before you install" briefing for the updates Windows is waiting to install: what each one changes,
/// what is known to break, and whether to hold it back. Markdown, so it pastes into a ticket or change request.
/// </summary>
public static class Briefing
{
    public static string Render(IEnumerable<UpdateRow> pending, bool restartPending, DateTime now, string machine)
    {
        var rows = pending.ToList();
        var live = rows.Where(r => !r.Ignored).OrderBy(Rank).ThenBy(r => r.Update.Title).ToList();
        var ignored = rows.Where(r => r.Ignored).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Pending Windows updates on {machine}");
        sb.AppendLine();
        sb.AppendLine($"Prepared {now:yyyy-MM-dd HH:mm} by PatchRadar. {live.Count} update{(live.Count == 1 ? "" : "s")} waiting to be installed.");
        if (restartPending) sb.AppendLine("A restart is already pending from an earlier install.");
        sb.AppendLine();

        if (live.Count == 0)
        {
            sb.AppendLine("Nothing is waiting to be installed.");
        }
        else
        {
            sb.AppendLine(string.Join(" · ", new[] { Severity.Red, Severity.Orange, Severity.Grey, Severity.Green }
                .Select(s => (s, n: live.Count(r => r.Severity == s))).Where(x => x.n > 0)
                .Select(x => $"{x.n} {Label(x.s)}")));
            sb.AppendLine();
        }

        foreach (var r in live) AppendUpdate(sb, r);

        if (ignored.Count > 0)
        {
            sb.AppendLine("## Ignored");
            sb.AppendLine();
            foreach (var r in ignored) sb.AppendLine($"- {Name(r)}");
        }
        return sb.ToString();
    }

    public static int Rank(UpdateRow r) => r.Severity switch { Severity.Red => 0, Severity.Orange => 1, Severity.Grey => 2, _ => 3 };

    public static string Label(Severity s) => s switch
    {
        Severity.Red => "risky", Severity.Orange => "to read", Severity.Grey => "unknown", _ => "no known problems",
    };

    public static string Name(UpdateRow r) => r.Update.HasKb ? $"{r.Update.Kb} – {r.Update.Title}" : r.Update.Title;

    private static void AppendUpdate(StringBuilder sb, UpdateRow r)
    {
        sb.AppendLine($"## [{r.Severity.ToString().ToUpperInvariant()}] {Name(r)}");
        sb.AppendLine();
        sb.AppendLine($"Offered since {r.Update.InstalledOn.LocalDateTime:yyyy-MM-dd}. Category: {r.Update.Category}.");
        if (r.Advice is { } advice) sb.AppendLine().AppendLine("**Advice:** " + advice);
        if (r.DriverNote is { } note) sb.AppendLine().AppendLine("**Driver:** " + note);
        if (r.Result.MatchedKeywords.Count > 0) sb.AppendLine().AppendLine("**Matches your keywords:** " + string.Join(", ", r.Result.MatchedKeywords));

        if (!r.Update.HasKb)
            sb.AppendLine().AppendLine("Microsoft publishes no KB article for this update, so there are no known issues to list.");
        else if (r.Kb is not { Status: KbStatus.Ok })
            sb.AppendLine().AppendLine("The KB article could not be read: " + (r.Kb?.LastError ?? "not fetched yet") + ".");

        if (r.Result.BehaviourChanges.Count > 0)
        {
            sb.AppendLine().AppendLine("**Behaviour changes:**");
            foreach (var c in r.Result.BehaviourChanges) sb.AppendLine("- " + Flat(c.Text));
        }

        if (r.Result.ActiveIssues.Count > 0)
        {
            sb.AppendLine().AppendLine("**Known issues:**");
            foreach (var i in r.Result.ActiveIssues)
            {
                sb.AppendLine("- " + (string.IsNullOrWhiteSpace(i.Title) ? Flat(i.Symptom) : $"{i.Title}: {Flat(i.Symptom)}"));
                if (i.Workaround.Length > 0) sb.AppendLine("  - Workaround: " + Flat(i.Workaround));
                if (i.Resolution.Length > 0) sb.AppendLine("  - Resolution: " + Flat(i.Resolution));
                if (i.ResolvedInKb is not null) sb.AppendLine($"  - {(i.PartialFix ? "Partial fix" : "Fix")}: {i.ResolvedInKb}{(i.OutOfBandFix ? " (out-of-band)" : "")}");
                else if (i.Workaround.Length == 0 && i.Resolution.Length == 0) sb.AppendLine("  - No fix or workaround published yet");
            }
        }
        sb.AppendLine();
    }

    private static string Flat(string s) => string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
