using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.Scan;

/// <summary>
/// Startup-delay then periodic scans. Call <see cref="Reschedule"/> after settings change; no restart needed.
/// Startup delay 0 = immediately. Interval 0 = no periodic scan.
/// </summary>
public sealed class ScanScheduler : IDisposable
{
    private readonly Func<CancellationToken, Task> _scan;
    private readonly Func<ScanningSettings> _settings;
    private readonly ILog _log;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _startupDone;

    public DateTime? NextScan { get; private set; }

    public ScanScheduler(Func<CancellationToken, Task> scan, Func<ScanningSettings> settings, ILog? log = null)
    {
        _scan = scan;
        _settings = settings;
        _log = log ?? NullLog.Instance;
    }

    public static TimeSpan? ComputeDelay(ScanningSettings s, bool startupDone)
    {
        if (!startupDone && s.ScanOnStartup) return TimeSpan.FromMinutes(Math.Max(0, s.StartupDelayMinutes));
        return s.ScanIntervalMinutes > 0 ? TimeSpan.FromMinutes(s.ScanIntervalMinutes) : null;
    }

    public void Reschedule()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            var delay = ComputeDelay(_settings(), _startupDone);
            if (delay is null) { NextScan = null; return; }
            NextScan = DateTime.Now + delay;
            _timer = new Timer(OnTick, null, delay.Value, Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnTick(object? _)
    {
        try { await _scan(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error("Scheduled scan failed", ex); }
        finally { _startupDone = true; Reschedule(); }
    }

    /// <summary>A manual scan restarts the interval clock.</summary>
    public void NotifyManualScan() { _startupDone = true; Reschedule(); }

    public void Dispose() { lock (_lock) { _timer?.Dispose(); _timer = null; } }
}
