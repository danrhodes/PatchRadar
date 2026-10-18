using System.IO;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.AppUpdate;

public enum UpdateOutcomeKind
{
    /// <summary>Mode Off, or a development build.</summary>
    Disabled,
    UpToDate,
    /// <summary>A newer release exists; the user must act (Notify mode, read-only install dir, or a verification problem is not this).</summary>
    Available,
    /// <summary>Downloaded and verified, waiting for the user to restart (applyAutomatically false).</summary>
    Staged,
    /// <summary>Swapped in; the new exe has been launched.</summary>
    Applied,
    Failed,
}

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string? Version = null, string? ReleaseUrl = null, string? Message = null);

public interface IProcessLauncher
{
    /// <summary>Start the new exe with --post-update and end the current process.</summary>
    void StartAndExit(string exePath, string arguments);
}

/// <summary>
/// Check, download, verify, apply. All I/O is behind interfaces, so tests run without a network or a real install.
/// </summary>
public sealed class AppUpdater
{
    /// <summary>
    /// Stays false until the first SignPath-signed full release exists and verifies on a clean machine.
    /// Flipping it makes the Stable channel reject any exe without a valid SignPath Foundation signature.
    /// </summary>
    public const bool RequireSignatureOnStable = false;

    private readonly Func<AppUpdateSettings> _settings;
    private readonly IReleaseSource _releases;
    private readonly IFileDownloader _downloader;
    private readonly IAuthenticodeVerifier _signature;
    private readonly UpdateInstaller _installer;
    private readonly IProcessLauncher _launcher;
    private readonly Action<string> _setSkipped;
    private readonly string _updatesDir;
    private readonly AppVersion _running;
    private readonly string _flavour;
    private readonly bool _requireSignature;
    private readonly ILog _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    private (ReleaseInfo Release, string ExePath)? _staged;

    public UpdateOutcome? Last { get; private set; }
    public string? StagedVersion => _staged?.Release.Version.ToString();
    public event Action? StateChanged;

