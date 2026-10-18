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

    /// <summary>Drop the current client. In-flight requests finish on the old one; it is not disposed under them.</summary>
    public void Reset()
    {
        lock (_lock) _client = null;
    }

    public void Dispose()
    {
        lock (_lock) { _client?.Dispose(); _client = null; }
    }
}
