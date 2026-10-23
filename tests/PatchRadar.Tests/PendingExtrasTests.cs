using PatchRadar.Core;
using PatchRadar.Export;
using PatchRadar.Scan;
using PatchRadar.State;
using PatchRadar.Updates;

namespace PatchRadar.Tests;

public class RestartAndErrorTests
{
    [Fact]
    public void Restart_PendingWhenEitherKeyExists()
    {
        Assert.False(RegistryRestartDetector.Check(_ => false));
        Assert.True(RegistryRestartDetector.Check(k => k.EndsWith("RebootRequired")));
        Assert.True(RegistryRestartDetector.Check(k => k.EndsWith("RebootPending")));
    }

    [Theory]
    [InlineData(unchecked((int)0x800F0922), "System Reserved")]
    [InlineData(unchecked((int)0x80070BC9), "restart")]
    public void KnownErrorCodes_AreExplained(int hr, string fragment) =>
        Assert.Contains(fragment, WuErrors.Describe(hr), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void UnknownErrorCode_StillShowsTheCode() =>
        Assert.StartsWith("0x80041234", WuErrors.Describe(unchecked((int)0x80041234)));

    [Fact]
    public void FailedInstall_KeepsItsErrorCode_ButInstalledDoesNot()
    {
        var failed = UpdateMerger.FromWua(new WuaEntry("Update (KB1234567)", DateTimeOffset.UtcNow, 4, 1, Array.Empty<string>(), null, unchecked((int)0x800F0922)))!;
        var ok = UpdateMerger.FromWua(new WuaEntry("Update (KB1234568)", DateTimeOffset.UtcNow, 2, 1, Array.Empty<string>(), null, 0))!;
        Assert.Equal(UpdateState.Failed, failed.State);
        Assert.Equal(unchecked((int)0x800F0922), failed.HResult);
        Assert.Equal(0, ok.HResult);
        var row = new UpdateRow(failed, null, SeverityEngine.Default().Evaluate(null));
        Assert.Contains("0x800F0922", row.FailureText);
    }
}

public class DriverInfoTests
{
    private static InstalledUpdate Drv(string title, UpdateState state = UpdateState.Installed) =>
        new("WU-1", title, DateTimeOffset.UtcNow, state, UpdateCategory.Driver);

    [Fact]
    public void Parse_SplitsNameAndVersion()
    {
        var p = DriverInfo.Parse("Intel Corporation Bluetooth Driver Update (24.70.0.4)");
        Assert.Equal("Intel Corporation Bluetooth Driver Update", p!.Value.Name);
        Assert.Equal(new Version(24, 70, 0, 4), p.Value.Version);
        Assert.Null(DriverInfo.Parse("No version here"));
    }

    [Theory]
    [InlineData("Dev Driver Update (24.70.0.4)", "Dev Driver Update (24.60.0.2)", "Newer than")]
    [InlineData("Dev Driver Update (25.0.0.1)", "Dev Driver Update (24.60.0.2)", "Major version change")]
    [InlineData("Dev Driver Update (24.50.0.1)", "Dev Driver Update (24.60.0.2)", "Older than")]
    [InlineData("Dev Driver Update (24.60.0.2)", "Dev Driver Update (24.60.0.2)", "Same version")]
    [InlineData("Dev Driver Update (24.60.0.2)", "Other Driver Update (1.0.0.0)", "No earlier version")]
    public void Compare_DescribesTheChange(string pending, string installed, string expected) =>
        Assert.StartsWith(expected, DriverInfo.Compare(Drv(pending, UpdateState.Pending), new[] { Drv(installed) }));
}

public class AdviceAndBriefingTests
{
    private static UpdateRow Pending(string fixture, string kb) =>
        Rows.FromFixture(fixture, kb, "Pending update") with { };

    private static UpdateRow AsPending(UpdateRow r) => r with { Update = r.Update with { State = UpdateState.Pending } };

    [Fact]
    public void Advice_OnlyForPending()
    {
        Assert.Null(DeferAdvice.For(Rows.FromFixture("kb5127648", "KB5127648", "Installed one")));
    }

    [Fact]
    public void Advice_RedWithUnresolvedIssue_SaysDefer()
    {
        var r = AsPending(Rows.FromFixture("kb5127648", "KB5127648", "2026-10 Cumulative Update"));
        Assert.Equal(Severity.Red, r.Severity);
        Assert.NotNull(DeferAdvice.For(r));
    }

    [Fact]
    public void Advice_UnreadArticle_SaysScanFirst()
    {
        var r = AsPending(Rows.Grey("KB1111111", "Not read yet"));
        Assert.Contains("Run Scan now", DeferAdvice.For(r));
    }

    [Fact]
    public void Advice_NoKbDriver_NullUnlessDowngrade()
    {
        var drv = new InstalledUpdate("WU-1", "Dev Driver (1.0.0.0)", DateTimeOffset.UtcNow, UpdateState.Pending, UpdateCategory.Driver);
        var row = new UpdateRow(drv, null, SeverityEngine.Default().Evaluate(null));
        Assert.Null(DeferAdvice.For(row));
        Assert.StartsWith("Check before installing", DeferAdvice.For(row with { DriverNote = "Older than the version installed here (2.0)." }));
    }

