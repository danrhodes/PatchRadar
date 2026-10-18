using PatchRadar.Core;
using PatchRadar.Scan;
using PatchRadar.Settings;
using PatchRadar.Uninstall;
using PatchRadar.Views;

namespace PatchRadar.Tests;

internal sealed class FakeCbs(params CbsPackage[] packages) : ICbsPackageSource
{
    public IReadOnlyList<CbsPackage> GetPackages() => packages;
}

internal static class Cbs
{
    public static CbsPackage Rollup(string version, int state = CbsPackage.Installed) =>
        new($"Package_for_RollupFix~31bf3856ad364e35~amd64~~{version}", null, true, state, Version.Parse(version), DateTime.UtcNow);

    public static CbsPackage Kb(string kb, int state = CbsPackage.Installed) =>
        new($"Package_for_{kb}~31bf3856ad364e35~amd64~~10.0.1.0", kb, false, state, new Version(10, 0, 1, 0), DateTime.UtcNow);
}

public class UninstallPlannerTests
{
    private static InstalledUpdate U(string kb, string title, UpdateCategory cat, UpdateState st = UpdateState.Installed) =>
        new(kb, title, DateTimeOffset.UtcNow.AddDays(-3), st, cat);

    private static readonly CbsPackage[] Packages =
    {
        Cbs.Rollup("26100.9457.1.0", 80),            // superseded
        Cbs.Rollup("26100.9550.1.28", 80),           // superseded
        Cbs.Rollup("26100.9711.1.30"),               // newest installed
        Cbs.Kb("KB5127694"),                         // standalone .NET Framework update
    };

    private static UninstallPlan Plan(InstalledUpdate u, params CbsPackage[] pk) =>
        new UninstallPlanner(new FakeCbs(pk.Length > 0 ? pk : Packages)).Plan(u);

    [Fact]
    public void NewestCumulativeUpdate_IsRemovedWithDism_ByPackageName()
    {
        var p = Plan(U("KB5127648", "2026-10 Security Update (KB5127648) (26300.9711)", UpdateCategory.Security));
        Assert.Equal(UninstallMethod.DismRemovePackage, p.Method);
        Assert.Equal("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9711.1.30", p.PackageName);
        Assert.Equal("dism.exe /online /remove-package /packagename:Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9711.1.30 /quiet /norestart", p.Command);
        Assert.True(p.IsSecurityUpdate);
        Assert.Equal("dism.exe", p.Executable);
    }

