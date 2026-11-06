using System.Text.Json;
using PatchRadar.Infra;
using Xunit;

namespace PatchRadar.Tests;

public class ProblemReportTests
{
    private const string Trace =
        "System.InvalidOperationException: Sequence contains no elements for C:\\Users\\alice\\Desktop\\x.json\n" +
        "   at System.Linq.ThrowHelper.ThrowNoElementsException()\n" +
        "   at PatchRadar.Scan.ScanService.MakeRow(InstalledUpdate u) in C:\\a\\PatchRadar\\src\\Scan\\ScanService.cs:line 191\n" +
        "   at PatchRadar.Scan.ScanService.ScanAsync(Boolean force) in C:\\a\\PatchRadar\\src\\Scan\\ScanService.cs:line 77";

    private static string Entry(string msg) => $"2026-11-06 10:00:00.000 [Error] Scan failed: {msg}";

    [Fact]
    public void Hash_ignores_message_and_line_numbers()
    {
        var other = Trace.Replace("alice", "bob").Replace("line 191", "line 195").Replace("Sequence contains no elements", "Different text");
        Assert.Equal(ProblemHash.Compute(Trace), ProblemHash.Compute(other));
        Assert.Equal(8, ProblemHash.Compute(Trace).Length);
    }

    [Fact]
    public void Hash_differs_for_a_different_method()
        => Assert.NotEqual(ProblemHash.Compute(Trace), ProblemHash.Compute(Trace.Replace("MakeRow", "Other")));

    [Fact]
    public void Offline_exceptions_are_noise()
        => Assert.True(ProblemHash.IsNoise("Update check failed: System.Net.Http.HttpRequestException: No such host is known.\n   at System.Net.Http.X()"));

