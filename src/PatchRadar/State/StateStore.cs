using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.State;

/// <summary>Contents of %APPDATA%\PatchRadar\state.json. Unknown keys survive a round-trip.</summary>
public sealed class AppState
{
    /// <summary>KB -> content hash for which a "new red" toast has already been shown (or deliberately suppressed).</summary>
    public Dictionary<string, string> Notified { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return new AppState();
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), SettingsStore.JsonOptions) ?? new AppState();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Warn($"state.json unreadable ({ex.Message}); starting empty");
                return new AppState();
            }
        }
    }

    public void Save(AppState state)
    {
        lock (_lock)
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
}