    [Fact]
    public void OlderCumulativeUpdate_IsNotOffered_AndSaysWhy()
    {
        var p = Plan(U("KB5124010", "2026-09 Preview Update (KB5124010) (26300.9550)", UpdateCategory.Other));
        Assert.Equal(UninstallMethod.None, p.Method);
        Assert.Contains("superseded", p.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CumulativeThatIsInstalledButNotNewest_IsNotOffered()
    {
        // two packages both installed: only the higher one may be removed
        var pk = new[] { Cbs.Rollup("26100.9550.1.28"), Cbs.Rollup("26100.9711.1.30") };
        var p = Plan(U("KB1111111", "2026-09 Cumulative Update (KB1111111) (26100.9550)", UpdateCategory.Cumulative), pk);
        Assert.Equal(UninstallMethod.None, p.Method);
        Assert.Contains("newest", p.Reason);
    }

    [Fact]
    public void StandaloneUpdateWithAPackage_UsesWusa()
    {
        var p = Plan(U("KB5127694", "2026-10 .NET Framework Security Update (KB5127694)", UpdateCategory.DotNet));
        Assert.Equal(UninstallMethod.Wusa, p.Method);
        Assert.Equal("wusa.exe /uninstall /kb:5127694 /quiet /norestart", p.Command);
        Assert.Equal("wusa.exe", p.Executable);
        Assert.Equal("/uninstall /kb:5127694 /quiet /norestart", p.Arguments);
    }

    [Fact]
    public void StandaloneUpdateWithoutAPackage_IsNotOffered() =>
        Assert.Equal(UninstallMethod.None, Plan(U("KB5999999", "Some update (KB5999999)", UpdateCategory.Other)).Method);

    [Theory]
    [InlineData("2026-10 Servicing Stack Update (KB5000001)", UpdateCategory.ServicingStack)]
    [InlineData("Servicing Stack Update for Windows (KB5000001)", UpdateCategory.Other)]
    public void ServicingStack_IsNeverOffered(string title, UpdateCategory cat)
    {
        var p = Plan(U("KB5000001", title, cat), Cbs.Kb("KB5000001"));      // even if a package exists
        Assert.Equal(UninstallMethod.None, p.Method);
        Assert.Contains("Servicing stack", p.Reason);
    }

    [Theory]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.1)", UpdateCategory.Definition)]
    [InlineData("Windows Malicious Software Removal Tool x64 - v5.146 (KB890830)", UpdateCategory.Other)]
    [InlineData("Update for Microsoft Defender Antivirus antimalware platform - KB4052623 (Version 4.18)", UpdateCategory.Other)]
    [InlineData("Update for Windows Security platform - KB5007651 (Version 10.0.29628.1000)", UpdateCategory.Other)]
    public void DefenderAndMsrt_AreNeverOffered(string title, UpdateCategory cat)
    {
        var kb = System.Text.RegularExpressions.Regex.Match(title, @"KB\d+").Value;
        Assert.Equal(UninstallMethod.None, Plan(U(kb, title, cat), Cbs.Kb(kb)).Method);
    }

    [Fact]
    public void Drivers_Office_AndModernDotNet_AreNotOffered()
    {
        Assert.Equal(UninstallMethod.None, Plan(U("KB5000002", "Intel - Net - 22.1.0 (KB5000002)", UpdateCategory.Driver), Cbs.Kb("KB5000002")).Method);
        Assert.Equal(UninstallMethod.None, Plan(U("KB5000003", "Update for Microsoft Office (KB5000003)", UpdateCategory.Office), Cbs.Kb("KB5000003")).Method);
        var dn = Plan(U("KB5130036", "2026-10 .NET 10.0.13 Security Update for x64 Client (KB5130036)", UpdateCategory.DotNet), Cbs.Kb("KB5130036"));
        Assert.Equal(UninstallMethod.None, dn.Method);
    }

    [Fact]
    public void NotInstalled_IsNotOffered() =>
        Assert.Equal(UninstallMethod.None, Plan(U("KB5127694", "x (KB5127694)", UpdateCategory.DotNet, UpdateState.Uninstalled)).Method);

    [Fact]
    public void StagedButNotInstalledPackage_IsNotOffered() =>
        Assert.Equal(UninstallMethod.None, Plan(U("KB5127694", "2026-10 .NET Framework Security Update (KB5127694)", UpdateCategory.DotNet), Cbs.Kb("KB5127694", state: 64)).Method);

    [Fact]
    public void CumulativeWithoutABuildInItsTitle_CannotBeMatched_SoIsNotOffered()
    {
        var p = Plan(U("KB5044281", "2024-10 Cumulative Update for Microsoft server operating system (KB5044281)", UpdateCategory.Cumulative));
        Assert.Equal(UninstallMethod.None, p.Method);
    }

    [Theory]
    [InlineData("KB12")]
    [InlineData("KB5127694; calc")]
    [InlineData("../KB5127694")]
    [InlineData("")]
    public void MalformedKb_NeverProducesACommand(string kb)
    {
        var p = Plan(U(kb, "x", UpdateCategory.DotNet), Cbs.Kb("KB5127694"));
        Assert.Equal(UninstallMethod.None, p.Method);
        Assert.Equal("", p.Command);
    }

    [Fact]
    public void UnsafePackageName_NeverProducesACommand()
    {
        var bad = new CbsPackage("Package_for_RollupFix~x & calc.exe~~26100.9711.1.30", null, true, CbsPackage.Installed, new Version(26100, 9711, 1, 30), null);
        var p = Plan(U("KB5127648", "2026-10 Security Update (KB5127648) (26300.9711)", UpdateCategory.Security), bad);
        Assert.Equal(UninstallMethod.None, p.Method);
    }

    [Fact]
    public void CbsParsing_ReadsVersionTimeAndRevision()
    {
        var t = new DateTime(2026, 10, 14, 19, 54, 2, DateTimeKind.Utc).ToFileTimeUtc();
        var p = RegistryCbsPackageSource.Parse("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9711.1.30", null, true, 112, (int)(t >> 32), (int)(t & 0xFFFFFFFF));
        Assert.True(p.IsInstalled);
        Assert.Equal(9711, p.Revision);
        Assert.Equal(new DateTime(2026, 10, 14, 19, 54, 2, DateTimeKind.Utc), p.InstalledUtc);
    }

    [Fact]
    public void RealCbs_OnThisMachine_ReadsRollupPackages_WithoutElevation()
    {
        var pk = new RegistryCbsPackageSource().GetPackages();
        Assert.Contains(pk, p => p.IsRollup && p.Version is not null);
        Assert.Single(pk.Where(p => p.IsRollup && p.IsInstalled), p => true);      // exactly one installed rollup at a time
    }
}

public class UninstallResultTests
{
    [Theory]
    [InlineData(0, UninstallOutcome.Succeeded)]
    [InlineData(3010, UninstallOutcome.SucceededRestartRequired)]
    [InlineData(1641, UninstallOutcome.SucceededRestartRequired)]
    [InlineData(-2146498523, UninstallOutcome.Failed)]     // 0x800F0825 permanent package
    [InlineData(-2145124329, UninstallOutcome.Failed)]     // 0x80240017 not applicable
    [InlineData(5, UninstallOutcome.Failed)]
    [InlineData(87, UninstallOutcome.Failed)]
    [InlineData(12345, UninstallOutcome.Failed)]
    public void ExitCodes_MapToPlainOutcomes(int code, UninstallOutcome expected)
    {
        var r = UninstallResults.Map(code);
        Assert.Equal(expected, r.Outcome);
        Assert.NotEmpty(r.Message);
    }

