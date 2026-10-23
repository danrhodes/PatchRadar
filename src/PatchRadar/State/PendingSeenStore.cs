namespace PatchRadar.State;

/// <summary>
/// Which pending updates the user has already looked at (by opening the tray menu). The icon badge counts only
/// the ones not seen yet, so it works as a reminder to look rather than a permanent number.
/// </summary>
public sealed class PendingSeenStore
{
    private readonly StateStore _store;
    private readonly Func<DateTime> _now;
    private HashSet<string> _seen;
    private readonly object _lock = new();

    public PendingSeenStore(StateStore store, Func<DateTime>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTime.UtcNow);
        _seen = new HashSet<string>(store.Load().PendingSeen.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsSeen(string key) { lock (_lock) return _seen.Contains(key); }

    /// <summary>
    /// Record that these are the pending updates the user has now seen. Anything no longer pending is dropped,
    /// so an update that goes away and comes back is flagged again. Returns true when something changed.
    /// </summary>
    public bool MarkSeen(IEnumerable<string> pendingKeys)
    {
        var keys = new HashSet<string>(pendingKeys, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (_seen.SetEquals(keys)) return false;
            _seen = keys;
        }
        _store.Update(s =>
        {
            s.PendingSeen = keys.ToDictionary(k => k, _ => _now(), StringComparer.OrdinalIgnoreCase);
        });
        return true;
    }
}
