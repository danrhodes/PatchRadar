using System.Net;
using System.Security.Cryptography;
using System.Text;
using PatchRadar.AppUpdate;
using PatchRadar.Settings;

namespace PatchRadar.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.2.3", "v1.2.3", 0)]
    [InlineData("1.2.3", "1.2.3+abcdef", 0)]
    [InlineData("1.2.3", "1.2.3-pre-release.1-abc1234", 1)]                       // full outranks its own pre-release
    [InlineData("1.2.3-pre-release.10-abc1234", "1.2.3-pre-release.9-fff9999", 1)] // N is numeric, not text
    [InlineData("1.2.3-pre-release.2-zzz", "1.2.3-pre-release.11-aaa", -1)]
    [InlineData("1.2.4-pre-release.1-abc1234", "1.2.3", 1)]
    [InlineData("1.2.3-pre-release.5-abc1234", "1.2.3-pre-release.5-def5678", 0)]  // same N: same build counter
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]                               // plain SemVer rules elsewhere
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    public void Compare(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(AppVersion.Parse(a).CompareTo(AppVersion.Parse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    public void Invalid_DoesNotParse(string s) => Assert.False(AppVersion.TryParse(s, out _));

    [Fact]
    public void ToString_StripsVAndBuildMetadata() =>
        Assert.Equal("1.2.3-pre-release.4-abc1234", AppVersion.Parse("v1.2.3-pre-release.4-abc1234+sha.1").ToString());

    [Fact] public void DevBuild_IsDetected() => Assert.True(AppVersion.Parse("1.0.0").IsDevBuild);
    [Fact] public void ReleaseBuild_IsNotDev() => Assert.False(AppVersion.Parse("1.0.1").IsDevBuild);
    [Fact] public void Running_ParsesTheAssembly() => Assert.NotNull(AppVersion.Running());
}

internal static class Rel
{
    public static ReleaseInfo Make(string tag, bool pre = false, bool draft = false, bool slim = true, bool sha = true, string host = "https://github.com")
    {
        var assets = new List<ReleaseAsset>
        {
            new($"PatchRadar-{tag}-win-x64.exe", $"{host}/danrhodes/PatchRadar/releases/download/{tag}/PatchRadar-{tag}-win-x64.exe", 100),
        };
        if (sha) assets.Add(new($"PatchRadar-{tag}-win-x64.exe.sha256", $"{host}/danrhodes/PatchRadar/releases/download/{tag}/PatchRadar-{tag}-win-x64.exe.sha256", 90));
        if (slim)
        {
            assets.Add(new($"PatchRadar-{tag}-win-x64-slim.exe", $"{host}/danrhodes/PatchRadar/releases/download/{tag}/PatchRadar-{tag}-win-x64-slim.exe", 50));
            if (sha) assets.Add(new($"PatchRadar-{tag}-win-x64-slim.exe.sha256", $"{host}/danrhodes/PatchRadar/releases/download/{tag}/PatchRadar-{tag}-win-x64-slim.exe.sha256", 90));
        }
        return new ReleaseInfo(tag, AppVersion.Parse(tag), pre, draft, $"https://github.com/danrhodes/PatchRadar/releases/tag/{tag}", assets);
    }
}

public class ReleaseSelectionTests
{
    private static readonly AppVersion Running = AppVersion.Parse("1.2.0");

    [Fact]
    public void Stable_IgnoresPreReleasesAndDrafts()
    {
        var list = new[] { Rel.Make("v1.3.0-pre-release.4-abc1234", pre: true), Rel.Make("v1.2.5", draft: true), Rel.Make("v1.2.1") };
        Assert.Equal("v1.2.1", ReleaseSelector.Choose(list, UpdateChannel.Stable, Running, null)!.Tag);
    }

    [Fact]
    public void PreReleaseChannel_TakesNewestIncludingPreReleases()
    {
        var list = new[] { Rel.Make("v1.3.0-pre-release.10-abc1234", pre: true), Rel.Make("v1.3.0-pre-release.9-fff9999", pre: true), Rel.Make("v1.2.1") };
        Assert.Equal("v1.3.0-pre-release.10-abc1234", ReleaseSelector.Choose(list, UpdateChannel.PreRelease, Running, null)!.Tag);
    }

    [Fact]
    public void NeverDowngrades_OrReoffersSameVersion()
    {
        Assert.Null(ReleaseSelector.Choose(new[] { Rel.Make("v1.1.0"), Rel.Make("v1.2.0") }, UpdateChannel.Stable, Running, null));
    }

    [Fact]
    public void RunningPreRelease_IsUpgradedByItsFullRelease()
    {
        var running = AppVersion.Parse("1.3.0-pre-release.7-abc1234");
        Assert.Equal("v1.3.0", ReleaseSelector.Choose(new[] { Rel.Make("v1.3.0") }, UpdateChannel.Stable, running, null)!.Tag);
    }

    [Fact]
    public void SkippedVersion_IsNeverOffered_ButNewerIs()
    {
        var list = new[] { Rel.Make("v1.2.1") };
        Assert.Null(ReleaseSelector.Choose(list, UpdateChannel.Stable, Running, "1.2.1"));
        Assert.Null(ReleaseSelector.Choose(list, UpdateChannel.Stable, Running, "v1.2.1"));
        Assert.Equal("v1.2.2", ReleaseSelector.Choose(new[] { Rel.Make("v1.2.1"), Rel.Make("v1.2.2") }, UpdateChannel.Stable, Running, "1.2.1")!.Tag);
    }
}

public class AssetPickerTests
{
    [Fact]
    public void Full_TakesPlainExe_Slim_TakesSlimExe()
    {
        var r = Rel.Make("v1.2.3");
        Assert.Equal("PatchRadar-v1.2.3-win-x64.exe", AssetPicker.Pick(r, "full")!.Exe.Name);
        Assert.Equal("PatchRadar-v1.2.3-win-x64.exe.sha256", AssetPicker.Pick(r, "full")!.Sha256.Name);
        Assert.Equal("PatchRadar-v1.2.3-win-x64-slim.exe", AssetPicker.Pick(r, "slim")!.Exe.Name);
        Assert.Equal("PatchRadar-v1.2.3-win-x64-slim.exe.sha256", AssetPicker.Pick(r, "slim")!.Sha256.Name);
    }

    [Fact] public void MissingChecksum_IsRejected() => Assert.Null(AssetPicker.Pick(Rel.Make("v1.2.3", sha: false), "full"));
    [Fact] public void MissingSlim_IsRejectedForSlimOnly() => Assert.Null(AssetPicker.Pick(Rel.Make("v1.2.3", slim: false), "slim"));
    [Fact] public void AssetOnAnotherHost_IsRejected() => Assert.Null(AssetPicker.Pick(Rel.Make("v1.2.3", host: "https://evil.example"), "full"));

    [Fact]
    public void ReleaseJson_ParsesArrayAndSingle()
    {
        const string one = """
        {"tag_name":"v1.2.3","prerelease":false,"draft":false,"html_url":"https://github.com/danrhodes/PatchRadar/releases/tag/v1.2.3",
         "assets":[{"name":"a.exe","browser_download_url":"https://github.com/danrhodes/PatchRadar/releases/download/v1.2.3/a.exe","size":5}]}
        """;
        Assert.Single(ReleaseJson.Parse(one));
        var arr = ReleaseJson.Parse("[" + one + ",{\"tag_name\":\"not-a-version\"},{\"nothing\":1}]");
        Assert.Single(arr);                                // junk entries skipped, never throw
        Assert.Equal(5, arr[0].Assets[0].Size);
    }
}

public class UrlAllowlistTests
{
    [Theory]
    [InlineData("https://github.com/danrhodes/PatchRadar/releases/download/v1/a.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/abc", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/abc", true)]
    [InlineData("https://api.github.com/repos/danrhodes/PatchRadar/releases/assets/123", true)]
    [InlineData("http://github.com/danrhodes/PatchRadar/releases/download/v1/a.exe", false)]      // not https
    [InlineData("https://github.com/someoneelse/Other/releases/download/v1/a.exe", false)]         // another repo
    [InlineData("https://api.github.com/repos/someoneelse/Other/releases/assets/1", false)]
    [InlineData("https://github.com.evil.example/danrhodes/PatchRadar/releases/download/v1/a.exe", false)]
    [InlineData("https://evil.example/a.exe", false)]
    [InlineData("ftp://github.com/danrhodes/PatchRadar/releases/download/v1/a.exe", false)]
    [InlineData("file:///c:/windows/system32/cmd.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AssetUrl(string? url, bool allowed) => Assert.Equal(allowed, UpdateRepo.IsAllowedAssetUrl(url));

    [Theory]
    [InlineData("https://github.com/danrhodes/PatchRadar/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com/other/repo/releases/tag/v1.0.0", false)]
    [InlineData("http://github.com/danrhodes/PatchRadar/releases", false)]
    [InlineData("https://evil.example/danrhodes/PatchRadar/", false)]
    public void ReleasePage(string url, bool allowed) => Assert.Equal(allowed, UpdateRepo.IsAllowedReleasePage(url));
}

public class DownloaderTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Urls.Add(r.RequestUri!.ToString());
            return Task.FromResult(route(r));
        }
    }

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    [Fact]
    public async Task FollowsRedirectToAllowedCdn()
    {
        using var t = new TempDir();
        var h = new RouteHandler(r => r.RequestUri!.Host == "github.com"
            ? Redirect("https://objects.githubusercontent.com/x")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var d = new AllowlistedDownloader(new HttpClient(h), "1.0.0");
        var dest = Path.Combine(t.Path, "a.exe");
        await d.DownloadAsync("https://github.com/danrhodes/PatchRadar/releases/download/v1/a.exe", dest, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dest));
        Assert.Equal(2, h.Urls.Count);
    }

    [Fact]
    public async Task RedirectToDisallowedHost_IsRefused_AndNothingIsWritten()
    {
        using var t = new TempDir();
        var h = new RouteHandler(r => r.RequestUri!.Host == "github.com"
            ? Redirect("https://evil.example/payload.exe")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 9 }) });
        var d = new AllowlistedDownloader(new HttpClient(h), "1.0.0");
        var dest = Path.Combine(t.Path, "a.exe");
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() =>
            d.DownloadAsync("https://github.com/danrhodes/PatchRadar/releases/download/v1/a.exe", dest, default));
        Assert.Contains("not an allowed update host", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.Single(h.Urls);       // the evil host was never contacted
    }

    [Fact]
    public async Task RedirectToHttp_IsRefused()
    {
        var h = new RouteHandler(_ => Redirect("http://objects.githubusercontent.com/x"));
        var d = new AllowlistedDownloader(new HttpClient(h), "1.0.0");
        await Assert.ThrowsAsync<UpdateCheckException>(() => d.DownloadTextAsync("https://github.com/danrhodes/PatchRadar/releases/download/v1/a.sha256", default));
    }

    [Fact]
    public async Task RedirectLoop_Stops()
    {
        var h = new RouteHandler(_ => Redirect("https://github.com/danrhodes/PatchRadar/releases/download/v1/a"));
        var d = new AllowlistedDownloader(new HttpClient(h), "1.0.0");
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => d.DownloadTextAsync("https://github.com/danrhodes/PatchRadar/releases/download/v1/a", default));
        Assert.Contains("redirects", ex.Message);
    }

    [Fact]
    public async Task HttpError_IsReported()
    {
        var d = new AllowlistedDownloader(new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), "1.0.0");
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => d.DownloadTextAsync("https://github.com/danrhodes/PatchRadar/releases/download/v1/a", default));
        Assert.Contains("404", ex.Message);
    }
}