    [Fact]
    public void DeclinedUac_IsCancelled_NotAFailure()
    {
        var r = UninstallResults.Map(null);
        Assert.Equal(UninstallOutcome.Cancelled, r.Outcome);
        Assert.Contains("nothing was changed", r.Message);
        Assert.False(r.Succeeded);
    }

    [Fact] public void PermanentPackage_ExplainsItself() => Assert.Contains("permanent", UninstallResults.Map(unchecked((int)0x800F0825)).Message);
}

public class PrivilegeTests
{
    [Theory]
    [InlineData(true, 2, true)]       // elevated administrator
    [InlineData(true, 1, true)]       // administrator with UAC off
    [InlineData(false, 3, true)]      // administrator on a filtered (limited) token: the normal case for a desktop app
    [InlineData(false, 1, false)]     // standard user
    [InlineData(false, 0, false)]     // unknown token type and not in the role
    public void AdminCapable(bool inRole, int tokenType, bool expected) =>
        Assert.Equal(expected, WindowsPrivilegeInfo.Classify(inRole, tokenType));

    [Fact]
    public void RealPrivilegeInfo_ReturnsAnAnswer()
    {
        var w = new WindowsPrivilegeInfo();
        _ = w.IsAdminCapable;
        Assert.Contains("\\", w.UserName);
    }
}

public class UninstallConfirmationTests
{
    [Theory]
    [InlineData("KB5127648", true)]
    [InlineData("kb5127648", true)]
    [InlineData(" 5127648 ", true)]
    [InlineData("KB5127649", false)]
    [InlineData("", false)]
    public void MustTypeTheKb(string typed, bool expected) =>
        Assert.Equal(expected, UninstallConfirmation.IsConfirmed(typed, "KB5127648", acknowledged: true));

