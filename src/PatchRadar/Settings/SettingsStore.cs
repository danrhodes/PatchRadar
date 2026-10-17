using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PatchRadar.Infra;

namespace PatchRadar.Settings;

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly ILog _log;

    public SettingsStore(string path, ILog? log = null)
    {
        _path = path;
        _log = log ?? NullLog.Instance;
    }

    public string FilePath => _path;

    /// <summary>Load settings. Creates the file with defaults on first run. Never throws on bad content.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            var fresh = new AppSettings();
            TrySave(fresh);
            return fresh;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(_path));
            if (root is not JsonObject) throw new JsonException("Root is not an object");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"settings.json unreadable ({ex.Message}); backing up and resetting");
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
            var fresh = new AppSettings();
            TrySave(fresh);
            return fresh;
        }

        var obj = (JsonObject)root;
        var settings = new AppSettings();
        var extra = new Dictionary<string, JsonElement>();

        foreach (var (key, node) in obj)
        {
            try
            {
                switch (key.ToLowerInvariant())
                {
                    case "schemaversion": settings.SchemaVersion = node!.GetValue<int>(); break;
                    case "general": settings.General = Read<GeneralSettings>(node); break;
                    case "appupdate": settings.AppUpdate = Read<AppUpdateSettings>(node); break;
                    case "scanning": settings.Scanning = Read<ScanningSettings>(node); break;
                    case "updates": settings.Updates = Read<UpdatesSettings>(node); break;
                    case "severity": settings.Severity = Read<SeveritySettings>(node); break;
                    case "appearance": settings.Appearance = Read<AppearanceSettings>(node); break;
                    case "network": settings.Network = Read<NetworkSettings>(node); break;
                    case "advanced": settings.Advanced = Read<AdvancedSettings>(node); break;
                    default:
                        if (node is not null) extra[key] = JsonSerializer.SerializeToElement(node);
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                _log.Warn($"settings.json: invalid section '{key}' ({ex.Message}); using defaults");
            }
        }

        if (extra.Count > 0) settings.Extra = extra;
        foreach (var w in settings.Validate()) _log.Warn("settings.json: " + w);
        return settings;
    }

    private static T Read<T>(JsonNode? node) where T : new() =>
        node is null ? new T() : node.Deserialize<T>(JsonOptions) ?? new T();

    /// <summary>Atomic write: temp file then replace.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
    }

    private void TrySave(AppSettings s)
    {
        try { Save(s); }
        catch (Exception ex) { _log.Warn($"Could not write settings.json: {ex.Message}"); }
    }
}