public class GitHubReleaseSourceTests
{
    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r);
            return Task.FromResult(route(r));
        }
    }

    private const string Body = """{"tag_name":"v1.2.3","prerelease":false,"draft":false,"html_url":"https://github.com/danrhodes/PatchRadar/releases/tag/v1.2.3","assets":[]}""";

    [Fact]
    public async Task Stable_UsesLatestEndpoint_WithRequiredHeaders()
    {
        var h = new Capture(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        var src = new GitHubReleaseSource(new HttpClient(h), "1.0.0");
        var list = await src.GetReleasesAsync(UpdateChannel.Stable, default);
        Assert.Single(list);
        var req = h.Requests.Single();
        Assert.Equal("https://api.github.com/repos/danrhodes/PatchRadar/releases/latest", req.RequestUri!.ToString());
        Assert.Contains("PatchRadar/1.0.0", req.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", req.Headers.Accept.ToString());
    }

    [Fact]
    public async Task PreRelease_UsesListEndpoint()
    {
        var h = new Capture(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + Body + "]") });
        await new GitHubReleaseSource(new HttpClient(h), "1.0.0").GetReleasesAsync(UpdateChannel.PreRelease, default);
        Assert.EndsWith("/releases?per_page=10", h.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task ETag_IsSentAndA304ServesTheCachedBody()
    {
        using var t = new TempDir();
        var calls = 0;
        var h = new Capture(r =>
        {
            calls++;
            if (calls == 1)
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) };
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
                return ok;
            }
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var src = new GitHubReleaseSource(new HttpClient(h), "1.0.0", t.Path);
        await src.GetReleasesAsync(UpdateChannel.Stable, default);
        var again = await src.GetReleasesAsync(UpdateChannel.Stable, default);
        Assert.Single(again);
        Assert.Equal("\"abc\"", h.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task RateLimit_ProducesAClearReason()
    {
        var h = new Capture(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        });
        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() =>
            new GitHubReleaseSource(new HttpClient(h), "1.0.0").GetReleasesAsync(UpdateChannel.Stable, default));
        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task NoStableReleaseYet_IsNotAnError()
    {
        var h = new Capture(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await new GitHubReleaseSource(new HttpClient(h), "1.0.0").GetReleasesAsync(UpdateChannel.Stable, default));
    }

    [Fact]
    public async Task NetworkFailure_IsWrapped()
    {
        var h = new Capture(_ => throw new HttpRequestException("no route"));
        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            new GitHubReleaseSource(new HttpClient(h), "1.0.0").GetReleasesAsync(UpdateChannel.Stable, default));
    }
}