    [Fact]
    public void Briefing_ListsRiskiestFirst_NamesIgnored_AndNotesRestart()
    {
        var red = AsPending(Rows.FromFixture("kb5127648", "KB5127648", "2026-10 Cumulative Update"));
        var grey = AsPending(Rows.Grey("KB2222222", "Unread update"));
        var ignored = AsPending(Rows.Grey("KB3333333", "Ignored update")) with { Ignored = true };
        var md = Briefing.Render(new[] { grey, ignored, red }, restartPending: true, new DateTime(2026, 10, 23, 9, 0, 0), "PC01");

        Assert.Contains("# Pending Windows updates on PC01", md);
        Assert.Contains("restart is already pending", md);
        Assert.True(md.IndexOf("KB5127648", StringComparison.Ordinal) < md.IndexOf("KB2222222", StringComparison.Ordinal));
        Assert.Contains("## Ignored", md);
        Assert.Contains("KB3333333", md.Split("## Ignored")[1]);
        Assert.DoesNotContain("## [GREY] KB3333333", md);
    }

    [Fact]
    public void Briefing_Empty_SaysNothingWaiting() =>
        Assert.Contains("Nothing is waiting", Briefing.Render(Array.Empty<UpdateRow>(), false, DateTime.Now, "PC01"));
}

public class IgnoreAndSinceTests
{
    [Fact]
    public void IgnoreStore_PersistsAcrossInstances()
    {
        using var t = new TempDir();
        var state = new StateStore(t.File("state.json"));
        new IgnoreStore(state).Set("KB1", true);
        Assert.True(new IgnoreStore(state).IsIgnored("kb1"));
        new IgnoreStore(state).Set("KB1", false);
        Assert.False(new IgnoreStore(state).IsIgnored("KB1"));
    }

    [Fact]
    public void IgnoredRows_DoNotColourTheIcon()
    {
        var red = Rows.FromFixture("kb5127648", "KB5127648", "2026-10 Cumulative Update");
        Assert.Equal(Severity.Red, new ScanResult(new[] { red }, DateTime.Now, 0, 0).Worst());
        Assert.Equal(Severity.Grey, new ScanResult(new[] { red with { Ignored = true } }, DateTime.Now, 0, 0).Worst());
    }

    [Fact]
    public void Since_FiltersInstalled_ButNeverPending()
    {
        var old = Rows.Grey("KB1", "Old", daysAgo: 20);
        var recent = Rows.Grey("KB2", "Recent", daysAgo: 1);
        var pending = Rows.Grey("KB3", "Pending", daysAgo: 20) with { };
        pending = pending with { Update = pending.Update with { State = UpdateState.Pending } };

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var kept = UpdateFilter.Apply(new[] { old, recent, pending }, new UpdateFilterCriteria { Since = since }).Select(r => r.Update.Kb);
        Assert.Equal(new[] { "KB2", "KB3" }, kept.OrderBy(k => k));
    }
}

public class BriefingCountTests
{
    [Fact]
    public void Briefing_ListsEveryNoKbDriver()
    {
        UpdateRow Drv(string key, string title) =>
            new(new InstalledUpdate(key, title, DateTimeOffset.UtcNow, UpdateState.Pending, UpdateCategory.Driver), null, SeverityEngine.Default().Evaluate(null));
        var rows = new[] { Drv("WU-1", "A Driver (1.0.0.0)"), Drv("WU-2", "B Driver (1.0.0.0)"), Drv("WU-3", "C Driver (1.0.0.0)"), Drv("WU-4", "D Driver (1.0.0.0)") };
        var md = Briefing.Render(rows, false, DateTime.Now, "PC");
        Assert.Equal(4, md.Split('\n').Count(l => l.StartsWith("## [")));
        Assert.Contains("4 updates waiting", md);
    }
}

public class UptimeTests
{
    [Theory]
    [InlineData(0.01, "under an hour")]
    [InlineData(0.05, "1 hour")]
    [InlineData(0.5, "12 hours")]
    [InlineData(1.2, "1 day")]
    [InlineData(17.9, "17 days")]
    public void Describe_UsesTheLargestWholeUnit(double days, string expected) =>
        Assert.Equal(expected, PatchRadar.Infra.Uptime.Describe(TimeSpan.FromDays(days)));

    [Fact]
    public void IsLong_ZeroTurnsItOff()
    {
        Assert.False(PatchRadar.Infra.Uptime.IsLong(TimeSpan.FromDays(100), 0));
        Assert.True(PatchRadar.Infra.Uptime.IsLong(TimeSpan.FromDays(14), 14));
        Assert.False(PatchRadar.Infra.Uptime.IsLong(TimeSpan.FromDays(13.9), 14));
    }
}

public class PendingSeenTests
{
    [Fact]
    public void NewPending_IsUnseen_UntilMarked_AndPersists()
    {
        using var t = new TempDir();
        var state = new StateStore(t.File("state.json"));
        var seen = new PendingSeenStore(state);
        Assert.False(seen.IsSeen("KB1"));
        Assert.True(seen.MarkSeen(new[] { "KB1", "WU-2" }));
        Assert.False(seen.MarkSeen(new[] { "KB1", "WU-2" }));          // nothing new: no change
        Assert.True(new PendingSeenStore(state).IsSeen("wu-2"));       // survives a restart
    }

