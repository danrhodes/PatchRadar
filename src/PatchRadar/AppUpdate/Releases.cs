using System.IO;
using System.Net;
using System.Text.Json;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.AppUpdate;

public static class UpdateRepo
{
    public const string Owner = "danrhodes";
    public const string Name = "PatchRadar";
    public const string ApiBase = "https://api.github.com/repos/" + Owner + "/" + Name;
    public const string HtmlBase = "https://github.com/" + Owner + "/" + Name;

    /// <summary>Hosts a release asset may be downloaded from (including redirect hops).</summary>
    public static readonly string[] AllowedHosts =
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    public static bool IsAllowedHost(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A download URL is acceptable when it is https, on an allowed host, and (for github.com itself)
    /// inside this repository's release downloads. CDN hosts cannot be path-checked; they are only reached
    /// via URLs that came from this repo's release JSON.
    /// </summary>
    public static bool IsAllowedAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsAllowedHost(u)) return false;
        if (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return u.AbsolutePath.StartsWith($"/{Owner}/{Name}/releases/download/", StringComparison.OrdinalIgnoreCase);
        if (u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return u.AbsolutePath.StartsWith($"/repos/{Owner}/{Name}/releases/assets/", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Release page link for Notify mode. Only this repo's github.com pages open.</summary>
    public static bool IsAllowedReleasePage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        (u.AbsolutePath.Equals($"/{Owner}/{Name}", StringComparison.OrdinalIgnoreCase) ||
         u.AbsolutePath.StartsWith($"/{Owner}/{Name}/", StringComparison.OrdinalIgnoreCase));
}

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(string Tag, AppVersion Version, bool Prerelease, bool Draft, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets);

public sealed class UpdateCheckException(string message, Exception? inner = null) : Exception(message, inner);

public interface IReleaseSource
{
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(UpdateChannel channel, CancellationToken ct);
}

public static class ReleaseJson
{
    /// <summary>Parses either a single release object (/releases/latest) or an array (/releases). Bad entries are skipped.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ReleaseInfo>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var e in doc.RootElement.EnumerateArray()) Add(e, list);
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            Add(doc.RootElement, list);
        return list;
    }

    private static void Add(JsonElement e, List<ReleaseInfo> list)
    {
        try
        {
            var tag = e.GetProperty("tag_name").GetString();
            if (!AppVersion.TryParse(tag, out var ver)) return;
            var assets = new List<ReleaseAsset>();
            if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (name is null || url is null) continue;
                    assets.Add(new ReleaseAsset(name, url, a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0));
                }
            list.Add(new ReleaseInfo(tag!, ver,
                e.TryGetProperty("prerelease", out var p) && p.GetBoolean(),
                e.TryGetProperty("draft", out var d) && d.GetBoolean(),
                e.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                assets));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
    }
}

/// <summary>GitHub Releases API with ETag caching (a 304 does not count against the 60/h unauthenticated limit).</summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    private readonly Func<HttpClient> _http;
    private readonly string? _cacheDir;
    private readonly string _userAgent;
    private readonly ILog _log;

    public GitHubReleaseSource(HttpClient http, string userAgentVersion, string? cacheDir = null, ILog? log = null)
        : this(() => http, userAgentVersion, cacheDir, log) { }

    /// <summary>Client resolved per call so proxy changes in Settings apply without a restart.</summary>
    public GitHubReleaseSource(Func<HttpClient> http, string userAgentVersion, string? cacheDir = null, ILog? log = null)
    {
        _http = http;
        _cacheDir = cacheDir;
        _userAgent = "PatchRadar/" + userAgentVersion;
        _log = log ?? NullLog.Instance;
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(UpdateChannel channel, CancellationToken ct)
    {
        var url = channel == UpdateChannel.Stable ? UpdateRepo.ApiBase + "/releases/latest" : UpdateRepo.ApiBase + "/releases?per_page=10";
        var cacheKey = channel == UpdateChannel.Stable ? "stable" : "prerelease";
        var cached = LoadCache(cacheKey);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(_userAgent);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (cached?.ETag is { Length: > 0 } etag) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

        HttpResponseMessage resp;
        try { resp = await _http().SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateCheckException("Could not reach GitHub: " + ex.Message, ex);
        }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotModified when cached is not null:
                    _log.Debug("Update check: 304 Not Modified");
                    return ReleaseJson.Parse(cached.Body);
                case HttpStatusCode.OK:
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var list = ParseOrThrow(body);
                    SaveCache(cacheKey, new CacheEntry { ETag = resp.Headers.ETag?.ToString(), Body = body });
                    return list;
                case HttpStatusCode.NotFound when channel == UpdateChannel.Stable:
                    return Array.Empty<ReleaseInfo>();        // /releases/latest 404 = no full release yet
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    var remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var r) ? r.FirstOrDefault() : null;
                    var reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var rs) && long.TryParse(rs.FirstOrDefault(), out var epoch)
                        ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("HH:mm") : null;
                    throw new UpdateCheckException(
                        $"GitHub refused the request (HTTP {(int)resp.StatusCode}); rate limit remaining {remaining ?? "unknown"}" +
                        (reset is null ? "" : $", resets {reset}") + ". Will retry at the next scheduled check.");
                default:
                    throw new UpdateCheckException($"GitHub returned HTTP {(int)resp.StatusCode}.");
            }
        }
    }

    private static IReadOnlyList<ReleaseInfo> ParseOrThrow(string body)
    {
        try { return ReleaseJson.Parse(body); }
        catch (JsonException ex) { throw new UpdateCheckException("GitHub returned unreadable release data.", ex); }
    }

    private sealed class CacheEntry
    {
        public string? ETag { get; set; }
        public string Body { get; set; } = "";
    }

    private CacheEntry? LoadCache(string key)
    {
        if (_cacheDir is null) return null;
        try
        {
            var p = Path.Combine(_cacheDir, $"release-{key}.json");
            return File.Exists(p) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(p)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private void SaveCache(string key, CacheEntry e)
    {
        if (_cacheDir is null) return;
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(Path.Combine(_cacheDir, $"release-{key}.json"), JsonSerializer.Serialize(e));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Release cache write failed: " + ex.Message); }
    }
}

public static class ReleaseSelector
{
    /// <summary>
    /// Newest release that is allowed on this channel, newer than what is running, and not skipped.
    /// Never downgrades. Stable ignores pre-releases; PreRelease considers everything.
    /// </summary>
    public static ReleaseInfo? Choose(IEnumerable<ReleaseInfo> releases, UpdateChannel channel, AppVersion running, string? skippedVersion)
    {
        AppVersion.TryParse(skippedVersion, out var skipped);
        return releases
            .Where(r => !r.Draft)
            .Where(r => channel == UpdateChannel.PreRelease || (!r.Prerelease && !r.Version.IsPreRelease))
            .Where(r => r.Version > running)
            .Where(r => skipped is null || !r.Version.Equals(skipped))
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }
}

public sealed record ChosenAssets(ReleaseAsset Exe, ReleaseAsset Sha256);

public static class AssetPicker
{
    /// <summary>
    /// Self-contained build takes <c>PatchRadar-&lt;tag&gt;-win-x64.exe</c>, slim takes <c>-win-x64-slim.exe</c>.
    /// Both need the matching <c>.sha256</c> asset, and every URL must pass the allowlist.
    /// </summary>
    public static ChosenAssets? Pick(ReleaseInfo release, string flavour)
    {
        var exeName = flavour == "slim" ? $"PatchRadar-{release.Tag}-win-x64-slim.exe" : $"PatchRadar-{release.Tag}-win-x64.exe";
        var exe = release.Assets.FirstOrDefault(a => a.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        var sha = release.Assets.FirstOrDefault(a => a.Name.Equals(exeName + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (exe is null || sha is null) return null;
        if (!UpdateRepo.IsAllowedAssetUrl(exe.Url) || !UpdateRepo.IsAllowedAssetUrl(sha.Url)) return null;
        return new ChosenAssets(exe, sha);
    }
}
