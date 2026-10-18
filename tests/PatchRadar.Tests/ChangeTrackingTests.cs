using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;

namespace PatchRadar.Tests;

public class KbDiffTests
{
    private static KbData Article(params KbIssue[] issues) => new() { Kb = "KB1", Status = KbStatus.Ok, KnownIssues = issues.ToList() };

    private static KbIssue Issue(string title, string symptom = "It breaks.", string workaround = "") =>
        new() { Title = title, Symptom = symptom, Workaround = workaround };

    [Fact]
    public void Identical_IsEmpty() =>
        Assert.True(KbDiff.Compare(Article(Issue("A")), Article(Issue("A"))).IsEmpty);

    [Fact]
    public void WhitespaceAndCase_AreNotChanges() =>
        Assert.True(KbDiff.Compare(Article(Issue("A", "It  breaks.")), Article(Issue("a", "it breaks. "))).IsEmpty);

    [Fact]
    public void HashOnlyDifference_IsNotAnEdit()
    {
        var a = Article(Issue("A")); a.ContentHash = "old-parser";
        var b = Article(Issue("A")); b.ContentHash = "new-parser";
        Assert.True(KbDiff.Compare(a, b).IsEmpty);
    }

    [Fact]
    public void NewIssue_IsAdded_RemovedIssue_IsRemoved()
    {
        var d = KbDiff.Compare(Article(Issue("Old problem")), Article(Issue("New problem")));
        Assert.Equal(1, d.Added);
        Assert.Equal(1, d.Removed);
        Assert.Equal("New problem", d.Changes.Single(c => c.Kind == ChangeKind.Added).Title);
        Assert.Equal("It breaks.", d.Changes.Single(c => c.Kind == ChangeKind.Removed).Before);
    }

    [Fact]
    public void NewWorkaround_ShowsFieldBeforeAndAfter()
    {
        var d = KbDiff.Compare(Article(Issue("A")), Article(Issue("A", workaround: "Reboot twice.")));
        var c = Assert.Single(d.Changes);
        Assert.Equal(ChangeKind.Changed, c.Kind);
        var f = Assert.Single(c.Fields!);
        Assert.Equal(("Workaround", "", "Reboot twice."), (f.Field, f.Before, f.After));
    }

    [Fact]
    public void FixAppearing_IsReported()
    {
        var after = Issue("A"); after.ResolvedInKb = "KB9"; after.OutOfBandFix = true;
        var d = KbDiff.Compare(Article(Issue("A")), Article(after));
        Assert.Contains(d.Changes.Single().Fields!, f => f.Field == "Fix" && f.After.Contains("KB9") && f.After.Contains("out-of-band"));
    }

    [Fact]
    public void IssueMarkedResolved_IsReported()
    {
        var after = Issue("A"); after.Status = IssueStatus.Resolved;
        Assert.Contains(KbDiff.Compare(Article(Issue("A")), Article(after)).Changes.Single().Fields!, f => f.Field == "Status" && f.After == "Resolved");
    }

    [Fact]
    public void EditedImprovement_IsPairedNotAddedAndRemoved()
    {
        var a = new KbData { Improvements = { new() { Category = "Authentication", Text = "[Authentication] This update changes the default for BlockNtlmv1SSO to Audit." } } };
        var b = new KbData { Improvements = { new() { Category = "Authentication", Text = "[Authentication] This update changes the default for BlockNtlmv1SSO to Enforce." } } };
        var d = KbDiff.Compare(a, b);
        var c = Assert.Single(d.Changes);
        Assert.Equal(ChangeKind.Changed, c.Kind);
        Assert.Contains("Audit", c.Before);
        Assert.Contains("Enforce", c.After);
    }

    [Fact]
    public void UnrelatedImprovements_AreAddedAndRemoved()
    {
        var a = new KbData { Improvements = { new() { Text = "Fixed a crash in the print spooler service." } } };
        var b = new KbData { Improvements = { new() { Text = "Improved Bluetooth pairing reliability for headsets." } } };
        var d = KbDiff.Compare(a, b);
        Assert.Equal((1, 1, 0), (d.Added, d.Removed, d.Modified));
    }

