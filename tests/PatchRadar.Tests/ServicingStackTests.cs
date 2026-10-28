using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;

namespace PatchRadar.Tests;

public class ServicingStackTests
{
    [Fact]
    public void RealArticleNamesItsServicingStackUpdate()
    {
        var d = Fx.Parse("kb5127648");
        Assert.Equal("KB5127647", ServicingStackLinker.IncludedSsu(d.RawText));
    }

    [Fact]
    public void FindsTheUpdateAnSsuCameWith()
    {
        var d = Fx.Parse("kb5127648");
        var parent = ServicingStackLinker.ParentOf("KB5127647", new[] { ("KB5000000", (string?)"nothing here"), ("KB5127648", d.RawText) });
        Assert.Equal("KB5127648", parent);
    }

    [Fact]
    public void NoParentWhenNoArticleMentionsIt() =>
        Assert.Null(ServicingStackLinker.ParentOf("KB5127647", new[] { ("KB5127648", (string?)"Includes KB1 only"), ("KB2", null) }));

    private static UpdateRow Ssu(KbStatus status, string? parent)
    {
        var u = new InstalledUpdate("KB5127647", "2026-10 Servicing Stack Update for Windows 11", DateTimeOffset.UtcNow, UpdateState.Installed, UpdateCategory.ServicingStack);
        var d = new KbData { Kb = u.Kb, Status = status };
        return new UpdateRow(u, d, SeverityEngine.Default().Evaluate(d)) { SsuParentKb = parent };
    }

    [Fact]
    public void Missing_ssu_article_points_to_the_update_it_came_with() =>
        Assert.Equal("https://support.microsoft.com/help/5127648", UrlGuard.ArticleUrl(Ssu(KbStatus.NotFound, "KB5127648")));

    [Fact]
    public void Missing_ssu_article_with_unknown_parent_points_to_servicing_stack_page() =>
        Assert.Equal(UrlGuard.ServicingStackUrl, UrlGuard.ArticleUrl(Ssu(KbStatus.NotFound, null)));

    [Fact]
    public void Working_ssu_article_keeps_its_own_link() =>
        Assert.Equal("https://support.microsoft.com/help/5127647", UrlGuard.ArticleUrl(Ssu(KbStatus.Ok, "KB5127648")));

    [Fact]
    public void SsuPageIsAnAllowedMicrosoftLink() => Assert.True(UrlGuard.IsSafe(UrlGuard.ServicingStackUrl));
}

public class IncludedAndComponentsTests
{
    [Fact]
    public void ReadsPreviouslyReleasedUpdates()
    {
        var d = Fx.Parse("kb5127648");
        Assert.Equal(new[] { "KB5124008", "KB5129195", "KB5124010" }, d.IncludedUpdates.Select(u => u.Kb));
        Assert.Contains("Out-of-band", d.IncludedUpdates[1].Text);
    }

    [Fact]
    public void ReadsComponentsUpdates()
    {
        var d = Fx.Parse("kb5127648");
        Assert.Contains(d.Components, c => c.Name == "Servicing stack update" && c.Text.Contains("KB5127647"));
        Assert.Contains(d.Components, c => c.Name == "AI components");
    }

    [Fact]
    public void PriorUpdateLinksAreStillNotImprovements() =>
        Assert.DoesNotContain(Fx.Parse("kb5127648").Improvements, i => i.Text.StartsWith("September 8, 2026"));
}
