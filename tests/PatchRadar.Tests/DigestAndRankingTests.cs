using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Scan;
using PatchRadar.State;

namespace PatchRadar.Tests;

public class DigestTests
{
    private static readonly DateTime Since = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly SeverityEngine Engine = SeverityEngine.Default();

    private static UpdateRow Row(KbData kb, DateTimeOffset installed, ReviewStatus review = ReviewStatus.None)
    {
        var u = new InstalledUpdate(kb.Kb, "t " + kb.Kb, installed, UpdateState.Installed, UpdateCategory.Cumulative);
        return new UpdateRow(u, kb, Engine.Evaluate(kb)) { Review = review };
    }

    private static KbData Article(string kb, params KbIssue[] issues)
    {
        var d = new KbData { Kb = kb, Status = KbStatus.Ok, KnownIssues = issues.ToList() };
        d.ContentHash = KbParser.Hash(d);
        return d;
    }

    [Fact]
    public void NewUpdate_AfterTheLastLook_IsListed_OldOnesAreNot()
    {
        var fresh = Row(Article("KB1"), new DateTimeOffset(Since.AddDays(2), TimeSpan.Zero));
        var old = Row(Article("KB2"), new DateTimeOffset(Since.AddDays(-2), TimeSpan.Zero));
        var items = DigestBuilder.Build(new[] { fresh, old }, Since, Engine);
        var i = Assert.Single(items);
        Assert.Equal((DigestKind.NewUpdate, "KB1"), (i.Kind, i.Row.Update.Kb));
    }

    [Fact]
    public void EditedArticle_IsListed_WithWhatChanged()
    {
        var before = Article("KB1", new KbIssue { Title = "A", Symptom = "s" });
        var now = Article("KB1", new KbIssue { Title = "A", Symptom = "s", Workaround = "new workaround" });
        now.Previous = KbSnapshot.Of(before, Since.AddDays(-3));
        now.ChangedUtc = Since.AddDays(1);
        var items = DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero)) }, Since, Engine);
        var c = Assert.Single(items, x => x.Kind == DigestKind.ArticleChanged);
        Assert.Contains("1 changed", c.Text);
        Assert.NotNull(c.Diff);
    }

    [Fact]
    public void ChangeBeforeTheLastLook_IsNotListed()
    {
        var now = Article("KB1", new KbIssue { Title = "A", Symptom = "changed" });
        now.Previous = KbSnapshot.Of(Article("KB1", new KbIssue { Title = "A", Symptom = "s" }), Since.AddDays(-9));
        now.ChangedUtc = Since.AddDays(-1);
        Assert.Empty(DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero)) }, Since, Engine));
    }

    [Fact]
    public void BecomingRiskier_IsCalledOut()
    {
        var before = Article("KB1");                                                          // Green
        var now = Article("KB1", new KbIssue { Title = "Wi-Fi 802.1X fails", Symptom = "RADIUS authentication breaks" });   // Red
        now.Previous = KbSnapshot.Of(before, Since.AddDays(-3));
        now.ChangedUtc = Since.AddDays(1);
        var items = DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero)) }, Since, Engine);
        var risk = Assert.Single(items, x => x.Kind == DigestKind.BecameRisky);
        Assert.Contains("Green to Red", risk.Text);
    }

    [Fact]
    public void BecomingSaferOrSame_IsNotCalledRisky()
    {
        var before = Article("KB1", new KbIssue { Title = "Wi-Fi fails", Symptom = "RADIUS authentication breaks" });
        var now = Article("KB1");
        now.Previous = KbSnapshot.Of(before, Since.AddDays(-3));
        now.ChangedUtc = Since.AddDays(1);
        Assert.DoesNotContain(DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero)) }, Since, Engine), x => x.Kind == DigestKind.BecameRisky);
    }

    [Fact]
    public void NewlyNamedFix_IsCalledOut()
    {
        var before = Article("KB1", new KbIssue { Title = "Audio fails", Symptom = "no sound" });
        var after = new KbIssue { Title = "Audio fails", Symptom = "no sound", ResolvedInKb = "KB9", OutOfBandFix = true };
        var now = Article("KB1", after);
        now.Previous = KbSnapshot.Of(before, Since.AddDays(-3));
        now.ChangedUtc = Since.AddDays(1);
        var items = DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero)) }, Since, Engine);
        var fix = Assert.Single(items, x => x.Kind == DigestKind.FixNowNamed);
        Assert.Contains("KB9", fix.Text);
    }

    [Fact]
    public void ReviewedThenChanged_IsCalledOut()
    {
        var now = Article("KB1", new KbIssue { Title = "A", Symptom = "s2" });
        now.Previous = KbSnapshot.Of(Article("KB1", new KbIssue { Title = "A", Symptom = "s" }), Since.AddDays(-3));
        now.ChangedUtc = Since.AddDays(1);
        var items = DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero), ReviewStatus.ChangedSinceReviewed) }, Since, Engine);
        Assert.Contains(items, x => x.Kind == DigestKind.ReviewedChanged);
    }

    [Fact]
    public void ParserOnlyChange_ProducesNoDigestEntry()
    {
        var a = Article("KB1", new KbIssue { Title = "A", Symptom = "s" });
        var now = Article("KB1", new KbIssue { Title = "A", Symptom = "s" });
        now.Previous = KbSnapshot.Of(a, Since.AddDays(-3));
        now.ChangedUtc = Since.AddDays(1);
        Assert.Empty(DigestBuilder.Build(new[] { Row(now, new DateTimeOffset(Since.AddDays(-30), TimeSpan.Zero)) }, Since, Engine));
    }

    [Fact]
    public void Newest_IsFirst()
    {
        var a = Row(Article("KB1"), new DateTimeOffset(Since.AddDays(1), TimeSpan.Zero));
        var b = Row(Article("KB2"), new DateTimeOffset(Since.AddDays(3), TimeSpan.Zero));
        Assert.Equal("KB2", DigestBuilder.Build(new[] { a, b }, Since, Engine)[0].Row.Update.Kb);
    }

    [Fact]
    public void DigestStore_DefaultsToAWeekAgo_ThenRemembersMarkSeen_AcrossRestarts()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "state.json");
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var store = new DigestStore(new StateStore(path), () => now);
        Assert.Equal(now.AddDays(-7), store.LastSeenUtc);
        store.MarkSeen();
        Assert.Equal(now, new DigestStore(new StateStore(path), () => now.AddDays(5)).LastSeenUtc);
    }

    [Fact]
    public void Label_IsHumanReadable() => Assert.Equal("Became riskier", DigestBuilder.Label(DigestKind.BecameRisky));
}

