using System.IO;
using System.Text.Json;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.Kb;

/// <summary>JSON-per-KB cache with TTL and failure back-off logic.</summary>
public sealed class KbCache
{
    private readonly string _dir;
    private readonly ILog _log;

    public KbCache(string dir, ILog? log = null)
    {
        _dir = dir;
        _log = log ?? NullLog.Instance;
    }

    private string PathFor(string kb) => Path.Combine(_dir, kb.ToUpperInvariant() + ".json");

    public KbData? Load(string kb)
    {
        var p = PathFor(kb);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<KbData>(File.ReadAllText(p), SettingsStore.JsonOptions); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn($"Cache for {kb} unreadable: {ex.Message}");
            return null;
        }
    }

    public IEnumerable<KbData> LoadAll()
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            KbData? d = null;
            try { d = JsonSerializer.Deserialize<KbData>(File.ReadAllText(f), SettingsStore.JsonOptions); }
            catch (Exception ex) when (ex is JsonException or IOException) { }
            if (d is not null) yield return d;
        }
    }

    public void Save(KbData data)
    {
        Directory.CreateDirectory(_dir);
        var p = PathFor(data.Kb);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, SettingsStore.JsonOptions));
        if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
    }

    /// <summary>Retry delay after N consecutive failures: 1 h, 6 h, then 24 h.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(6),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>Decide whether a KB should be fetched now. Never refetch inside the TTL.</summary>
    public static bool NeedsRefresh(KbData? cached, DateTimeOffset installedOn, DateTime nowUtc, ScanningSettings s, bool force)
    {
        if (cached is null) return true;
        var recent = (nowUtc - installedOn.UtcDateTime) <= TimeSpan.FromDays(s.RecentWindowDays);
        if (force && recent) return true;

        if (cached.ConsecutiveFailures > 0 && cached.NextRetryUtc is { } next)
            return nowUtc >= next;

        var ttl = recent ? TimeSpan.FromHours(s.CacheHoursRecent) : TimeSpan.FromDays(s.CacheDaysOlder);
        return nowUtc - cached.FetchedUtc >= ttl;
    }
}
