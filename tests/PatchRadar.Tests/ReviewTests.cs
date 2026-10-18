using PatchRadar.Core;
using PatchRadar.Export;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.State;
using PatchRadar.Updates;

namespace PatchRadar.Tests;

internal sealed class FakeHistory(params InstalledUpdate[] list) : IUpdateHistoryProvider
{
    public Task<IReadOnlyList<InstalledUpdate>> GetAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstalledUpdate>>(list);
}

public class ReviewStoreTests
{
    private static (StateStore state, ReviewStore reviews, TempDir dir) Make()
    {
        var t = new TempDir();
        var state = new StateStore(Path.Combine(t.Path, "state.json"));
        return (state, new ReviewStore(state), t);
    }

    [Fact]
    public void Unknown_IsNone()
    {
        var (_, reviews, t) = Make();
        using var _t = t;
        Assert.Equal(ReviewStatus.None, reviews.Snapshot().StatusOf("KB1", "H"));
    }

    [Fact]
    public void Marked_IsReviewed_WhileHashMatches()
    {
        var (_, reviews, t) = Make();
        using var _t = t;
        reviews.Mark("KB1", "H1");
        Assert.Equal(ReviewStatus.Reviewed, reviews.Snapshot().StatusOf("KB1", "H1"));
    }

    [Fact]
    public void HashChange_MeansChangedSinceReviewed_UntilReMarked()
    {
        var (_, reviews, t) = Make();
        using var _t = t;
        reviews.Mark("KB1", "H1");
        Assert.Equal(ReviewStatus.ChangedSinceReviewed, reviews.Snapshot().StatusOf("KB1", "H2"));
        reviews.Mark("KB1", "H2");
        Assert.Equal(ReviewStatus.Reviewed, reviews.Snapshot().StatusOf("KB1", "H2"));
    }

    [Fact]
    public void FetchFailed_NoHash_KeepsShowingReviewed()
    {
        var (_, reviews, t) = Make();
        using var _t = t;
        reviews.Mark("KB1", "H1");
        Assert.Equal(ReviewStatus.Reviewed, reviews.Snapshot().StatusOf("KB1", null));
    }

    [Fact]
    public void Unmark_Clears()
    {
        var (_, reviews, t) = Make();
        using var _t = t;
        reviews.Mark("KB1", "H1");
        reviews.Unmark("KB1");
        Assert.Equal(ReviewStatus.None, reviews.Snapshot().StatusOf("KB1", "H1"));
    }

    [Fact]
    public void SurvivesRestart_AndRecordsTime()
    {
        var (state, reviews, t) = Make();
        using var _t = t;
        var at = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);
        new ReviewStore(state, () => at).Mark("KB1", "H1");
        var again = new ReviewStore(new StateStore(Path.Combine(t.Path, "state.json"))).Snapshot();
        Assert.Equal(ReviewStatus.Reviewed, again.StatusOf("kb1", "H1"));     // KB ids are case-insensitive
        Assert.Equal(at, again.ReviewedUtc("KB1"));
    }

    [Fact]
    public void ReviewsAndToasts_ShareOneFile_WithoutOverwritingEachOther()
    {
        var (state, reviews, t) = Make();
        using var _t = t;
        var tracker = new ToastTracker(state);
        reviews.Mark("KB1", "H1");
        tracker.TakeNew(new[] { Rows.FromFixture("kb5127648", "KB5127648", "t") }, 30);
        reviews.Mark("KB2", "H2");
        var s = state.Load();
        Assert.Contains("KB1", s.Reviewed.Keys);
        Assert.Contains("KB2", s.Reviewed.Keys);
        Assert.Contains("KB5127648", s.Notified.Keys);
    }
}

public class ScanServiceReviewTests
{
    private static InstalledUpdate Upd() =>
        new("KB5044281", "2024-10 Cumulative Update", DateTimeOffset.UtcNow.AddDays(-3), UpdateState.Installed, UpdateCategory.Cumulative);

    private static (ScanService scan, KbCache cache, TempDir dir) Make()
    {
        var t = new TempDir();
        var cache = new KbCache(Path.Combine(t.Path, "kb"));
        var fetcher = new KbFetcher(Array.Empty<IKbSource>(), cache);
        var reviews = new ReviewStore(new StateStore(Path.Combine(t.Path, "state.json")));
        var scan = new ScanService(new FakeHistory(Upd()), fetcher, () => new AppSettings(), null, reviews);
        return (scan, cache, t);
    }