public class RankingAndFixFilterTests
{
    private static UpdateRow Row(string kb, string fixture, int daysAgo, IReadOnlyList<FixInfo>? fixes = null)
    {
        var r = Rows.FromFixture(fixture, kb, "t " + kb, daysAgo);
        return fixes is null ? r : r with { Fixes = fixes };
    }

    [Fact]
    public void RankByKeywordHits_PutsTheMostMatchesFirst()
    {
        var many = Row("KB5127648", "kb5127648", 20);          // many keywords, oldest
        var few = Row("KB5055523", "kb5055523", 2);
        var none = Row("KB5044293", "kb5044293", 1);           // no keywords, newest
        var ranked = UpdateFilter.RankByKeywordHits(new[] { none, few, many });
        Assert.Equal("KB5127648", ranked[0].Update.Kb);
        Assert.Equal("KB5044293", ranked[^1].Update.Kb);
        Assert.True(ranked[0].KeywordHits >= ranked[1].KeywordHits);
        Assert.True(ranked[1].KeywordHits >= ranked[2].KeywordHits);
    }

    [Fact]
    public void RankTies_GoToTheNewerUpdate()
    {
        var older = Row("KB5044293", "kb5044293", 9);
        var newer = Row("KB5044033", "kb5044033", 1);
        Assert.Equal(older.KeywordHits, newer.KeywordHits);
        Assert.Equal("KB5044033", UpdateFilter.RankByKeywordHits(new[] { older, newer })[0].Update.Kb);
    }

    [Fact]
    public void FixNotInstalledFilter_KeepsOnlyThose()
    {
        var withFix = Row("KB5127648", "kb5127648", 3, new[] { new FixInfo("KB5129195", false, true, true, "USB", false) });
        var installedFix = Row("KB5055523", "kb5055523", 3, new[] { new FixInfo("KB5055627", true, false, false, "Hello", false) });
        var plain = Row("KB5044293", "kb5044293", 3);
        var got = UpdateFilter.Apply(new[] { withFix, installedFix, plain }, new UpdateFilterCriteria { FixNotInstalledOnly = true });
        Assert.Equal("KB5127648", Assert.Single(got).Update.Kb);
        Assert.Equal(3, UpdateFilter.Apply(new[] { withFix, installedFix, plain }, new UpdateFilterCriteria()).Count);
    }

    [Fact]
    public void ExportsCarryHitsAndFixes()
    {
        var r = Row("KB5127648", "kb5127648", 3, new[] { new FixInfo("KB5129195", false, true, true, "USB", false) });
        var csv = PatchRadar.Export.CsvExporter.Render(new[] { r });
        Assert.Contains("Keyword hits", csv.Split("\r\n")[0]);
        Assert.Contains("KB5129195", csv.Split("\r\n")[1]);
        Assert.Contains("\"fixesNotInstalled\"", PatchRadar.Export.JsonExporter.Render(new[] { r }));
    }
}
