using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Win32;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.Kb;

/// <summary>Optional second pass over a KB that source A already parsed.</summary>
public interface IKbEnricher
{
    bool IsEnabled { get; }
    Task EnrichAsync(KbData data, CancellationToken ct);
}

/// <summary>One row of the "Known issues" table on a Windows release health status page.</summary>
public sealed class RhIssue
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string OriginatingKb { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ResolvedInKb { get; set; }
    public bool IsResolved => Status.StartsWith("Resolved", StringComparison.OrdinalIgnoreCase);
}

public sealed class RhPage
{
    public string Url { get; set; } = "";
    public string Heading { get; set; } = "";
    public DateTime FetchedUtc { get; set; }
    public List<RhIssue> Issues { get; set; } = new();
}

public static class ReleaseHealthParser
{
    private static readonly Regex KbRx = new(@"KB\d{6,8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Finds the table whose header row has Summary / Originating update / Status columns (by header text, not class)
    /// and returns one issue per row.
    /// </summary>
    public static RhPage Parse(string html, string url, DateTime fetchedUtc)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var page = new RhPage { Url = url, FetchedUtc = fetchedUtc, Heading = T(doc.DocumentNode.SelectSingleNode("//h1")) };

        foreach (var table in doc.DocumentNode.Descendants("table"))
        {
            var headers = table.Descendants("th").Select(T).ToList();
            var iSummary = headers.FindIndex(h => h.StartsWith("summary", StringComparison.OrdinalIgnoreCase));
            var iOrigin = headers.FindIndex(h => h.StartsWith("originating", StringComparison.OrdinalIgnoreCase));
            var iStatus = headers.FindIndex(h => h.StartsWith("status", StringComparison.OrdinalIgnoreCase));
            if (iSummary < 0 || iOrigin < 0 || iStatus < 0) continue;

            foreach (var tr in table.Descendants("tr"))
            {
                var cells = tr.Elements("td").ToList();
                if (cells.Count <= Math.Max(iOrigin, iStatus) || cells.Count <= iSummary) continue;

                var summary = cells[iSummary];
                var titleNode = summary.Descendants("b").FirstOrDefault() ?? summary.Descendants("a").FirstOrDefault();
                var title = T(titleNode);
                var full = T(summary);
                var desc = title.Length > 0 && full.StartsWith(title, StringComparison.Ordinal) ? full[title.Length..].Trim() : full;
                if (title.Length == 0) title = full;
                if (title.Length == 0) continue;

                var origin = KbRx.Match(T(cells[iOrigin]));
                if (!origin.Success) continue;

                var statusText = T(cells[iStatus]);
                var statusWord = statusText.Split(' ', 2)[0];
                var resolvedIn = KbRx.Match(statusText);

                page.Issues.Add(new RhIssue
                {
                    Title = title,
                    Description = desc,
                    OriginatingKb = origin.Value.ToUpperInvariant(),
                    Status = statusWord,
                    ResolvedInKb = resolvedIn.Success ? resolvedIn.Value.ToUpperInvariant() : null,
                });
            }
        }
        return page;
    }

    // Text nodes are joined with a space: <br> would otherwise glue "KB5124010" to the date after it.
    private static string T(HtmlNode? n)
    {
        if (n is null) return "";
        var parts = n.DescendantsAndSelf().OfType<HtmlTextNode>().Select(t => HtmlEntity.DeEntitize(t.Text));
        return Ws.Replace(string.Join(" ", parts).Replace(' ', ' ').Replace("​", ""), " ").Trim();
    }
}

/// <summary>What the machine is running; decides which release health pages apply.</summary>
public sealed record OsInfo(int Build, bool IsServer, string? DisplayVersion)
{
    public static OsInfo Current()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
            var server = string.Equals(k?.GetValue("InstallationType") as string, "Server", StringComparison.OrdinalIgnoreCase)
                         || ((k?.GetValue("ProductName") as string) ?? "").Contains("Server", StringComparison.OrdinalIgnoreCase);
            return new OsInfo(build, server, k?.GetValue("DisplayVersion") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return new OsInfo(Environment.OSVersion.Version.Build, false, null);
        }
    }
}

public static class ReleaseHealthPages
{
    private const string Root = "https://learn.microsoft.com/en-us/windows/release-health/";

    /// <summary>Status page(s) for this OS. Empty when the OS is not one we know (never guess a URL).</summary>
    public static IReadOnlyList<string> UrlsFor(OsInfo os)
    {
        string? slug;
        if (os.IsServer)
        {
            slug = os.Build switch
            {
                14393 => "status-windows-10-1607-and-windows-server-2016",
                17763 => "status-windows-10-1809-and-windows-server-2019",
                20348 => "status-windows-server-2022",
                >= 26100 => "status-windows-server-2025",
                _ => null,
            };
        }
        else if (os.Build >= 22000)
        {
            var dv = os.DisplayVersion?.Trim().ToLowerInvariant();
            slug = dv is not null && Regex.IsMatch(dv, @"^\d\dh[12]$")
                ? $"status-windows-11-{dv}"
                : os.Build switch
                {
                    22000 => "status-windows-11-21h2",
                    22621 => "status-windows-11-22h2",
                    22631 => "status-windows-11-23h2",
                    26100 => "status-windows-11-24h2",
                    26200 => "status-windows-11-25h2",
                    _ => null,
                };
        }
        else slug = null;
        return slug is null ? Array.Empty<string>() : new[] { Root + slug };
    }
}