    private static async Task<UpdateRow> Row(ScanService scan) => (await scan.ListCachedAsync(false, default)).Single();

    [Fact]
    public async Task EditedKnownIssueText_ClearsReviewedFlag_ShowsChanged()
    {
        var (scan, cache, t) = Make();
        using var _t = t;
        var kb = Fx.Parse("kb5044281");
        Assert.NotEmpty(kb.KnownIssues);
        cache.Save(kb);

        var row = await Row(scan);
        Assert.Equal(ReviewStatus.None, row.Review);

        row = scan.SetReviewed(row, true);
        Assert.Equal(ReviewStatus.Reviewed, row.Review);
        Assert.Equal(ReviewStatus.Reviewed, (await Row(scan)).Review);           // persisted, not just in memory

        // Microsoft edits the known-issue text; the next fetch stores new content with a new hash.
        kb.KnownIssues[0].Symptom += " Microsoft has updated this guidance.";
        kb.ContentHash = KbParser.Hash(kb);
        cache.Save(kb);

        var after = await Row(scan);
        Assert.Equal(ReviewStatus.ChangedSinceReviewed, after.Review);

        after = scan.SetReviewed(after, true);                                   // read it again, re-review
        Assert.Equal(ReviewStatus.Reviewed, (await Row(scan)).Review);
    }

    [Fact]
    public async Task CannotReview_WhenThereIsNoKbData()
    {
        var (scan, _, t) = Make();
        using var _t = t;
        var row = scan.SetReviewed(await Row(scan), true);
        Assert.Equal(ReviewStatus.None, row.Review);
    }

    [Fact]
    public async Task Unreview_Works()
    {
        var (scan, cache, t) = Make();
        using var _t = t;
        cache.Save(Fx.Parse("kb5044281"));
        var row = scan.SetReviewed(await Row(scan), true);
        row = scan.SetReviewed(row, false);
        Assert.Equal(ReviewStatus.None, row.Review);
        Assert.Equal(ReviewStatus.None, (await Row(scan)).Review);
    }

    [Fact]
    public async Task ReviewedRow_StillCountsInSeverity()
    {
        var (scan, cache, t) = Make();
        using var _t = t;
        cache.Save(Fx.Parse("kb5044281"));
        var before = (await Row(scan)).Severity;
        var after = scan.SetReviewed(await Row(scan), true).Severity;
        Assert.Equal(before, after);          // reviewing is a note to self, not a verdict
    }
}

public class ReviewFilterAndExportTests
{
    [Fact]
    public void Filter_ByReviewState()
    {
        var a = Rows.FromFixture("kb5127648", "KB5127648", "a") with { Review = ReviewStatus.Reviewed };
        var b = Rows.FromFixture("kb5044293", "KB5044293", "b") with { Review = ReviewStatus.ChangedSinceReviewed };
        var c = Rows.FromFixture("kb5044281", "KB5044281", "c");
        var all = new[] { a, b, c };
        Assert.Equal(new[] { "KB5127648" }, UpdateFilter.Apply(all, new UpdateFilterCriteria { Review = ReviewStatus.Reviewed }).Select(r => r.Update.Kb));
        Assert.Equal(new[] { "KB5044293" }, UpdateFilter.Apply(all, new UpdateFilterCriteria { Review = ReviewStatus.ChangedSinceReviewed }).Select(r => r.Update.Kb));
        Assert.Equal(new[] { "KB5044281" }, UpdateFilter.Apply(all, new UpdateFilterCriteria { Review = ReviewStatus.None }).Select(r => r.Update.Kb));
    }

    [Fact]
    public void Exports_CarryReviewState()
    {
        var r = Rows.FromFixture("kb5127648", "KB5127648", "a") with { Review = ReviewStatus.ChangedSinceReviewed };
        Assert.EndsWith("ChangedSinceReviewed", CsvExporter.Render(new[] { r }).Split("\r\n")[1]);
        Assert.Contains("\"review\": \"ChangedSinceReviewed\"", JsonExporter.Render(new[] { r }));
    }
}