    [Fact]
    public void Scrub_removes_user_machine_paths_and_addresses()
    {
        var s = ProblemScrubber.Scrub(@"alice on LAPTOP-9 opened C:\Users\alice\AppData\x.log from 10.1.2.3, mail a.b@corp.co.uk, https://u:pw@proxy.local:8080/",
            "alice", "LAPTOP-9", "CORP");
        Assert.DoesNotContain("alice", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LAPTOP-9", s);
        Assert.DoesNotContain("10.1.2.3", s);
        Assert.DoesNotContain("a.b@corp", s);
        Assert.DoesNotContain("pw@", s);
        Assert.Contains(@"C:\Users\<user>", s);
    }

    [Fact]
    public void Scrub_shortens_build_machine_paths_in_stack_frames()
    {
        var s = ProblemScrubber.Scrub(Trace, "alice", "M");
        Assert.Contains("in ScanService.cs:line 191", s);
        Assert.DoesNotContain(@"C:\a\PatchRadar", s);
    }

    [Fact]
    public void Scanner_finds_only_errors_with_stacks()
    {
        string[] lines =
        [
            "2026-11-06 09:59:00.000 [Information] starting",
            Entry(Trace.Split('\n')[0]),
            .. Trace.Split('\n')[1..],
            "2026-11-06 10:01:00.000 [Error] Update check failed",                       // no stack: expected failure, not a bug
            "2026-11-06 10:02:00.000 [Error] x: System.Net.Http.HttpRequestException: offline",
            "   at System.Net.Http.Foo()",
        ];
        var found = LogScanner.Parse(lines, new DateTime(2026, 11, 1)).ToList();
        var e = Assert.Single(found);
        Assert.Contains("MakeRow", e.Text);
        Assert.Single(e.Context);
    }

    [Fact]
    public void Scanner_ignores_old_entries()
        => Assert.Empty(LogScanner.Parse([Entry(Trace.Split('\n')[0]), .. Trace.Split('\n')[1..]], new DateTime(2026, 12, 1)));

    [Fact]
    public void A_problem_is_offered_once_then_again_only_after_a_day_up_to_three_times()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pr-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ProblemStore(path);
            var t = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(store.ShouldOffer("abc", t));
            store.Set("abc", ProblemStatus.Offered);
            Assert.False(store.ShouldOffer("abc", DateTime.UtcNow));
            Assert.True(store.ShouldOffer("abc", DateTime.UtcNow.AddDays(2)));
            store.Set("abc", ProblemStatus.Dismissed);
            Assert.False(store.ShouldOffer("abc", DateTime.UtcNow.AddDays(30)));
            Assert.Equal(ProblemStatus.Dismissed, new ProblemStore(path).Get("abc")!.Status);   // persisted
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Link_fits_in_a_url_and_points_at_this_repo()
    {
        var huge = new ProblemEntry(DateTime.Now, Entry(Trace.Split('\n')[0]) + "\n" + string.Join("\n", Enumerable.Repeat("   at PatchRadar.A.B()", 400)), Array.Empty<string>());
        var report = ProblemIssue.Build(huge, s => s, "1.2.1 (full)", "10.0.26300 (64-bit)");
        var url = ProblemIssue.NewIssueUrl(report);
        Assert.True(url.Length <= ProblemIssue.MaxUrlLength + 80);
        Assert.True(ProblemIssue.IsIssuePage(url));
        Assert.Contains("%5Bproblem%20" + report.Hash, url);
    }

    [Theory]
    [InlineData("https://github.com/danrhodes/PatchRadar/issues/12", true)]
    [InlineData("https://github.com/someoneelse/PatchRadar/issues/12", false)]
    [InlineData("http://github.com/danrhodes/PatchRadar/issues/12", false)]
    [InlineData("https://evil.example/danrhodes/PatchRadar/issues/12", false)]
    public void Only_issue_pages_in_this_repo_are_opened(string url, bool ok) => Assert.Equal(ok, ProblemIssue.IsIssuePage(url));

    [Fact]
    public void Issue_search_result_is_matched_by_hash_in_title()
    {
        const string json = """
            {"items":[
              {"number":3,"title":"Unrelated","html_url":"https://github.com/danrhodes/PatchRadar/issues/3","state":"open"},
              {"number":9,"title":"[problem 3fa9c1d2] InvalidOperationException","html_url":"https://github.com/danrhodes/PatchRadar/issues/9","state":"closed"}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var hit = ProblemLookup.ParseFirst(doc.RootElement, "3fa9c1d2");
        Assert.Equal(9, hit!.Number);
        Assert.False(hit.Open);
        Assert.Null(ProblemLookup.ParseFirst(doc.RootElement, "00000000"));
    }

    [Fact]
    public void Manual_scan_lists_every_distinct_error_even_when_dismissed_or_switched_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string Stamp(int minutesAgo) => DateTime.Now.AddMinutes(-minutesAgo).ToString("yyyy-MM-dd HH:mm:ss.fff");
            var other = Trace.Replace("MakeRow", "OtherMethod");
            var lines = new List<string>();
            foreach (var (t, minutes) in new[] { (Trace, 30), (other, 20), (Trace, 10) })     // the first error happens twice
            {
                var parts = t.Split('\n');
                lines.Add($"{Stamp(minutes)} [Error] boom: {parts[0]}");
                lines.AddRange(parts[1..]);
            }
            File.WriteAllLines(Path.Combine(dir, $"patchradar-{DateTime.Now:yyyyMMdd}.log"), lines);

            var store = new ProblemStore(Path.Combine(dir, "p.json"));
            var w = new ProblemWatcher(dir, store, () => false, (_, _, _) => { }, _ => { }, "1.0", "Win");
            var groups = w.Groups(DateTime.UtcNow);
            Assert.Equal(2, groups.Count);
            Assert.Equal(2, groups.Single(g => g.Count == 2).Count);
            Assert.True(groups[0].Last >= groups[1].Last);                       // newest first

            store.Set(groups[0].Report.Hash, ProblemStatus.Dismissed);
            Assert.Equal(2, w.Groups(DateTime.UtcNow).Count);                    // dismissed ones are still listed
            Assert.Equal(ProblemStatus.Dismissed, w.Groups(DateTime.UtcNow).Single(g => g.Report.Hash == groups[0].Report.Hash).Status);
            Assert.Empty(new ProblemWatcher(Path.Combine(dir, "none"), store, () => true, (_, _, _) => { }, _ => { }, "1.0", "Win").Groups(DateTime.UtcNow));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Automatic_notice_says_how_many_more_are_waiting()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lines = new List<string>();
            foreach (var (t, minutes) in new[] { (Trace, 20), (Trace.Replace("MakeRow", "OtherMethod"), 10) })
            {
                var parts = t.Split('\n');
                lines.Add($"{DateTime.Now.AddMinutes(-minutes):yyyy-MM-dd HH:mm:ss.fff} [Error] boom: {parts[0]}");
                lines.AddRange(parts[1..]);
            }
            File.WriteAllLines(Path.Combine(dir, $"patchradar-{DateTime.Now:yyyyMMdd}.log"), lines);
            var message = "";
            var w = new ProblemWatcher(dir, new ProblemStore(Path.Combine(dir, "p.json")), () => true, (_, m, _) => message = m, _ => { }, "1.0", "Win");
            Assert.NotNull(w.CheckNow(DateTime.UtcNow));
            Assert.Contains("1 more is waiting", message);
            Assert.NotNull(w.CheckNow(DateTime.UtcNow));                          // the second one is offered on the next check
            Assert.Null(w.CheckNow(DateTime.UtcNow));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Watcher_offers_one_new_problem_and_nothing_when_off_or_repeated()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var store = Path.Combine(dir, "p.json");
        try
        {
            var stamp = DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss.fff");
            var lines = new[] { $"{stamp} [Error] boom: {Trace.Split('\n')[0]}" }.Concat(Trace.Split('\n')[1..]);
            File.WriteAllLines(Path.Combine(dir, $"patchradar-{DateTime.Now:yyyyMMdd}.log"), lines);
            var toasts = 0; var on = true;
            var w = new ProblemWatcher(dir, new ProblemStore(store), () => on, (_, _, _) => toasts++, _ => { }, "1.0", "Win");

            on = false;
            Assert.Null(w.CheckNow(DateTime.UtcNow));
            on = true;
            Assert.NotNull(w.CheckNow(DateTime.UtcNow));
            Assert.Null(w.CheckNow(DateTime.UtcNow));      // already offered, less than a day ago
            Assert.Equal(1, toasts);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