    [Fact]
    public void Summary_ReadsNaturally()
    {
        var d = KbDiff.Compare(Article(Issue("A"), Issue("B")), Article(Issue("A", workaround: "x"), Issue("C")));
        Assert.Equal("1 added, 1 changed, 1 removed", d.Summary);
        Assert.Equal("No changes", KbDiff.Compare(Article(), Article()).Summary);
    }

    [Fact]
    public void RealFixture_AgainstItself_IsEmpty()
    {
        var d = Fx.Parse("kb5127648");
        Assert.True(KbDiff.Compare(KbSnapshot.Of(d, DateTime.UtcNow), d).IsEmpty);
    }
}

public class ChangeHistoryTests
{
    private sealed class Seq(params KbData[] results) : IKbSource
    {
        private readonly Queue<KbData> _q = new(results);
        public string Name => "seq";
        public Task<KbData> FetchAsync(string kb, CancellationToken ct) => Task.FromResult(_q.Dequeue());
    }

    private static InstalledUpdate Upd() => new("KB1", "t", DateTimeOffset.UtcNow.AddDays(-2), UpdateState.Installed, UpdateCategory.Cumulative);

    private static KbData Fresh(Action<KbData>? tweak = null)
    {
        var d = Fx.Parse("kb5044281");
        d.Kb = "KB1";
        tweak?.Invoke(d);
        d.ContentHash = KbParser.Hash(d);
        return d;
    }

    [Fact]
    public async Task EditedArticle_KeepsThePreviousReading_AndWhenItChanged()
    {
        using var t = new TempDir();
        var f = new KbFetcher(new[] { new Seq(Fresh(), Fresh(d => d.KnownIssues[0].Workaround = "A brand new workaround.")) }, new KbCache(t.Path));
        var s = new ScanningSettings();
        var first = (await f.GetAsync(Upd(), s, false, default))!;
        Assert.Null(first.Previous);
        Assert.Null(first.ChangedUtc);

        var second = (await f.GetAsync(Upd(), s, force: true, default))!;
        Assert.NotNull(second.Previous);
        Assert.NotNull(second.ChangedUtc);
        Assert.NotEqual("A brand new workaround.", second.Previous!.KnownIssues[0].Workaround);
        Assert.Equal(1, KbDiff.Compare(second.Previous, second).Modified);
    }

    [Fact]
    public async Task UnchangedArticle_CarriesEarlierHistoryForward_AndDoesNotInventChanges()
    {
        using var t = new TempDir();
        var f = new KbFetcher(new[] { new Seq(Fresh(), Fresh(d => d.KnownIssues[0].Workaround = "v2"), Fresh(d => d.KnownIssues[0].Workaround = "v2")) }, new KbCache(t.Path));
        var s = new ScanningSettings();
        await f.GetAsync(Upd(), s, false, default);
        var changed = (await f.GetAsync(Upd(), s, true, default))!;
        var again = (await f.GetAsync(Upd(), s, true, default))!;
        Assert.Equal(changed.ChangedUtc, again.ChangedUtc);
        Assert.NotNull(again.Previous);
    }

    [Fact]
    public async Task ParserUpgrade_HashChangeWithSameText_IsNotAChange()
    {
        using var t = new TempDir();
        var second = Fresh(); second.ContentHash = "different-hash-same-text";
        var f = new KbFetcher(new[] { new Seq(Fresh(), second) }, new KbCache(t.Path));
        var s = new ScanningSettings();
        await f.GetAsync(Upd(), s, false, default);
        var r = (await f.GetAsync(Upd(), s, true, default))!;
        Assert.Null(r.ChangedUtc);
        Assert.Null(r.Previous);
    }
}

public class ReviewBaselineAndFixTests
{
    private static InstalledUpdate Upd(string kb, int daysAgo, UpdateCategory cat = UpdateCategory.Cumulative) =>
        new(kb, "t " + kb, DateTimeOffset.UtcNow.AddDays(-daysAgo), UpdateState.Installed, cat);

    private static (ScanService scan, KbCache cache, TempDir dir) Make(params InstalledUpdate[] installed)
    {
        var t = new TempDir();
        var cache = new KbCache(Path.Combine(t.Path, "kb"));
        var reviews = new ReviewStore(new StateStore(Path.Combine(t.Path, "state.json")), null, Path.Combine(t.Path, "snap"));
        return (new ScanService(new FakeHistory(installed), new KbFetcher(Array.Empty<IKbSource>(), cache), () => new AppSettings(), null, reviews), cache, t);
    }