    public AppUpdater(Func<AppUpdateSettings> settings, IReleaseSource releases, IFileDownloader downloader,
        IAuthenticodeVerifier signature, UpdateInstaller installer, IProcessLauncher launcher,
        string updatesDir, AppVersion running, string flavour, Action<string> setSkipped,
        bool? requireSignatureOnStable = null, ILog? log = null)
    {
        _settings = settings; _releases = releases; _downloader = downloader; _signature = signature;
        _installer = installer; _launcher = launcher; _updatesDir = updatesDir; _running = running;
        _flavour = flavour; _setSkipped = setSkipped;
        _requireSignature = requireSignatureOnStable ?? RequireSignatureOnStable;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>
    /// One check. <paramref name="manual"/> is an explicit user request: it works even in Off mode
    /// (as Notify, never auto-applying) and on a development build.
    /// </summary>
    public async Task<UpdateOutcome> CheckAsync(bool manual, CancellationToken ct)
    {
        var s = _settings();
        if (!manual && s.Mode == UpdateMode.Off) return Set(new UpdateOutcome(UpdateOutcomeKind.Disabled, Message: "App updates are off."));
        if (!manual && _running.IsDevBuild)
        {
            _log.Info("Development build: automatic update checks skipped");
            return Set(new UpdateOutcome(UpdateOutcomeKind.Disabled, Message: "Development build."));
        }

        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false))
            return Last ?? new UpdateOutcome(UpdateOutcomeKind.UpToDate, Message: "A check is already running.");
        try
        {
            if (_staged is { } st && File.Exists(st.ExePath))
                return Set(new UpdateOutcome(UpdateOutcomeKind.Staged, st.Release.Version.ToString(), st.Release.HtmlUrl));

            IReadOnlyList<ReleaseInfo> list;
            try { list = await _releases.GetReleasesAsync(s.Channel, ct).ConfigureAwait(false); }
            catch (UpdateCheckException ex)
            {
                _log.Warn("Update check failed: " + ex.Message);
                return Set(new UpdateOutcome(UpdateOutcomeKind.Failed, Message: ex.Message));
            }

            var chosen = ReleaseSelector.Choose(list, s.Channel, _running, s.SkippedVersion);
            if (chosen is null)
            {
                _log.Info($"No newer release (running {_running}, channel {s.Channel})");
                return Set(new UpdateOutcome(UpdateOutcomeKind.UpToDate, Message: "You are on the latest version."));
            }

            var mode = manual && s.Mode == UpdateMode.Off ? UpdateMode.Notify : s.Mode;
            if (manual && _running.IsDevBuild) mode = UpdateMode.Notify;
            var version = chosen.Version.ToString();
            var available = new UpdateOutcome(UpdateOutcomeKind.Available, version, chosen.HtmlUrl, $"Version {version} is available.");

            if (mode == UpdateMode.Notify) { _log.Info($"Update {version} available (notify mode)"); return Set(available); }

            if (!_installer.CanWrite)
            {
                _log.Warn($"Install directory not writable; offering {version} as notify-only");
                return Set(available with { Message = $"Version {version} is available, but PatchRadar's folder is read-only so it cannot update itself. Install per-user (see README) or update manually." });
            }

            var assets = AssetPicker.Pick(chosen, _flavour);
            if (assets is null)
            {
                _log.Warn($"Release {chosen.Tag} has no usable {_flavour} exe + .sha256 pair");
                return Set(new UpdateOutcome(UpdateOutcomeKind.Failed, version, chosen.HtmlUrl, $"Release {chosen.Tag} is missing the expected files."));
            }

            string exePath;
            try { exePath = await DownloadAndVerifyAsync(chosen, assets, s.Channel, ct).ConfigureAwait(false); }
            catch (UpdateCheckException ex)
            {
                _log.Error($"Update {version} rejected: {ex.Message}");
                return Set(new UpdateOutcome(UpdateOutcomeKind.Failed, version, chosen.HtmlUrl, ex.Message));
            }

            _staged = (chosen, exePath);
            if (!s.ApplyAutomatically)
            {
                _log.Info($"Update {version} verified and staged");
                return Set(new UpdateOutcome(UpdateOutcomeKind.Staged, version, chosen.HtmlUrl, $"Restart to update to v{version}."));
            }
            return ApplyStagedCore();
        }
        finally { _busy.Release(); }
    }

    /// <summary>Apply the staged update (tray: "Restart to update to vX").</summary>
    public UpdateOutcome ApplyStaged()
    {
        if (!_busy.Wait(0)) return Last ?? new UpdateOutcome(UpdateOutcomeKind.Failed, Message: "Busy.");
        try { return ApplyStagedCore(); }
        finally { _busy.Release(); }
    }

    private UpdateOutcome ApplyStagedCore()
    {
        if (_staged is not { } st || !File.Exists(st.ExePath))
            return Set(new UpdateOutcome(UpdateOutcomeKind.Failed, Message: "No staged update."));
        var version = st.Release.Version;
        var result = _installer.Apply(st.ExePath, _running, version);
        switch (result)
        {
            case ApplyResult.Applied:
                Set(new UpdateOutcome(UpdateOutcomeKind.Applied, version.ToString(), st.Release.HtmlUrl, $"Updating to v{version}…"));
                _launcher.StartAndExit(_installer.InstalledPath, "--post-update");
                return Last!;
            case ApplyResult.NotWritable:
                _staged = null;
                return Set(new UpdateOutcome(UpdateOutcomeKind.Available, version.ToString(), st.Release.HtmlUrl,
                    $"Version {version} is available, but PatchRadar's folder is read-only so it cannot update itself."));
            default:
                _staged = null;
                return Set(new UpdateOutcome(UpdateOutcomeKind.Failed, version.ToString(), st.Release.HtmlUrl,
                    "The update could not be installed. The current version is untouched. See the log."));
        }
    }

    private async Task<string> DownloadAndVerifyAsync(ReleaseInfo release, ChosenAssets assets, UpdateChannel channel, CancellationToken ct)
    {
        var dir = Path.Combine(_updatesDir, release.Tag);
        Directory.CreateDirectory(dir);
        var exePath = Path.Combine(dir, assets.Exe.Name);

        var shaText = await _downloader.DownloadTextAsync(assets.Sha256.Url, ct).ConfigureAwait(false);
        var expected = Sha256Check.ParseExpected(shaText, assets.Exe.Name)
            ?? throw new UpdateCheckException($"The checksum file for {assets.Exe.Name} is missing or malformed.");

        // A previous run may already have downloaded and verified this file.
        if (!(File.Exists(exePath) && Sha256Check.Matches(exePath, expected)))
        {
            await _downloader.DownloadAsync(assets.Exe.Url, exePath, ct).ConfigureAwait(false);
            if (!Sha256Check.Matches(exePath, expected))
            {
                TryDelete(exePath);
                throw new UpdateCheckException("Downloaded file failed its SHA-256 check (corrupt or tampered). Update aborted.");
            }
        }

        // Pre-releases are unsigned; the Stable channel requires a SignPath Foundation signature once enforced.
        if (channel == UpdateChannel.Stable && _requireSignature)
        {
            var sig = _signature.Verify(exePath);
            if (!sig.IsValid)
            {
                TryDelete(exePath);
                throw new UpdateCheckException($"Signature check failed ({sig.Status}): {sig.Detail ?? "no detail"}. Update aborted.");
            }
            _log.Info($"Signature OK: {sig.Signer}");
        }
        else _log.Info(channel == UpdateChannel.Stable ? "Signature check not enforced yet (RequireSignatureOnStable is false)" : "Pre-release channel: hash check only");
        return exePath;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Remember the user's "skip this version" choice.</summary>
    public void Skip(string version) => _setSkipped(version);

    private UpdateOutcome Set(UpdateOutcome o)
    {
        Last = o;
        StateChanged?.Invoke();
        return o;
    }
}