    [Fact]
    public void TypingIsNotEnough_WithoutTheAcknowledgement() =>
        Assert.False(UninstallConfirmation.IsConfirmed("KB5127648", "KB5127648", acknowledged: false));

    private static UpdateRow Row(string fixture, string kb, string title, UpdateCategory cat) =>
        Rows.FromFixture(fixture, kb, title, 3, cat);

    [Fact]
    public void RedSecurityUpdate_GetsEveryWarning()
    {
        var row = Row("kb5127648", "KB5127648", "2026-10 Security Update (KB5127648) (26300.9711)", UpdateCategory.Security);
        var plan = new UninstallPlan(UninstallMethod.DismRemovePackage, "r", "KB5127648", "Package_x", true);
        var texts = UninstallConfirmation.Warnings(row, plan, @"CORP\dan").Select(w => w.Text).ToList();
        Assert.Contains(texts, t => t.Contains("SECURITY update") && t.Contains("exposed"));
        Assert.Contains(texts, t => t.Contains("PatchRadar flagged this update (Red)"));
        Assert.Contains(texts, t => t.Contains("restart"));
        Assert.Contains(texts, t => t.Contains("install it again automatically"));
        Assert.Contains(texts, t => t.Contains("permission prompt"));
        Assert.Contains(texts, t => t.Contains("recorded") && t.Contains(@"CORP\dan"));
        Assert.Contains(texts, t => t.Contains("newest cumulative update"));
        Assert.True(UninstallConfirmation.Warnings(row, plan, "u").First(w => w.Text.Contains("SECURITY")).Severe);
    }

    [Fact]
    public void UnflaggedUpdate_WarnsThatNoProblemWasFound()
    {
        var row = Row("kb5044293", "KB5044293", "x (KB5044293) (26100.1)", UpdateCategory.Other);
        var plan = new UninstallPlan(UninstallMethod.Wusa, "r", "KB5044293", "p", false);
        var w = UninstallConfirmation.Warnings(row, plan, "u");
        Assert.Contains(w, x => x.Text.Contains("NOT flagged") && x.Severe);
        Assert.DoesNotContain(w, x => x.Text.Contains("SECURITY"));
    }

    [Fact]
    public void AvailableFix_IsSuggestedInsteadOfRemoval()
    {
        var row = Row("kb5127648", "KB5127648", "t", UpdateCategory.Security) with
        {
            Fixes = new[] { new FixInfo("KB5129195", false, true, true, "USB audio", false) },
        };
        var w = UninstallConfirmation.Warnings(row, new UninstallPlan(UninstallMethod.Wusa, "r", "KB5127648", "p"), "u");
        Assert.Contains(w, x => x.Text.Contains("A fix is available") && x.Text.Contains("KB5129195"));
    }
}

public class UninstallServiceTests
{
    private sealed class FakePrivilege(bool admin, string user = @"CORP\dan") : IPrivilegeInfo
    {
        public bool IsAdminCapable => admin;
        public string UserName => user;
        public string MachineName => "PC01";
    }

    private sealed class FakeRunner(int? exit) : IElevatedRunner
    {
        public List<(string Exe, string Args)> Calls = new();
        public Func<Task<int?>>? Override;
        public Task<int?> RunAsync(string exe, string args, CancellationToken ct)
        {
            Calls.Add((exe, args));
            return Override is null ? Task.FromResult(exit) : Override();
        }
    }

    private static readonly InstalledUpdate Lcu =
        new("KB5127648", "2026-10 Security Update (KB5127648) (26300.9711)", DateTimeOffset.UtcNow.AddDays(-3), UpdateState.Installed, UpdateCategory.Security);

