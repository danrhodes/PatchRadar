using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.AppUpdate;

/// <summary>
/// Schedules update checks (10 minutes after startup, then every checkIntervalHours) and turns outcomes into
/// toasts. The tray menu reads <see cref="Last"/> for its "Check / Restart to update" item.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(10);

    private readonly AppUpdater _updater;
    private readonly Func<AppUpdateSettings> _settings;
    private readonly Action<string, string, string?> _toast;   // title, message, release url to open on click
    private readonly ILog _log;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _firstDone;
    private string? _toastedVersion;

    public UpdateCoordinator(AppUpdater updater, Func<AppUpdateSettings> settings, Action<string, string, string?> toast, ILog? log = null)
    {
        _updater = updater;
        _settings = settings;
        _toast = toast;
        _log = log ?? NullLog.Instance;
        _updater.StateChanged += () => Changed?.Invoke();
    }

    public UpdateOutcome? Last => _updater.Last;
    public string? StagedVersion => _updater.StagedVersion;
    public event Action? Changed;

    public void Start() => Reschedule();

    public static TimeSpan? ComputeDelay(AppUpdateSettings s, bool firstDone)
    {
        if (s.Mode == UpdateMode.Off) return null;
        return firstDone ? TimeSpan.FromHours(Math.Max(1, s.CheckIntervalHours)) : FirstCheckDelay;
    }

    public void Reschedule()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed) return;      // a check that finishes after shutdown must not start a new timer
            var delay = ComputeDelay(_settings(), _firstDone);
            if (delay is null) return;
            _timer = new Timer(OnTick, null, delay.Value, Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnTick(object? _)
    {
        try
        {
            var o = await _updater.CheckAsync(manual: false, CancellationToken.None).ConfigureAwait(false);
            Announce(o, manual: false);
        }
        catch (Exception ex) { _log.Error("Scheduled update check failed", ex); }
        finally { _firstDone = true; Reschedule(); }
    }

    /// <summary>Tray: "Check for app update". Always tells the user what happened.</summary>
    public async Task CheckNowAsync()
    {
        try
        {
            var o = await _updater.CheckAsync(manual: true, CancellationToken.None).ConfigureAwait(false);
            Announce(o, manual: true);
        }
        catch (Exception ex)
        {
            _log.Error("Manual update check failed", ex);
            _toast("PatchRadar update check", "The check failed. See the log for details.", null);
        }
    }

    /// <summary>Tray: "Restart to update to vX".</summary>
    public void ApplyStaged()
    {
        var o = _updater.ApplyStaged();
        if (o.Kind != UpdateOutcomeKind.Applied) Announce(o, manual: true);
    }

    public void Skip(string version)
    {
        _updater.Skip(version);
        Changed?.Invoke();
    }

    private void Announce(UpdateOutcome o, bool manual)
    {
        switch (o.Kind)
        {
            case UpdateOutcomeKind.Available when manual || _toastedVersion != o.Version:
                _toastedVersion = o.Version;
                _toast("PatchRadar update available", o.Message ?? $"Version {o.Version} is available.", o.ReleaseUrl);
                break;
            case UpdateOutcomeKind.Staged when manual || _toastedVersion != o.Version:
                _toastedVersion = o.Version;
                _toast("PatchRadar update ready", $"Version {o.Version} is downloaded and verified. Use the tray menu: Restart to update.", null);
                break;
            case UpdateOutcomeKind.Applied:
                _toast("PatchRadar updating", o.Message ?? $"Updating to v{o.Version}. PatchRadar will restart.", null);
                break;
            case UpdateOutcomeKind.Failed when !manual:
                _toast("PatchRadar update problem", o.Message ?? "A scheduled update could not be installed. See the log.", null);
                break;
            case UpdateOutcomeKind.UpToDate when manual:
                _toast("PatchRadar", "You are on the latest version.", null);
                break;
            case UpdateOutcomeKind.Failed when manual:
                _toast("PatchRadar update check failed", o.Message ?? "See the log for details.", null);
                break;
            case UpdateOutcomeKind.Disabled when manual:
                _toast("PatchRadar", o.Message ?? "Updates are disabled.", null);
                break;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        lock (_lock) { _disposed = true; _timer?.Dispose(); _timer = null; }
    }
}