public class Sha256Tests
{
    [Fact]
    public void Parse_AcceptsBothSeparatorStyles_AndRequiresMatchingName()
    {
        var h = new string('a', 64);
        Assert.Equal(h, Sha256Check.ParseExpected($"{h}  PatchRadar.exe\n", "PatchRadar.exe"));
        Assert.Equal(h, Sha256Check.ParseExpected($"{h} *PatchRadar.exe\r\n", "PatchRadar.exe"));
        Assert.Null(Sha256Check.ParseExpected($"{h}  Other.exe\n", "PatchRadar.exe"));     // swapped checksum file
        Assert.Null(Sha256Check.ParseExpected("not a hash  PatchRadar.exe", "PatchRadar.exe"));
        Assert.Null(Sha256Check.ParseExpected("", "PatchRadar.exe"));
    }

    [Fact]
    public void Matches_ComparesRealFileHash()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "f.bin");
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        var hex = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
        Assert.True(Sha256Check.Matches(p, hex));
        Assert.True(Sha256Check.Matches(p, hex.ToLowerInvariant()));
        Assert.False(Sha256Check.Matches(p, new string('0', 64)));
    }
}

public class SignatureTests
{
    private const string Good = "CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US";

    [Fact] public void Valid_AndRightSigner() => Assert.Equal(SignatureStatus.Valid, SignatureClassifier.Classify(0, Good).Status);
    [Fact] public void Valid_ButWrongSigner() => Assert.Equal(SignatureStatus.WrongSigner, SignatureClassifier.Classify(0, "CN=Microsoft Corporation, O=Microsoft Corporation").Status);
    [Fact] public void Valid_NoSubject_IsWrongSigner() => Assert.Equal(SignatureStatus.WrongSigner, SignatureClassifier.Classify(0, null).Status);
    [Fact] public void Unsigned() => Assert.Equal(SignatureStatus.Unsigned, SignatureClassifier.Classify(unchecked((int)0x800B0100), null).Status);
    [Fact] public void Tampered() => Assert.Equal(SignatureStatus.Tampered, SignatureClassifier.Classify(unchecked((int)0x80096010), Good).Status);
    [Fact] public void UntrustedChain() => Assert.Equal(SignatureStatus.Invalid, SignatureClassifier.Classify(unchecked((int)0x800B0109), Good).Status);
    [Fact] public void UnknownFailure() => Assert.Equal(SignatureStatus.Invalid, SignatureClassifier.Classify(unchecked((int)0x80004005), null).Status);
    [Fact] public void SignerCheck_IsCaseInsensitive() => Assert.True(SignatureClassifier.Classify(0, "cn=x, o=signpath foundation").IsValid);

