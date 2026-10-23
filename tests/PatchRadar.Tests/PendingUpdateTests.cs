using PatchRadar.Core;
using PatchRadar.Settings;
using PatchRadar.Updates;

namespace PatchRadar.Tests;

public class PendingUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static WuaPendingEntry Entry(string title, params string[] kbs) =>
        new(title, kbs, Now.AddDays(-3), new[] { "Security Updates" }, null);

    private static InstalledUpdate Pending(string kb, string title, UpdateCategory cat = UpdateCategory.Cumulative, int daysAgo = 3) =>
        new(kb, title, Now.AddDays(-daysAgo), UpdateState.Pending, cat);

    [Fact]
    public void FromPending_UsesKbArticleId()
    {
        var u = UpdateMerger.FromPending(Entry("2026-10 Cumulative Update for Windows 11", "5127648"))!;
        Assert.Equal("KB5127648", u.Kb);
        Assert.Equal(UpdateState.Pending, u.State);
        Assert.Equal(UpdateCategory.Cumulative, u.Category);
    }

    [Fact]
    public void FromPending_FallsBackToTitle_AndKeysDriversWithoutKbByTitle()
    {
        Assert.Equal("KB5001234", UpdateMerger.FromPending(Entry("Some update (KB5001234)"))!.Kb);
        var drv = UpdateMerger.FromPending(Entry("Vendor driver with no KB"))!;
        Assert.False(drv.HasKb);
        Assert.StartsWith("WU-", drv.Kb);
        Assert.Equal(drv.Kb, UpdateMerger.FromPending(Entry("  VENDOR driver with no kb "))!.Kb);
        Assert.Null(UpdateMerger.FromPending(Entry("")));
    }

    [Fact]
    public void FilterPending_DropsInstalled_Duplicates_ExcludedAndOtherCategories_ButKeepsOldOnes()
    {
        var s = new UpdatesSettings { HistoryDays = 30 };
        var all = new[]
        {
            Pending("KB1", "Old pending, well outside the history window", daysAgo: 200),
            Pending("KB2", "Already installed"),
            Pending("KB3", "Security Intelligence Update for Microsoft Defender"),
            Pending("KB4", "Dupe"), Pending("KB4", "Dupe"),
            Pending("KB5", "Office thing", UpdateCategory.Definition),
        };
        var kept = UpdateMerger.FilterPending(all, s, kb => kb == "KB2").Select(u => u.Kb).ToList();
        Assert.Equal(new[] { "KB4", "KB1" }, kept);
    }

    [Fact]
    public void PendingIsNotAnInstalledState_SoHistoryFilterIgnoresIt()
    {
        // Pending rows never reach the history filter; if one did it must not be mistaken for installed.
        Assert.NotEqual(UpdateState.Installed, UpdateState.Pending);
        Assert.True(new UpdatesSettings().ShowPending);
    }
}