/// <summary>
/// Source B. Known issues from the Windows release health status page, matched by originating KB.
/// Works as a fallback when the support article is blocked and as enrichment when it is not.
/// </summary>
public sealed class ReleaseHealthSource : IKbSource, IKbEnricher
{
    public static readonly TimeSpan PageTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);

    private readonly Func<HttpClient> _http;
    private readonly RequestGate _gate;
    private readonly Func<OsInfo> _os;
    private readonly Func<bool> _enabled;
    private readonly string? _cacheDir;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, (RhPage? Page, DateTime At)> _memory = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Name => "learn.microsoft.com (release health)";
    public bool IsEnabled => _enabled();

    public ReleaseHealthSource(Func<HttpClient> http, RequestGate gate, Func<OsInfo> os, Func<bool>? enabled = null,
        string? cacheDir = null, ILog? log = null, Func<DateTime>? now = null)
    {
        _http = http; _gate = gate; _os = os; _enabled = enabled ?? (() => true);
        _cacheDir = cacheDir; _log = log ?? NullLog.Instance; _now = now ?? (() => DateTime.UtcNow);
    }

    public async Task<KbData> FetchAsync(string kb, CancellationToken ct)
    {
        var id = kb.ToUpperInvariant();
        var matches = await IssuesForAsync(id, ct).ConfigureAwait(false);
        if (matches.Count == 0)
            return new KbData { Kb = id, Source = Name, FetchedUtc = _now(), Status = KbStatus.Failed, LastError = "Not mentioned on the Windows release health page (that page only lists updates with known issues, so this is normal)" };

        var d = new KbData
        {
            Kb = id, Source = Name, FetchedUtc = _now(), Status = KbStatus.Ok, Structure = KbStructure.Standard,
            CanonicalUrl = matches[0].Url,
            KnownIssues = matches.Select(m => ToIssue(m.Issue, m.Heading)).ToList(),
            Notice = "The Microsoft support article could not be read. Showing known issues from Windows release health only; improvements are not available.",
        };
        d.ContentHash = KbParser.Hash(d);
        return d;
    }

    public async Task EnrichAsync(KbData data, CancellationToken ct)
    {
        var matches = await IssuesForAsync(data.Kb, ct).ConfigureAwait(false);
        var added = false;
        foreach (var (issue, heading, _) in matches)
        {
            if (AlreadyListed(data, issue)) continue;
            data.KnownIssues.Add(ToIssue(issue, heading));
            added = true;
        }
        if (added) data.ContentHash = KbParser.Hash(data);
    }

    private static bool AlreadyListed(KbData data, RhIssue issue)
    {
        var t = Norm(issue.Title);
        return data.KnownIssues.Any(i => Norm(i.Title ?? "") == t || Norm(i.Symptom).Contains(t) || Norm(i.Title ?? "").Contains(t));
    }

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static KbIssue ToIssue(RhIssue r, string heading) => new()
    {
        Title = r.Title,
        AppliesTo = heading.Length > 0 ? heading : null,
        Symptom = r.Description,
        Workaround = r.IsResolved ? "" : $"Status: {r.Status}.",
        Resolution = r.IsResolved ? "Resolved" + (r.ResolvedInKb is null ? "." : $" in {r.ResolvedInKb}.") : "",
        ResolvedInKb = r.IsResolved ? r.ResolvedInKb : null,
        Status = r.IsResolved ? IssueStatus.Resolved : IssueStatus.Active,
    };

    private async Task<List<(RhIssue Issue, string Heading, string Url)>> IssuesForAsync(string kb, CancellationToken ct)
    {
        var result = new List<(RhIssue, string, string)>();
        foreach (var url in ReleaseHealthPages.UrlsFor(_os()))
        {
            var page = await GetPageAsync(url, ct).ConfigureAwait(false);
            if (page is null) continue;
            foreach (var i in page.Issues.Where(i => i.OriginatingKb == kb)) result.Add((i, page.Heading, url));
        }
        return result;
    }

    private async Task<RhPage?> GetPageAsync(string url, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _now();
            if (_memory.TryGetValue(url, out var m) && now - m.At < (m.Page is null ? FailureTtl : PageTtl)) return m.Page;

            var disk = LoadDisk(url);
            if (disk is not null && now - disk.FetchedUtc < PageTtl) { _memory[url] = (disk, now); return disk; }

            try
            {
                var (code, body) = await _gate.RunAsync(async () =>
                {
                    using var resp = await _http().GetAsync(url, ct).ConfigureAwait(false);
                    return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                }, ct).ConfigureAwait(false);

                if (code == HttpStatusCode.OK)
                {
                    var page = ReleaseHealthParser.Parse(body, url, now);
                    if (page.Issues.Count == 0 && !body.Contains("Known issues", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn($"Release health page layout not recognised: {url}");
                    }
                    else
                    {
                        _memory[url] = (page, now);
                        SaveDisk(page);
                        return page;
                    }
                }
                else _log.Warn($"Release health {url}: HTTP {(int)code}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.Warn($"Release health {url}: {ex.Message}");
            }

            // Fetch failed: serve a stale disk copy rather than nothing, and don't retry for an hour.
            _memory[url] = (disk, now);
            return disk;
        }
        finally { _lock.Release(); }
    }

    private string? DiskPath(string url) =>
        _cacheDir is null ? null : Path.Combine(_cacheDir, Path.GetFileName(new Uri(url).AbsolutePath) + ".json");

    private RhPage? LoadDisk(string url)
    {
        var p = DiskPath(url);
        if (p is null || !File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<RhPage>(File.ReadAllText(p), SettingsStore.JsonOptions); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private void SaveDisk(RhPage page)
    {
        var p = DiskPath(page.Url);
        if (p is null) return;
        try
        {
            Directory.CreateDirectory(_cacheDir!);
            File.WriteAllText(p, JsonSerializer.Serialize(page, SettingsStore.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Release health cache write failed: " + ex.Message); }
    }
}
