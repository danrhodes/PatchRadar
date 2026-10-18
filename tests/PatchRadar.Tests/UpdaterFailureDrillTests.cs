using System.Net;
using System.Text;
using PatchRadar.AppUpdate;
using PatchRadar.Settings;

namespace PatchRadar.Tests;

/// <summary>
/// P3.2 drills. Every case must: log a clear reason, leave the running version intact,
/// and never leave the install without a working exe.
/// </summary>
public class UpdaterFailureDrillTests
{
    private static readonly ReleaseInfo R = Rel.Make("v1.2.0");

    private static UpdaterRig Rig(bool requireSig = false, SignatureResult? sig = null, ReleaseInfo? release = null) =>
        new("1.0.1", "full", new FakeReleases(release ?? R), sig, requireSig);

    /// <summary>The invariant that holds after every drill.</summary>
    private static void AssertInstallIntact(UpdaterRig rig)
    {
        Assert.True(File.Exists(rig.Exe), "the install must always have an exe");
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.Null(rig.Launcher.Exe);                         // never restarted into a bad build
        Assert.Null(rig.Pending.Load());                       // no half-finished update recorded
        Assert.False(File.Exists(rig.Exe + ".old"));           // nothing left to confuse a later start
    }

    // 1. corrupt download -------------------------------------------------------------------

    [Fact]
    public async Task CorruptDownload_TruncatedFile_FailsHashCheck()
    {
        using var rig = Rig();
        var bytes = rig.Downloader.Serve(R);
        rig.Downloader.Files[AssetPicker.Pick(R, "full")!.Exe.Url] = bytes[..(bytes.Length / 2)];   // short read

        var o = await rig.Updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("SHA-256", o.Message);
        Assert.True(rig.Log.Has("rejected"));
        Assert.False(File.Exists(Path.Combine(rig.UpdatesDir, R.Tag, AssetPicker.Pick(R, "full")!.Exe.Name)), "the bad file must not be kept");
        AssertInstallIntact(rig);
    }

    private sealed class DyingDownloader : IFileDownloader
    {
        public Task DownloadAsync(string url, string dest, CancellationToken ct)
        {
            File.WriteAllBytes(dest, new byte[] { 1, 2, 3 });          // partial data on disk, then the connection drops
            throw new UpdateCheckException("Download failed: The connection was closed unexpectedly.");
        }
        public Task<string> DownloadTextAsync(string url, CancellationToken ct) =>
            Task.FromResult(new string('a', 64) + "  PatchRadar-v1.2.0-win-x64.exe\n");
    }

    [Fact]
    public async Task ConnectionDropsMidDownload_IsReported_AndNothingInstalled()
    {
        using var rig = new UpdaterRig("1.0.1", "full", new FakeReleases(R));
        var updater = new AppUpdater(() => rig.Settings, rig.Releases, new DyingDownloader(), rig.Verifier, rig.Installer,
            rig.Launcher, rig.UpdatesDir, AppVersion.Parse("1.0.1"), "full", _ => { }, false, rig.Log);

        var o = await updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("connection", o.Message, StringComparison.OrdinalIgnoreCase);
        AssertInstallIntact(rig);
    }

    // 2. hash mismatch --------------------------------------------------------------------------

    [Fact]
    public async Task HashMismatch_LogsClearReason()
    {
        using var rig = Rig();
        rig.Downloader.Serve(R, hashOverride: new string('0', 64));
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.True(rig.Log.Has("SHA-256"));
        Assert.True(rig.Log.Has("Update 1.2.0 rejected"));
        AssertInstallIntact(rig);
    }