    [Fact]
    public void UpdateThatLeavesAndReturns_IsFlaggedAgain()
    {
        using var t = new TempDir();
        var seen = new PendingSeenStore(new StateStore(t.File("state.json")));
        seen.MarkSeen(new[] { "KB1" });
        seen.MarkSeen(Array.Empty<string>());                          // installed, so no longer pending
        Assert.False(seen.IsSeen("KB1"));
    }
}

public class RebootReminderTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static PatchRadar.Settings.RebootReminderSettings S(bool on = true, int after = 14, int repeat = 3) =>
        new() { Enabled = on, AfterDays = after, RepeatDays = repeat };

    [Fact]
    public void NotDue_WhenDisabled_OrUptimeBelowThreshold()
    {
        Assert.False(RebootReminder.IsDue(S(on: false), TimeSpan.FromDays(30), Now, null));
        Assert.False(RebootReminder.IsDue(S(), TimeSpan.FromDays(13.9), Now, null));
    }

    [Fact]
    public void Due_FirstTimeOverThreshold() =>
        Assert.True(RebootReminder.IsDue(S(), TimeSpan.FromDays(14), Now, null));

    [Fact]
    public void Repeats_OnlyAfterTheRepeatInterval()
    {
        var up = TimeSpan.FromDays(20);
        Assert.False(RebootReminder.IsDue(S(repeat: 3), up, Now, Now.AddDays(-2)));
        Assert.True(RebootReminder.IsDue(S(repeat: 3), up, Now, Now.AddDays(-3)));
    }

    [Fact]
    public void RepeatZero_RemindsOncePerStartup_ThenAgainAfterRestart()
    {
        var up = TimeSpan.FromDays(20);
        Assert.False(RebootReminder.IsDue(S(repeat: 0), up, Now, Now.AddDays(-10)));   // already reminded this start-up
        Assert.True(RebootReminder.IsDue(S(repeat: 0), up, Now, Now.AddDays(-40)));    // that reminder was before the last restart
    }

    [Fact]
    public void Servers_AreOffByDefault_AndUseTheirOwnThreshold()
    {
        var s = S();
        Assert.False(RebootReminder.IsDue(s, TimeSpan.FromDays(100), Now, null, isServer: true));   // not opted in
        s.RemindOnServers = true;
        Assert.False(RebootReminder.IsDue(s, TimeSpan.FromDays(30), Now, null, isServer: true));    // below the 45-day server threshold
        Assert.True(RebootReminder.IsDue(s, TimeSpan.FromDays(45), Now, null, isServer: true));
        Assert.True(RebootReminder.IsDue(s, TimeSpan.FromDays(14), Now, null, isServer: false));    // clients keep the 14-day one
    }

    [Fact]
    public void TryTake_RecordsSoItDoesNotRepeatImmediately()
    {
        using var t = new TempDir();
        var r = new RebootReminder(new StateStore(t.File("state.json")), () => Now);
        Assert.True(r.TryTake(S(), TimeSpan.FromDays(20)));
        Assert.False(r.TryTake(S(), TimeSpan.FromDays(20)));
    }

    [Fact]
    public void SettingsValidation_ResetsNonsense()
    {
        var s = new PatchRadar.Settings.AppSettings();
        s.RebootReminder.AfterDays = 0; s.RebootReminder.RepeatDays = -1; s.RebootReminder.ServerAfterDays = 0;
        Assert.Equal(3, s.Validate().Count(w => w.Contains("rebootReminder")));
        Assert.Equal(14, s.RebootReminder.AfterDays);
        Assert.Equal(3, s.RebootReminder.RepeatDays);
        Assert.Equal(45, s.RebootReminder.ServerAfterDays);
    }
}

public class FontSizeTests
{
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(12, 1.0)]
    [InlineData(10, 10 / 12.0)]
    [InlineData(18, 1.5)]
    public void Factor_ScalesAgainstTheBaseSize(int size, double expected) =>
        Assert.Equal(expected, PatchRadar.Views.UiScale.Factor(size), 6);

    [Theory]
    [InlineData(0, 0)]      // default stays default
    [InlineData(10, 10)]
    [InlineData(5, 0)]      // too small: reset
    [InlineData(40, 0)]     // too big: reset
    public void Validate_KeepsSaneSizesAndResetsTheRest(int input, int expected)
    {
        var s = new PatchRadar.Settings.AppSettings();
        s.Appearance.FontSize = input;
        s.Validate();
        Assert.Equal(expected, s.Appearance.FontSize);
    }
}
