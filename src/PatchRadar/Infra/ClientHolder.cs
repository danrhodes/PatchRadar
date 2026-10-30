using PatchRadar.Settings;

namespace PatchRadar.Infra;

/// <summary>
/// Lazily builds an HttpClient from the network settings and rebuilds it after they change,
/// so a new proxy, timeout or User-Agent applies to the next request without a restart.
/// </summary>
public sealed class ClientHolder(Func<NetworkSettings, HttpClient> factory, Func<NetworkSettings> settings) : IDisposable
{
    private readonly object _lock = new();
    private HttpClient? _client;

    public HttpClient Get()
    {
        lock (_lock) return _client ??= factory(settings());
    }

    /// <summary>
    /// Drop the current client. In-flight requests finish on the old one, so it is disposed later rather than under them
    /// (every request is bounded by a timeout, so a few minutes is plenty); otherwise each settings change would leak a handler.
    /// </summary>
    public void Reset()
    {
        HttpClient? old;
        lock (_lock) { old = _client; _client = null; }
        if (old is not null)
            _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => { try { old.Dispose(); } catch { } });
    }

    public void Dispose()
    {
        lock (_lock) { _client?.Dispose(); _client = null; }
    }
}