    private static KbData Golden(string kb = "KB5127648") { var d = Fx.Parse("kb5127648"); d.Kb = kb; return d; }

    [Fact]
    public async Task EditedAfterReview_ShowsExactlyWhatChanged()
    {
        var (scan, cache, t) = Make(Upd("KB5127648", 3));
        using var _t = t;
        var kb = Golden();
        cache.Save(kb);
        var row = (await scan.ListCachedAsync(false, default)).Single();
        scan.SetReviewed(row, true);

        kb.KnownIssues[0].Workaround = "Microsoft published a new workaround.";
        kb.KnownIssues.Add(new KbIssue { Title = "A brand new issue", Symptom = "Something else broke." });
        kb.ContentHash = KbParser.Hash(kb);
        cache.Save(kb);

        var after = (await scan.ListCachedAsync(false, default)).Single();
        Assert.Equal(ReviewStatus.ChangedSinceReviewed, after.Review);
        Assert.NotNull(after.ChangeSinceReview);
        Assert.Equal(1, after.ChangeSinceReview!.Added);
        Assert.Equal(1, after.ChangeSinceReview.Modified);
        Assert.Contains(after.ChangeSinceReview.Changes, c => c.Kind == ChangeKind.Added && c.Title == "A brand new issue");
    }

    [Fact]
    public async Task HashChangeWithoutTextChange_StaysReviewed()
    {
        var (scan, cache, t) = Make(Upd("KB5127648", 3));
        using var _t = t;
        var kb = Golden();
        cache.Save(kb);
        scan.SetReviewed((await scan.ListCachedAsync(false, default)).Single(), true);

        kb.ContentHash = "hash-from-a-newer-parser";
        cache.Save(kb);

        var after = (await scan.ListCachedAsync(false, default)).Single();
        Assert.Equal(ReviewStatus.Reviewed, after.Review);
        Assert.Null(after.ChangeSinceReview);
    }

    [Fact]
    public async Task FixKbNotInstalled_IsFlagged_Installed_IsNot()
    {
        var (scan, cache, t) = Make(Upd("KB5127648", 3));
        using var _t = t;
        cache.Save(Golden());
        var row = (await scan.ListCachedAsync(false, default)).Single();
        var usb = Assert.Single(row.Fixes, f => f.Kb == "KB5129195");
        Assert.False(usb.Installed);
        Assert.True(usb.OutOfBand);
        Assert.True(usb.Partial);
        Assert.True(row.FixAvailableNotInstalled);
        Assert.False(row.NewerCumulativeInstalled);
    }

    [Fact]
    public async Task FixKbInstalled_IsNotFlagged()
    {
        var (scan, cache, t) = Make(Upd("KB5127648", 3), Upd("KB5129195", 20, UpdateCategory.Security));
        using var _t = t;
        cache.Save(Golden());
        var row = (await scan.ListCachedAsync(false, default)).Single(r => r.Update.Kb == "KB5127648");
        Assert.True(row.Fixes.Single(f => f.Kb == "KB5129195").Installed);
        Assert.DoesNotContain(row.Fixes, f => f.Kb == "KB5129195" && !f.Installed);
    }

    [Fact]
    public async Task NewerCumulativeUpdate_IsNoted()
    {
        var (scan, cache, t) = Make(Upd("KB5127648", 30), Upd("KB5999999", 2));
        using var _t = t;
        cache.Save(Golden());
        var row = (await scan.ListCachedAsync(false, default)).Single(r => r.Update.Kb == "KB5127648");
        Assert.True(row.NewerCumulativeInstalled);
    }

    [Fact]
    public async Task KeywordHits_CountsMatchedKeywords()
    {
        var (scan, cache, t) = Make(Upd("KB5127648", 3), Upd("KB5044293", 5));
        using var _t = t;
        cache.Save(Golden());
        var g = Fx.Parse("kb5044293"); g.Kb = "KB5044293"; cache.Save(g);
        var rows = await scan.ListCachedAsync(false, default);
        Assert.True(rows.Single(r => r.Update.Kb == "KB5127648").KeywordHits >= 5);
        Assert.Equal(0, rows.Single(r => r.Update.Kb == "KB5044293").KeywordHits);
    }
}