    private static (UninstallService svc, FakeRunner run, UninstallAudit audit, TempDir dir, AppSettings settings) Make(bool admin = true, bool enabled = true, int? exit = 0)
    {
        var t = new TempDir();
        var settings = new AppSettings();
        settings.Advanced.EnableUninstall = enabled;
        var run = new FakeRunner(exit);
        var audit = new UninstallAudit(Path.Combine(t.Path, "audit", "uninstall.jsonl"));
        var svc = new UninstallService(new UninstallPlanner(new FakeCbs(Cbs.Rollup("26100.9711.1.30"))), run, new FakePrivilege(admin), audit,
            () => settings, () => new[] { Lcu }, null, () => new DateTime(2026, 10, 18, 12, 30, 5, DateTimeKind.Utc));
        return (svc, run, audit, t, settings);
    }

    [Fact]
    public void FeatureOffByDefault() => Assert.False(new AppSettings().Advanced.EnableUninstall);

    [Fact]
    public void AdminWithFeatureOn_IsOfferedTheButton_ForARemovableUpdate()
    {
        var (svc, _, _, t, _) = Make();
        using var _t = t;
        Assert.True(svc.IsAvailable);
        Assert.NotNull(svc.PlanIfOffered(Lcu));
    }

    [Fact]
    public void StandardUser_NeverSeesIt_EvenWithTheFeatureOn()
    {
        var (svc, _, _, t, _) = Make(admin: false, enabled: true);
        using var _t = t;
        Assert.False(svc.IsAvailable);
        Assert.Null(svc.PlanIfOffered(Lcu));
    }

    [Fact]
    public void FeatureOff_NobodySeesIt_EvenAdmins()
    {
        var (svc, _, _, t, _) = Make(admin: true, enabled: false);
        using var _t = t;
        Assert.Null(svc.PlanIfOffered(Lcu));
    }

    [Fact]
    public void NotRemovable_NoButton()
    {
        var (svc, _, _, t, _) = Make();
        using var _t = t;
        var ssu = new InstalledUpdate("KB5000001", "2026-10 Servicing Stack Update (KB5000001)", DateTimeOffset.UtcNow, UpdateState.Installed, UpdateCategory.ServicingStack);
        Assert.Null(svc.PlanIfOffered(ssu));
    }

    [Fact]
    public async Task Execute_RunsTheExactCommand_AndRecordsWhoWhenAndOutcome()
    {
        var (svc, run, audit, t, _) = Make(exit: 3010);
        using var _t = t;

        var result = await svc.ExecuteAsync(Lcu, "  INC-4821 Wi-Fi 802.1X broke  ", default);

        Assert.Equal(UninstallOutcome.SucceededRestartRequired, result.Outcome);
        var call = Assert.Single(run.Calls);
        Assert.Equal("dism.exe", call.Exe);
        Assert.Equal("/online /remove-package /packagename:Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9711.1.30 /quiet /norestart", call.Args);

        var entries = audit.ReadAll();
        Assert.Equal(new[] { "Started", "SucceededRestartRequired" }, entries.Select(e => e.Stage));
        Assert.All(entries, e =>
        {
            Assert.Equal(@"CORP\dan", e.User);
            Assert.Equal("PC01", e.Machine);
            Assert.Equal("KB5127648", e.Kb);
            Assert.Equal("INC-4821 Wi-Fi 802.1X broke", e.Reason);
            Assert.Equal(new DateTime(2026, 10, 18, 12, 30, 5, DateTimeKind.Utc), e.TimestampUtc);
            Assert.Contains("2026-10-18", e.TimestampLocal);
            Assert.Equal("DismRemovePackage", e.Method);
        });
        Assert.Equal(entries[0].Id, entries[1].Id);                 // the two lines belong together
        Assert.Equal(3010, entries[1].ExitCode);
    }