    [Fact]
    public void Tampered_NeverReportsValid_EvenWithRightSubject() =>
        Assert.False(SignatureClassifier.Classify(unchecked((int)0x80096010), Good).IsValid);

    // ---- real WinVerifyTrust against real files -------------------------------------------

    private static string? SignedMicrosoftExe() =>
        new[] { @"C:\Program Files\dotnet\dotnet.exe", @"C:\Program Files (x86)\dotnet\dotnet.exe" }.FirstOrDefault(File.Exists);

    [Fact]
    public void Real_UnsignedExe_IsUnsigned()
    {
        // The test host itself is built locally and unsigned.
        var unsigned = typeof(SignatureTests).Assembly.Location;
        var r = new WinTrustVerifier().Verify(unsigned);
        Assert.Equal(SignatureStatus.Unsigned, r.Status);
    }

    [Fact]
    public void Real_SignedByMicrosoft_IsValidSignatureButWrongSigner()
    {
        var exe = SignedMicrosoftExe();
        if (exe is null) return;       // no embedded-signature Microsoft binary on this machine
        var r = new WinTrustVerifier().Verify(exe);
        Assert.Equal(SignatureStatus.WrongSigner, r.Status);
        Assert.Contains("Microsoft", r.Signer);
    }

    [Fact]
    public void Real_TamperedSignedExe_IsDetected()
    {
        var exe = SignedMicrosoftExe();
        if (exe is null) return;
        using var t = new TempDir();
        var copy = Path.Combine(t.Path, "dotnet.exe");
        File.Copy(exe, copy);
        var bytes = File.ReadAllBytes(copy);
        bytes[1024] ^= 0xFF;           // flip a byte inside the signed image
        File.WriteAllBytes(copy, bytes);
        var r = new WinTrustVerifier().Verify(copy);
        Assert.NotEqual(SignatureStatus.Valid, r.Status);
        Assert.NotEqual(SignatureStatus.WrongSigner, r.Status);
    }

    [Fact]
    public void Real_MissingFile_IsNotValid()
    {
        var r = new WinTrustVerifier().Verify(@"C:\definitely\not\here.exe");
        Assert.False(r.IsValid);
    }
}

// ---- orchestration ------------------------------------------------------------------------------

internal sealed class FakeReleases(params ReleaseInfo[] list) : IReleaseSource
{
    public Exception? Throw;
    public int Calls;
    public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(UpdateChannel c, CancellationToken ct)
    {
        Calls++;
        if (Throw is not null) throw Throw;
        return Task.FromResult<IReadOnlyList<ReleaseInfo>>(list);
    }
}

internal sealed class FakeDownloader : IFileDownloader
{
    public Dictionary<string, byte[]> Files = new();
    public List<string> Requested = new();
    public Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        Requested.Add(url);
        File.WriteAllBytes(dest, Files[url]);
        return Task.CompletedTask;
    }
    public Task<string> DownloadTextAsync(string url, CancellationToken ct)
    {
        Requested.Add(url);
        return Task.FromResult(Encoding.UTF8.GetString(Files[url]));
    }

    /// <summary>Register an exe and its checksum for a release; returns the exe bytes.</summary>
    public byte[] Serve(ReleaseInfo r, string flavour = "full", string content = "NEW-EXE", string? hashOverride = null)
    {
        var a = AssetPicker.Pick(r, flavour)!;
        var bytes = Encoding.UTF8.GetBytes(content);
        Files[a.Exe.Url] = bytes;
        var hash = hashOverride ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Files[a.Sha256.Url] = Encoding.ASCII.GetBytes($"{hash}  {a.Exe.Name}\n");
        return bytes;
    }
}

