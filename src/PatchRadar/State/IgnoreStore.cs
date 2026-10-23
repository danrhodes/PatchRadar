namespace PatchRadar.State;

public sealed class IgnoreEntry
{
    public DateTime IgnoredUtc { get; set; }
}

/// <summary>Updates the user has decided to stop worrying about. They stay listed but stop colouring the icon or raising toasts.</summary>
public sealed class IgnoreStore
{
    private readonly StateStore _store;
    private readonly Func<DateTime> _now;
    private readonly HashSet<string> _keys;
    private readonly object _lock = new();

    public IgnoreStore(StateStore store, Func<DateTime>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTime.UtcNow);
        _keys = new HashSet<string>(store.Load().Ignored.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsIgnored(string key) { lock (_lock) return _keys.Contains(key); }

    public void Set(string key, bool ignored)
    {
        lock (_lock)
        {
            if (ignored) _keys.Add(key); else _keys.Remove(key);
        }
        _store.Update(s =>
        {
            if (ignored) s.Ignored[key] = new IgnoreEntry { IgnoredUtc = _now() };
            else s.Ignored.Remove(key);
        });
    }
}