    [Fact]
    public async Task DeclinedUac_IsRecordedAsCancelled()
    {
        var (svc, _, audit, t, _) = Make(exit: null);
        using var _t = t;
        var r = await svc.ExecuteAsync(Lcu, "", default);
        Assert.Equal(UninstallOutcome.Cancelled, r.Outcome);
        Assert.Equal("Cancelled", audit.ReadAll().Last().Stage);
    }

    [Fact]
    public async Task FailedRemoval_IsRecordedWithItsCode()
    {
        var (svc, _, audit, t, _) = Make(exit: unchecked((int)0x800F0825));
        using var _t = t;
        var r = await svc.ExecuteAsync(Lcu, "", default);
        Assert.Equal(UninstallOutcome.Failed, r.Outcome);
        var last = audit.ReadAll().Last();
        Assert.Equal("Failed", last.Stage);
        Assert.Contains("permanent", last.Message);
    }

    [Fact]
    public async Task RunnerThrows_IsContained_AndStillRecorded()
    {
        var (svc, run, audit, t, _) = Make();
        using var _t = t;
        run.Override = () => throw new InvalidOperationException("no process");
        var r = await svc.ExecuteAsync(Lcu, "", default);
        Assert.Equal(UninstallOutcome.Failed, r.Outcome);
        Assert.Equal("Failed", audit.ReadAll().Last().Stage);
    }

    [Fact]
    public async Task StandardUser_CannotExecute_EvenByCallingTheServiceDirectly()
    {
        var (svc, run, _, t, _) = Make(admin: false);
        using var _t = t;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExecuteAsync(Lcu, "", default));
        Assert.Empty(run.Calls);
    }

    [Fact]
    public async Task FeatureSwitchedOffAfterTheWindowOpened_BlocksTheAction()
    {
        var (svc, run, _, t, settings) = Make();
        using var _t = t;
        settings.Advanced.EnableUninstall = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExecuteAsync(Lcu, "", default));
        Assert.Empty(run.Calls);
    }

    [Fact]
    public async Task NonRemovableUpdate_CannotBeExecuted()
    {
        var (svc, run, _, t, _) = Make();
        using var _t = t;
        var ssu = new InstalledUpdate("KB5000001", "Servicing Stack Update (KB5000001)", DateTimeOffset.UtcNow, UpdateState.Installed, UpdateCategory.ServicingStack);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExecuteAsync(ssu, "", default));
        Assert.Empty(run.Calls);
    }

    [Fact]
    public async Task IfTheAuditRecordCannotBeWritten_NothingIsRun()
    {
        var t = new TempDir();
        using var _t = t;
        var blocker = Path.Combine(t.Path, "audit");
        File.WriteAllText(blocker, "a file where the folder should be");        // audit directory cannot be created
        var run = new FakeRunner(0);
        var settings = new AppSettings(); settings.Advanced.EnableUninstall = true;
        var svc = new UninstallService(new UninstallPlanner(new FakeCbs(Cbs.Rollup("26100.9711.1.30"))), run, new FakePrivilege(true),
            new UninstallAudit(Path.Combine(blocker, "u.jsonl")), () => settings, () => new[] { Lcu });
        await Assert.ThrowsAnyAsync<IOException>(() => svc.ExecuteAsync(Lcu, "", default));
        Assert.Empty(run.Calls);                          // no record, no removal
    }

    [Fact]
    public void Audit_SurvivesADamagedLine()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "a.jsonl");
        var audit = new UninstallAudit(p);
        audit.Append(new UninstallAuditEntry { Id = "1", Stage = "Started", Kb = "KB1" });
        File.AppendAllText(p, "{ not json\n");
        audit.Append(new UninstallAuditEntry { Id = "1", Stage = "Succeeded", Kb = "KB1" });
        Assert.Equal(2, audit.ReadAll().Count);
    }
}