internal sealed class FakeVerifier(SignatureResult result) : IAuthenticodeVerifier
{
    public int Calls;
    public SignatureResult Verify(string path) { Calls++; return result; }
}

internal sealed class FakeLauncher : IProcessLauncher
{
    public string? Exe, Args;
    public void StartAndExit(string exePath, string arguments) { Exe = exePath; Args = arguments; }
}

/// <summary>Wraps the real file ops and fails chosen operations, for the failure drills.</summary>
internal sealed class FlakyOps : IInstallFileOps
{
    private readonly InstallFileOps _real = new();
    public Func<string, string, bool>? FailMove, FailCopy;
    public bool Writable = true;
    public bool Exists(string p) => _real.Exists(p);
    public void Move(string a, string b) { if (FailMove?.Invoke(a, b) == true) throw new IOException("locked by antivirus"); _real.Move(a, b); }
    public void Copy(string a, string b) { if (FailCopy?.Invoke(a, b) == true) throw new IOException("blocked"); _real.Copy(a, b); }
    public void Delete(string p) => _real.Delete(p);
    public bool IsDirectoryWritable(string d) => Writable && _real.IsDirectoryWritable(d);
}

internal sealed class CapturingLog : PatchRadar.Infra.ILog
{
    public readonly List<string> Lines = new();
    public void Debug(string m) => Lines.Add("DEBUG " + m);
    public void Info(string m) => Lines.Add("INFO " + m);
    public void Warn(string m) => Lines.Add("WARN " + m);
    public void Error(string m, Exception? ex = null) => Lines.Add("ERROR " + m + (ex is null ? "" : " :: " + ex.Message));
    public string All => string.Join(Environment.NewLine, Lines);
    public bool Has(string text) => Lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
}

internal sealed class UpdaterRig : IDisposable
{
    public readonly TempDir Dir = new();
    public readonly string InstallDir, UpdatesDir, Exe;
    public readonly FakeDownloader Downloader = new();
    public readonly FakeLauncher Launcher = new();
    public readonly FlakyOps Ops = new();
    public readonly CapturingLog Log = new();
    public readonly PendingStore Pending;
    public readonly UpdateInstaller Installer;
    public readonly AppUpdateSettings Settings = new() { Mode = UpdateMode.Auto, Channel = UpdateChannel.Stable, ApplyAutomatically = true };
    public string? Skipped;
    public AppUpdater Updater = null!;

    public UpdaterRig(string running = "1.0.1", string flavour = "full", FakeReleases? releases = null,
        SignatureResult? sig = null, bool requireSig = false)
    {
        InstallDir = Path.Combine(Dir.Path, "install");
        UpdatesDir = Path.Combine(Dir.Path, "updates");
        Directory.CreateDirectory(InstallDir);
        Exe = Path.Combine(InstallDir, "PatchRadar.exe");
        File.WriteAllText(Exe, "OLD-EXE");
        Pending = new PendingStore(UpdatesDir);
        Installer = new UpdateInstaller(Ops, Pending, Exe, Log);
        Verifier = new FakeVerifier(sig ?? new SignatureResult(SignatureStatus.Valid, "O=SignPath Foundation"));
        Releases = releases ?? new FakeReleases();
        Updater = new AppUpdater(() => Settings, Releases, Downloader, Verifier, Installer, Launcher, UpdatesDir,
            AppVersion.Parse(running), flavour, v => Skipped = v, requireSig, Log);
    }

    public FakeVerifier Verifier { get; }
    public FakeReleases Releases { get; }
    public string InstalledText => File.ReadAllText(Exe);
    public void Dispose() => Dir.Dispose();
}

public class AppUpdaterTests
{
    private static readonly ReleaseInfo R = Rel.Make("v1.2.0");

    private static UpdaterRig Rig(string running = "1.0.1", string flavour = "full", bool requireSig = false, SignatureResult? sig = null, params ReleaseInfo[] releases)
        => new(running, flavour, new FakeReleases(releases.Length > 0 ? releases : new[] { R }), sig, requireSig);