    [Fact]
    public async Task ReplacedExeAfterVerification_IsNotPossible_BecauseHashIsCheckedOnTheFileThatIsInstalled()
    {
        // A previously downloaded file that was tampered with on disk is re-verified, not trusted.
        using var rig = Rig();
        rig.Downloader.Serve(R);
        var dir = Path.Combine(rig.UpdatesDir, R.Tag);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AssetPicker.Pick(R, "full")!.Exe.Name), "SOMETHING-ELSE");   // stale/tampered cache

        var o = await rig.Updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Applied, o.Kind);      // re-downloaded and verified
        Assert.Equal("NEW-EXE", rig.InstalledText);
    }

    // 3. unsigned / wrong signer on Stable ----------------------------------------------------------

    [Theory]
    [InlineData(SignatureStatus.Unsigned, "no Authenticode signature")]
    [InlineData(SignatureStatus.WrongSigner, "signer is not SignPath Foundation")]
    [InlineData(SignatureStatus.Tampered, "modified after it was signed")]
    public async Task Stable_BadSignature_LogsWhy_AndInstallsNothing(SignatureStatus status, string detailPart)
    {
        var detail = SignatureClassifier.Classify(status switch
        {
            SignatureStatus.Unsigned => unchecked((int)0x800B0100),
            SignatureStatus.Tampered => unchecked((int)0x80096010),
            _ => 0,
        }, status == SignatureStatus.WrongSigner ? "CN=Someone Else, O=Someone Else" : null);
        Assert.Equal(status, detail.Status);

        using var rig = Rig(requireSig: true, sig: detail);
        rig.Downloader.Serve(R);
        var o = await rig.Updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains(detailPart, o.Message);
        Assert.True(rig.Log.Has("Signature check failed"));
        Assert.False(File.Exists(Path.Combine(rig.UpdatesDir, R.Tag, AssetPicker.Pick(R, "full")!.Exe.Name)), "a rejected exe must not stay staged");
        AssertInstallIntact(rig);
    }

    // 4. read-only install dir ---------------------------------------------------------------------

    [Fact]
    public async Task ReadOnlyInstallDir_FallsBackToNotify_AndSaysWhy()
    {
        using var rig = Rig();
        rig.Ops.Writable = false;
        rig.Downloader.Serve(R);
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Available, o.Kind);
        Assert.True(rig.Log.Has("not writable"));
        Assert.Contains("read-only", o.Message);
        AssertInstallIntact(rig);
    }

    [Fact]
    public async Task InstallDirBecomesReadOnlyAfterStaging_IsHandledAtApplyTime()
    {
        using var rig = Rig();
        rig.Settings.ApplyAutomatically = false;
        rig.Downloader.Serve(R);
        Assert.Equal(UpdateOutcomeKind.Staged, (await rig.Updater.CheckAsync(false, default)).Kind);

        rig.Ops.Writable = false;
        var o = rig.Updater.ApplyStaged();

        Assert.Equal(UpdateOutcomeKind.Available, o.Kind);
        AssertInstallIntact(rig);
    }

    // 5. new exe that crashes on start -------------------------------------------------------------

    [Fact]
    public void NewExeCrashesOnStart_RollsBack_AndSaysSo()
    {
        using var rig = Rig();
        Assert.Equal(ApplyResult.Applied, rig.Installer.Apply(Write(rig, "BROKEN-EXE"), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0")));
        var health = new UpdateHealth(rig.Pending, rig.Installer, rig.Log);
        var v = AppVersion.Parse("1.2.0");

        health.OnStartup(v, _ => { });       // crashed
        health.OnStartup(v, _ => { });       // crashed again
        var o = health.OnStartup(v, s => rig.Skipped = s);

        Assert.Equal(StartupAction.RolledBack, o.Action);
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.True(rig.Log.Has("rolling back"));
        Assert.Equal("1.2.0", rig.Skipped);
        Assert.Contains("rolled back", rig.Pending.TakeNotice());
    }

    private static string Write(UpdaterRig rig, string content)
    {
        var p = Path.Combine(rig.Dir.Path, "new.exe");
        File.WriteAllText(p, content);
        return p;
    }

    // 6. antivirus blocking the rename / copy --------------------------------------------------------

    [Fact]
    public void AvBlocksRenameOfRunningExe_AbortsCleanly()
    {
        using var rig = Rig();
        rig.Ops.FailMove = (from, _) => from == rig.Exe;
        var r = rig.Installer.Apply(Write(rig, "NEW-EXE"), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.True(rig.Log.Has("could not rename the running exe"));
        AssertInstallIntact(rig);
    }

    private sealed class PartialCopyOps(Func<string> exePath) : IInstallFileOps
    {
        private readonly InstallFileOps _real = new();
        public bool Exists(string p) => _real.Exists(p);
        public void Move(string a, string b) => _real.Move(a, b);
        public void Delete(string p) => _real.Delete(p);
        public bool IsDirectoryWritable(string d) => _real.IsDirectoryWritable(d);
        public void Copy(string from, string to)
        {
            if (to == exePath()) { File.WriteAllText(to, "HALF-WRITTEN"); throw new IOException("The file was quarantined by antivirus"); }
            _real.Copy(from, to);
        }
    }

    [Fact]
    public void AvQuarantinesNewExeMidCopy_PartialFileIsReplacedByTheOriginal()
    {
        using var rig = Rig();
        var installer = new UpdateInstaller(new PartialCopyOps(() => rig.Exe), rig.Pending, rig.Exe, rig.Log);
        var r = installer.Apply(Write(rig, "NEW-EXE"), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.True(rig.Log.Has("restoring the current one"));
        AssertInstallIntact(rig);                    // not "HALF-WRITTEN"
    }

    [Fact]
    public void PendingJsonCannotBeWritten_UpdateIsUndone()
    {
        using var rig = Rig();
        File.WriteAllText(rig.UpdatesDir, "i am a file, not a folder");     // pending.json can never be created
        var r = rig.Installer.Apply(Write(rig, "NEW-EXE"), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.True(rig.Log.Has("pending.json"));
        Assert.True(File.Exists(rig.Exe));
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.False(File.Exists(rig.Exe + ".old"));
    }

    [Fact]
    public void DoubleFault_RestoreAlsoBlocked_IsLoudAndLeavesTheOldExeRecoverable()
    {
        using var rig = Rig();
        rig.Ops.FailCopy = (_, _) => true;
        var moves = 0;
        rig.Ops.FailMove = (from, to) => ++moves > 1;       // first move (exe -> .old) works, the restore move is blocked
        var r = rig.Installer.Apply(Write(rig, "NEW-EXE"), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.True(rig.Log.Has("CRITICAL"));
        Assert.Equal("OLD-EXE", File.ReadAllText(rig.Exe + ".old"));      // the log says where the good copy is
        Assert.True(rig.Log.Has(rig.Exe + ".old"));
    }

    [Fact]
    public void RestoreRenameBlocked_FallsBackToCopy_AndStillRestoresTheOriginal()
    {
        using var rig = Rig();
        var copies = 0;
        rig.Ops.FailCopy = (_, to) => to == rig.Exe && ++copies == 1;     // placing the new exe fails; the restore copy works
        var moves = 0;
        rig.Ops.FailMove = (_, _) => ++moves > 1;                         // exe -> .old works; .old -> exe is blocked
        var r = rig.Installer.Apply(Write(rig, "NEW-EXE"), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.False(rig.Log.Has("CRITICAL"));
    }

    [Fact]
    public async Task StagedFileQuarantinedBeforeApply_IsReported()
    {
        using var rig = Rig();
        rig.Settings.ApplyAutomatically = false;
        rig.Downloader.Serve(R);
        await rig.Updater.CheckAsync(false, default);
        File.Delete(Path.Combine(rig.UpdatesDir, R.Tag, AssetPicker.Pick(R, "full")!.Exe.Name));      // AV removes it

        var o = rig.Updater.ApplyStaged();

        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        AssertInstallIntact(rig);
    }

    [Fact]
    public async Task UpdatesFolderUnwritable_IsReported_NotThrown()
    {
        using var rig = Rig();
        File.WriteAllText(rig.UpdatesDir, "a file where the folder should be");
        rig.Downloader.Serve(R);
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("updates", o.Message, StringComparison.OrdinalIgnoreCase);
        AssertInstallIntact(rig);
    }

    private sealed class ThrowingLauncher : IProcessLauncher
    {
        public void StartAndExit(string exePath, string arguments) => throw new System.ComponentModel.Win32Exception(5, "Access is denied");
    }

    [Fact]
    public async Task NewExeInstalledButCannotBeStarted_TellsTheUserToRestart_AndPendingStaysForTheHealthCheck()
    {
        using var rig = Rig();
        rig.Downloader.Serve(R);
        var updater = new AppUpdater(() => rig.Settings, rig.Releases, rig.Downloader, rig.Verifier, rig.Installer,
            new ThrowingLauncher(), rig.UpdatesDir, AppVersion.Parse("1.0.1"), "full", _ => { }, false, rig.Log);

        var o = await updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Applied, o.Kind);
        Assert.Contains("manually", o.Message);
        Assert.Equal("NEW-EXE", rig.InstalledText);            // swapped, so the next launch runs the new build
        Assert.NotNull(rig.Pending.Load());                    // and the health check will watch it
        Assert.True(rig.Log.Has("could not be started"));
    }

    // 7. GitHub rate limit / outage -----------------------------------------------------------------------

    private sealed class FixedHandler(HttpStatusCode code, Action<HttpResponseMessage>? tweak = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(code) { RequestMessage = r };
            tweak?.Invoke(resp);
            return Task.FromResult(resp);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GitHubRateLimit_EndToEnd_LogsReason_AndKeepsRunningVersion(HttpStatusCode code)
    {
        using var rig = new UpdaterRig();
        var source = new GitHubReleaseSource(new HttpClient(new FixedHandler(code, r =>
        {
            r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds().ToString());
        })), "1.0.1", log: rig.Log);
        var updater = new AppUpdater(() => rig.Settings, source, rig.Downloader, rig.Verifier, rig.Installer, rig.Launcher,
            rig.UpdatesDir, AppVersion.Parse("1.0.1"), "full", _ => { }, false, rig.Log);

        var o = await updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("rate limit", o.Message);
        Assert.Contains("resets", o.Message);
        Assert.True(rig.Log.Has("Update check failed"));
        AssertInstallIntact(rig);
        Assert.Empty(rig.Downloader.Requested);
    }

    [Fact]
    public async Task GitHubServerError_IsReported()
    {
        using var rig = new UpdaterRig();
        var source = new GitHubReleaseSource(new HttpClient(new FixedHandler(HttpStatusCode.BadGateway)), "1.0.1");
        var updater = new AppUpdater(() => rig.Settings, source, rig.Downloader, rig.Verifier, rig.Installer, rig.Launcher,
            rig.UpdatesDir, AppVersion.Parse("1.0.1"), "full", _ => { }, false, rig.Log);
        var o = await updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("502", o.Message);
        AssertInstallIntact(rig);
    }

    // 8. hostile release data ---------------------------------------------------------------------------------

    [Fact]
    public async Task ReleaseAssetsOnAnotherHost_NeverDownloaded()
    {
        using var rig = Rig(release: Rel.Make("v1.2.0", host: "https://evil.example"));
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Empty(rig.Downloader.Requested);
        AssertInstallIntact(rig);
    }

    // 9. the schedule survives all of the above -----------------------------------------------------------

    [Fact]
    public async Task AfterAFailure_TheNextCheckCanSucceed()
    {
        using var rig = Rig();
        rig.Downloader.Serve(R, hashOverride: new string('0', 64));
        Assert.Equal(UpdateOutcomeKind.Failed, (await rig.Updater.CheckAsync(false, default)).Kind);
        rig.Downloader.Serve(R);                                            // the bad download is fixed upstream
        Assert.Equal(UpdateOutcomeKind.Applied, (await rig.Updater.CheckAsync(false, default)).Kind);
        Assert.Equal("NEW-EXE", rig.InstalledText);
    }
}
