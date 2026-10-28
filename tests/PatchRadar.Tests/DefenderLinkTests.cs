using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Kb;
using PatchRadar.Scan;

namespace PatchRadar.Tests;

public class DefenderLinkTests
{
    private static UpdateRow Row(string kb, string title, KbStatus? status)
    {
        var u = new InstalledUpdate(kb, title, DateTimeOffset.UtcNow, UpdateState.Installed, UpdateCategory.Definition);
        var d = status is null ? null : new KbData { Kb = kb, Status = status.Value };
        return new UpdateRow(u, d, SeverityEngine.Default().Evaluate(d));
    }

    [Fact]
    public void DefenderUpdateWith404GoesToDefenderPage() =>
        Assert.Equal(UrlGuard.DefenderUpdatesUrl,
            UrlGuard.ArticleUrl(Row("KB2267602", "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602", KbStatus.NotFound)));

    [Fact]
    public void DefenderUpdateWithWorkingArticleKeepsKbLink() =>
        Assert.Equal("https://support.microsoft.com/help/2267602",
            UrlGuard.ArticleUrl(Row("KB2267602", "Security Intelligence Update for Microsoft Defender Antivirus", KbStatus.Ok)));

    [Fact]
    public void NonDefenderUpdateWith404KeepsKbLink() =>
        Assert.Equal("https://support.microsoft.com/help/5000001",
            UrlGuard.ArticleUrl(Row("KB5000001", "2026-10 Cumulative Update for Windows 11", KbStatus.NotFound)));

    [Fact]
    public void DefenderPageIsAnAllowedMicrosoftLink() => Assert.True(UrlGuard.IsSafe(UrlGuard.DefenderUpdatesUrl));
}