    [Fact]
    public async Task Auto_DownloadsVerifiesSwapsAndRestartsIntoTheNewExe()
    {
        using var rig = Rig();
        rig.Downloader.Serve(R);

        var o = await rig.Updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Applied, o.Kind);
        Assert.Equal("NEW-EXE", rig.InstalledText);                       // new build is in place
        Assert.Equal("OLD-EXE", File.ReadAllText(rig.Exe + ".old"));      // old one kept for rollback
        Assert.Equal(rig.Exe, rig.Launcher.Exe);
        Assert.Equal("--post-update", rig.Launcher.Args);
        var p = rig.Pending.Load()!;
        Assert.Equal(("1.0.1", "1.2.0", 0, false), (p.From, p.To, p.Attempts, p.Healthy));
    }

    [Fact]
    public async Task HashMismatch_Aborts_LeavesInstallUntouched()
    {
        using var rig = Rig();
        rig.Downloader.Serve(R, hashOverride: new string('0', 64));

        var o = await rig.Updater.CheckAsync(false, default);

        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("SHA-256", o.Message);
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.False(File.Exists(rig.Exe + ".old"));
        Assert.Null(rig.Launcher.Exe);
        Assert.Null(rig.Pending.Load());
    }

    [Fact]
    public async Task ChecksumFileForAnotherAsset_Aborts()
    {
        using var rig = Rig();
        rig.Downloader.Serve(R);
        var a = AssetPicker.Pick(R, "full")!;
        rig.Downloader.Files[a.Sha256.Url] = Encoding.ASCII.GetBytes($"{new string('a', 64)}  SomethingElse.exe\n");
        Assert.Equal(UpdateOutcomeKind.Failed, (await rig.Updater.CheckAsync(false, default)).Kind);
        Assert.Equal("OLD-EXE", rig.InstalledText);
    }

    [Fact]
    public async Task Stable_IgnoresPreReleases()
    {
        using var rig = Rig(releases: Rel.Make("v1.3.0-pre-release.2-abc1234", pre: true));
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.UpToDate, o.Kind);
        Assert.Empty(rig.Downloader.Requested);
    }

    [Fact]
    public async Task PreReleaseChannel_UpdatesToPreRelease_WithHashCheckOnly()
    {
        var pre = Rel.Make("v1.3.0-pre-release.2-abc1234", pre: true);
        using var rig = Rig(requireSig: true, sig: new SignatureResult(SignatureStatus.Unsigned), releases: pre);
        rig.Settings.Channel = UpdateChannel.PreRelease;
        rig.Downloader.Serve(pre);
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Applied, o.Kind);
        Assert.Equal(0, rig.Verifier.Calls);                  // unsigned pre-release must not be signature-checked
    }

    [Fact]
    public async Task NotifyMode_OffersButNeverDownloads()
    {
        using var rig = Rig();
        rig.Settings.Mode = UpdateMode.Notify;
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Available, o.Kind);
        Assert.Equal("1.2.0", o.Version);
        Assert.Equal(R.HtmlUrl, o.ReleaseUrl);
        Assert.Empty(rig.Downloader.Requested);
        Assert.Equal("OLD-EXE", rig.InstalledText);
    }

    [Fact]
    public async Task OffMode_DoesNotEvenContactGitHub()
    {
        using var rig = Rig();
        rig.Settings.Mode = UpdateMode.Off;
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Disabled, o.Kind);
        Assert.Equal(0, rig.Releases.Calls);
    }

    [Fact]
    public async Task OffMode_ManualCheck_ActsAsNotify()
    {
        using var rig = Rig();
        rig.Settings.Mode = UpdateMode.Off;
        var o = await rig.Updater.CheckAsync(true, default);
        Assert.Equal(UpdateOutcomeKind.Available, o.Kind);
        Assert.Empty(rig.Downloader.Requested);
    }

    [Fact]
    public async Task DevBuild_IsNotCheckedAutomatically_ButManualCheckInstalls()
    {
        using var rig = Rig(running: "1.0.0");
        rig.Downloader.Serve(R);
        Assert.Equal(UpdateOutcomeKind.Disabled, (await rig.Updater.CheckAsync(false, default)).Kind);
        Assert.Equal(0, rig.Releases.Calls);
        var manual = await rig.Updater.CheckAsync(true, default);
        Assert.Equal(UpdateOutcomeKind.Applied, manual.Kind);
        Assert.Equal("NEW-EXE", rig.InstalledText);
    }

    [Fact]
    public async Task ApplyAutomaticallyFalse_StagesThenAppliesOnRequest()
    {
        using var rig = Rig();
        rig.Settings.ApplyAutomatically = false;
        rig.Downloader.Serve(R);

        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Staged, o.Kind);
        Assert.Equal("1.2.0", rig.Updater.StagedVersion);
        Assert.Equal("OLD-EXE", rig.InstalledText);            // nothing swapped yet
        Assert.Null(rig.Launcher.Exe);

        var applied = rig.Updater.ApplyStaged();
        Assert.Equal(UpdateOutcomeKind.Applied, applied.Kind);
        Assert.Equal("NEW-EXE", rig.InstalledText);
        Assert.Equal("--post-update", rig.Launcher.Args);
    }

    [Fact]
    public async Task SecondCheck_WhileStaged_DoesNotRedownload()
    {
        using var rig = Rig();
        rig.Settings.ApplyAutomatically = false;
        rig.Downloader.Serve(R);
        await rig.Updater.CheckAsync(false, default);
        var requests = rig.Downloader.Requested.Count;
        var again = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Staged, again.Kind);
        Assert.Equal(requests, rig.Downloader.Requested.Count);
    }

    [Fact]
    public async Task NonWritableInstallDir_FallsBackToNotify()
    {
        using var rig = Rig();
        rig.Ops.Writable = false;
        rig.Downloader.Serve(R);
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Available, o.Kind);
        Assert.Contains("read-only", o.Message);
        Assert.Empty(rig.Downloader.Requested);                // not even downloaded
        Assert.Equal("OLD-EXE", rig.InstalledText);
    }

    [Fact]
    public async Task SlimBuild_DownloadsTheSlimAsset()
    {
        using var rig = Rig(flavour: "slim");
        rig.Downloader.Serve(R, "slim", "SLIM-EXE");
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Applied, o.Kind);
        Assert.Equal("SLIM-EXE", rig.InstalledText);
        Assert.All(rig.Downloader.Requested, u => Assert.Contains("slim", u));
    }

    [Fact]
    public async Task SkippedVersion_IsNotOffered()
    {
        using var rig = Rig();
        rig.Settings.SkippedVersion = "1.2.0";
        Assert.Equal(UpdateOutcomeKind.UpToDate, (await rig.Updater.CheckAsync(false, default)).Kind);
    }

    [Fact]
    public async Task GitHubFailure_IsReportedNotThrown()
    {
        using var rig = Rig();
        rig.Releases.Throw = new UpdateCheckException("GitHub refused the request (HTTP 403); rate limit remaining 0");
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains("rate limit", o.Message);
    }

    [Fact]
    public async Task ReleaseWithoutChecksumAsset_IsRejected()
    {
        using var rig = Rig(releases: Rel.Make("v1.2.0", sha: false));
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Empty(rig.Downloader.Requested);
    }

    // ---- signature enforcement on Stable --------------------------------------------------

    [Theory]
    [InlineData(SignatureStatus.Unsigned)]
    [InlineData(SignatureStatus.WrongSigner)]
    [InlineData(SignatureStatus.Tampered)]
    [InlineData(SignatureStatus.Invalid)]
    public async Task Stable_WhenEnforced_RejectsEveryBadSignature(SignatureStatus status)
    {
        using var rig = Rig(requireSig: true, sig: new SignatureResult(status, "CN=x", "nope"));
        rig.Downloader.Serve(R);
        var o = await rig.Updater.CheckAsync(false, default);
        Assert.Equal(UpdateOutcomeKind.Failed, o.Kind);
        Assert.Contains(status.ToString(), o.Message);
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.Null(rig.Launcher.Exe);
    }

    [Fact]
    public async Task Stable_WhenEnforced_AcceptsValidSignPathSignature()
    {
        using var rig = Rig(requireSig: true);
        rig.Downloader.Serve(R);
        Assert.Equal(UpdateOutcomeKind.Applied, (await rig.Updater.CheckAsync(false, default)).Kind);
        Assert.Equal(1, rig.Verifier.Calls);
    }

    [Fact]
    public async Task Stable_WhenNotYetEnforced_SkipsTheCheck()
    {
        using var rig = Rig(requireSig: false, sig: new SignatureResult(SignatureStatus.Unsigned));
        rig.Downloader.Serve(R);
        Assert.Equal(UpdateOutcomeKind.Applied, (await rig.Updater.CheckAsync(false, default)).Kind);
        Assert.Equal(0, rig.Verifier.Calls);
    }

    [Fact]
    public void Enforcement_ShipsOff() => Assert.False(AppUpdater.RequireSignatureOnStable);
}

