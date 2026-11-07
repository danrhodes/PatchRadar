using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.State;
using Xunit;

namespace PatchRadar.Tests;

public class FixWatchAndSymptomTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static KbIssue Issue(string title, string symptom = "", string workaround = "", string? fixKb = null, IssueStatus status = IssueStatus.Active)
        => new() { Title = title, Symptom = symptom, Workaround = workaround, ResolvedInKb = fixKb, Status = status };

    private static KbData Kb(params KbIssue[] issues) => new() { Kb = "KB1", Status = KbStatus.Ok, KnownIssues = issues.ToList() };

    private static UpdateRow Row(string kb, string title, int daysAgo, KbData? data = null)
        => new(new InstalledUpdate(kb, title, new DateTimeOffset(Now.AddDays(-daysAgo)), UpdateState.Installed, UpdateCategory.Cumulative),
            data, new SeverityResult { Severity = Severity.Orange });

    // ---- fix watch -------------------------------------------------------------------------

    [Fact]
    public void Only_issues_without_a_fix_are_worth_watching()
    {
        var kb = Kb(Issue("Printing fails"), Issue("Has workaround", workaround: "Do X"), Issue("Fixed", fixKb: "KB9"));
        Assert.Equal(new[] { "Printing fails" }, WatchStore.OpenIssues(kb).Select(i => i.Title));
    }

    [Fact]
    public void Evaluate_reports_a_new_workaround_and_keeps_the_rest_open()
    {
        var entry = new WatchEntry { Open = { "printing fails", "vpn drops" } };
        var kb = Kb(Issue("Printing fails", workaround: "Reinstall the driver"), Issue("VPN drops"));
        var (lines, remaining) = WatchStore.Evaluate(entry, kb);
        Assert.Single(lines);
        Assert.Contains("workaround published", lines[0]);
        Assert.Equal(new[] { "vpn drops" }, remaining);
    }

    [Fact]
    public void Evaluate_treats_a_named_fix_and_a_removed_issue_as_good_news()
    {
        var entry = new WatchEntry { Open = { "printing fails", "vpn drops" } };
        var (lines, remaining) = WatchStore.Evaluate(entry, Kb(Issue("Printing fails", fixKb: "KB5000")));
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Contains("KB5000"));
        Assert.Contains(lines, l => l.Contains("no longer listed"));
        Assert.Empty(remaining);
    }

    [Fact]
    public void A_failed_fetch_never_fires_the_watch()
    {
        var entry = new WatchEntry { Open = { "printing fails" } };
        var (lines, remaining) = WatchStore.Evaluate(entry, new KbData { Status = KbStatus.Blocked });
        Assert.Empty(lines);
        Assert.Single(remaining);
    }

    // ---- symptom correlation ---------------------------------------------------------------

    [Fact]
    public void Matching_known_issue_ranks_above_an_unrelated_recent_update()
    {
        var printer = Row("KB100", "Cumulative Update", 2, Kb(Issue("Printers stop working", "Print jobs fail after the update")));
        var other = Row("KB200", "Cumulative Update for .NET", 1, Kb(Issue("Something else", "Unrelated")));
        var kw = SymptomCorrelator.Keywords(SymptomCorrelator.Categories[0], null);

        var result = SymptomCorrelator.Find(new[] { other, printer }, kw, Now.AddDays(-1), Now);

        Assert.Equal("KB100", Assert.Single(result.Suspects).Row.Update.Kb);
        Assert.Contains(result.RecentOthers, r => r.Update.Kb == "KB200");
    }

    [Fact]
    public void An_improvement_that_fixes_the_symptom_is_not_evidence_against_the_update()
    {
        var data = Kb();
        data.Improvements.Add(new KbImprovement { Category = "Printing", Text = "This update resolves an issue where printing could fail." });
        var result = SymptomCorrelator.Find(new[] { Row("KB100", "Security Update", 3, data) }, new[] { "print" }, Now.AddDays(-1), Now);
        Assert.Empty(result.Suspects);
        Assert.Single(result.RecentOthers);
    }

    [Fact]
    public void An_update_installed_after_the_problem_began_is_not_a_suspect()
    {
        var late = Row("KB100", "Cumulative Update", 0, Kb(Issue("Printers stop working", "Print jobs fail")));
        var result = SymptomCorrelator.Find(new[] { late }, new[] { "print" }, Now.AddDays(-5), Now);
        Assert.Empty(result.Suspects);
    }

    [Fact]
    public void Driver_titles_count_and_free_text_adds_keywords()
    {
        var driver = Row("Driver-abc", "Realtek Audio driver update", 3);
        var kw = SymptomCorrelator.Keywords(SymptomCorrelator.Categories[^1], "no sound from the speakers");
        Assert.Contains("sound", kw);
        var result = SymptomCorrelator.Find(new[] { driver }, new[] { "audio" }, null, Now);
        Assert.Contains("Its title mentions audio", Assert.Single(result.Suspects).Reasons);
    }

    [Fact]
    public void Updates_older_than_a_month_are_ignored_when_the_start_is_unknown()
    {
        var old = Row("KB100", "Cumulative Update", 45, Kb(Issue("Printers stop working", "Print jobs fail")));
        Assert.Empty(SymptomCorrelator.Find(new[] { old }, new[] { "print" }, null, Now).Suspects);
    }
}
