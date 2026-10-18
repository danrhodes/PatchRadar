using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.State;

public sealed class ReviewEntry
{
    public DateTime ReviewedUtc { get; set; }
    /// <summary>KB content hash at the time of review. A later, different hash means "changed since reviewed".</summary>
    public string ContentHash { get; set; } = "";
}

/// <summary>Contents of %APPDATA%\PatchRadar\state.json. Unknown keys survive a round-trip.</summary>
public sealed class AppState
{
    /// <summary>KB -> content hash for which a "new red" toast has already been shown (or deliberately suppressed).</summary>
    public Dictionary<string, string> Notified { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>KB -> review record (guide section 9).</summary>
    public Dictionary<string, ReviewEntry> Reviewed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the user last opened "What's changed".</summary>
    public DateTime? LastDigestSeenUtc { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly ILog _log;
    private readonly object _lock = new();

    public StateStore(string path, ILog? log = null)
    {
        _path = path;
        _log = log ?? NullLog.Instance;
    }

    public AppState Load()
    {
        lock (_lock) return LoadUnlocked();
    }

    /// <summary>
    /// Load, mutate and save under one lock, so two writers (toasts, review flags) never overwrite each other
    /// with a stale copy.
    /// </summary>
    public void Update(Action<AppState> change)
    {
        lock (_lock)
        {
            var state = LoadUnlocked();
            change(state);
            SaveUnlocked(state);
        }
    }

    public void Save(AppState state)
    {
        lock (_lock) SaveUnlocked(state);
    }

    private AppState LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path)) return new AppState();
            var s = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), SettingsStore.JsonOptions) ?? new AppState();
            s.Notified = new Dictionary<string, string>(s.Notified, StringComparer.OrdinalIgnoreCase);
            s.Reviewed = new Dictionary<string, ReviewEntry>(s.Reviewed, StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"state.json unreadable ({ex.Message}); starting empty");
            return new AppState();
        }
    }

    private void SaveUnlocked(AppState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, SettingsStore.JsonOptions));
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not write state.json: " + ex.Message);
        }
    }
}