public class InstallerAndHealthTests
{
    private static (UpdaterRig rig, UpdateHealth health) Make()
    {
        var rig = new UpdaterRig();
        return (rig, new UpdateHealth(rig.Pending, rig.Installer));
    }

    private static string NewExe(UpdaterRig rig, string content = "NEW-EXE")
    {
        var p = Path.Combine(rig.Dir.Path, "downloaded.exe");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Apply_FailsRenaming_LeavesEverythingAsItWas()
    {
        var (rig, _) = Make();
        using var _r = rig;
        rig.Ops.FailMove = (a, b) => a == rig.Exe;            // AV holds the running exe
        var r = rig.Installer.Apply(NewExe(rig), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.Equal("OLD-EXE", rig.InstalledText);
        Assert.False(File.Exists(rig.Exe + ".old"));
        Assert.Null(rig.Pending.Load());
    }

    [Fact]
    public void Apply_FailsCopyingNewExe_RestoresOriginal()
    {
        var (rig, _) = Make();
        using var _r = rig;
        rig.Ops.FailCopy = (a, b) => true;
        var r = rig.Installer.Apply(NewExe(rig), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0"));
        Assert.Equal(ApplyResult.Failed, r);
        Assert.Equal("OLD-EXE", rig.InstalledText);            // never left without a working exe
        Assert.False(File.Exists(rig.Exe + ".old"));
    }

    [Fact]
    public void Apply_ReplacesAStaleOldFile()
    {
        var (rig, _) = Make();
        using var _r = rig;
        File.WriteAllText(rig.Exe + ".old", "ANCIENT");
        Assert.Equal(ApplyResult.Applied, rig.Installer.Apply(NewExe(rig), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0")));
        Assert.Equal("OLD-EXE", File.ReadAllText(rig.Exe + ".old"));
    }

    private static void Swap(UpdaterRig rig) =>
        Assert.Equal(ApplyResult.Applied, rig.Installer.Apply(NewExe(rig), AppVersion.Parse("1.0.1"), AppVersion.Parse("1.2.0")));

    [Fact]
    public void FirstStart_IsJustUpdated_AndCountsAnAttempt()
    {
        var (rig, health) = Make();
        using var _r = rig;
        Swap(rig);
        var o = health.OnStartup(AppVersion.Parse("1.2.0"), v => rig.Skipped = v);
        Assert.Equal(StartupAction.JustUpdated, o.Action);
        Assert.Equal(1, rig.Pending.Load()!.Attempts);
    }

    [Fact]
    public void SecondUnhealthyStart_StillStarts_ThirdRollsBack()
    {
        var (rig, health) = Make();
        using var _r = rig;
        Swap(rig);
        var v = AppVersion.Parse("1.2.0");

        Assert.Equal(StartupAction.JustUpdated, health.OnStartup(v, s => rig.Skipped = s).Action);   // attempts 0 -> 1, crashed
        Assert.Equal(StartupAction.Normal, health.OnStartup(v, s => rig.Skipped = s).Action);        // attempts 1 -> 2, crashed
        var third = health.OnStartup(v, s => rig.Skipped = s);                                       // sees attempts 2, not healthy

        Assert.Equal(StartupAction.RolledBack, third.Action);
        Assert.Equal("OLD-EXE", rig.InstalledText);                    // previous build restored
        Assert.Equal("NEW-EXE", File.ReadAllText(rig.Exe + ".failed"));// failing build kept aside, not deleted
        Assert.False(File.Exists(rig.Exe + ".old"));
        Assert.Equal("1.2.0", rig.Skipped);                            // never offered again
        Assert.Null(rig.Pending.Load());
        Assert.Contains("rolled back", rig.Pending.TakeNotice());
        Assert.Null(rig.Pending.TakeNotice());                         // notice is one-shot
    }

    [Fact]
    public void Healthy_CleansUp()
    {
        var (rig, health) = Make();
        using var _r = rig;
        Swap(rig);
        health.OnStartup(AppVersion.Parse("1.2.0"), _ => { });
        health.MarkHealthy();
        Assert.Null(rig.Pending.Load());
        Assert.False(File.Exists(rig.Exe + ".old"));
        Assert.Equal("NEW-EXE", rig.InstalledText);
        // and a later start is perfectly normal
        Assert.Equal(StartupAction.Normal, health.OnStartup(AppVersion.Parse("1.2.0"), _ => { }).Action);
    }

    [Fact]
    public void HealthyAfter_IsSixtySeconds() => Assert.Equal(TimeSpan.FromSeconds(60), UpdateHealth.HealthyAfter);

    [Fact]
    public void PendingForADifferentVersion_IsDiscarded_NotRolledBack()
    {
        var (rig, health) = Make();
        using var _r = rig;
        Swap(rig);
        var o = health.OnStartup(AppVersion.Parse("1.0.1"), _ => { });   // the old build is running (someone restored it)
        Assert.Equal(StartupAction.Normal, o.Action);
        Assert.Null(rig.Pending.Load());
        Assert.Null(rig.Skipped);
    }

    [Fact]
    public void Rollback_WithoutOldFile_DoesNotLoopForever()
    {
        var (rig, health) = Make();
        using var _r = rig;
        rig.Pending.Save(new PendingUpdate { From = "1.0.1", To = "1.2.0", Attempts = 2 });
        var o = health.OnStartup(AppVersion.Parse("1.2.0"), _ => { });
        Assert.Equal(StartupAction.RollbackFailed, o.Action);
        Assert.Null(rig.Pending.Load());
        Assert.Equal("OLD-EXE", rig.InstalledText);              // untouched
    }

    [Fact]
    public void Rollback_WhenRestoreMoveFails_PutsTheFailingBuildBack()
    {
        var (rig, health) = Make();
        using var _r = rig;
        Swap(rig);
        var v = AppVersion.Parse("1.2.0");
        health.OnStartup(v, _ => { }); health.OnStartup(v, _ => { });
        rig.Ops.FailMove = (a, b) => a.EndsWith(".old");
        var o = health.OnStartup(v, _ => { });
        Assert.Equal(StartupAction.RollbackFailed, o.Action);
        Assert.True(File.Exists(rig.Exe));                       // an exe is always in place
        Assert.Equal("NEW-EXE", rig.InstalledText);
    }

    [Fact]
    public void NoPending_StartIsNormal_AndStaleLeftoversAreRemoved()
    {
        var (rig, health) = Make();
        using var _r = rig;
        File.WriteAllText(rig.Exe + ".old", "x");
        File.WriteAllText(rig.Exe + ".failed", "x");
        Assert.Equal(StartupAction.Normal, health.OnStartup(AppVersion.Parse("1.0.1"), _ => { }).Action);
        Assert.False(File.Exists(rig.Exe + ".old"));
        Assert.False(File.Exists(rig.Exe + ".failed"));
    }

    [Fact]
    public void CorruptPendingFile_IsTreatedAsNone()
    {
        var (rig, health) = Make();
        using var _r = rig;
        Directory.CreateDirectory(rig.UpdatesDir);
        File.WriteAllText(rig.Pending.PendingPath, "{ nope");
        Assert.Equal(StartupAction.Normal, health.OnStartup(AppVersion.Parse("1.0.1"), _ => { }).Action);
    }
}
