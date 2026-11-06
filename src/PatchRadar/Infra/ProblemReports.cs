using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PatchRadar.AppUpdate;

namespace PatchRadar.Infra;

/// <summary>One logged error that carries an exception stack, plus the log lines just before it.</summary>
public sealed record ProblemEntry(DateTime Time, string Text, IReadOnlyList<string> Context);

/// <summary>A stable fingerprint for a bug: the exception type and the top PatchRadar stack frames. No messages, paths or line numbers.</summary>
public static partial class ProblemHash
{
    [GeneratedRegex(@"^\s*at\s+(?<m>[^\r\n(]+)", RegexOptions.Multiline)]
    private static partial Regex Frame();

    [GeneratedRegex(@"(?<![\w.])(?<t>(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*(?:Exception|Error))(?=[:\s]|$)")]
    private static partial Regex ExceptionType();

    /// <summary>Exceptions that mean "offline" or "cancelled", not a bug in PatchRadar.</summary>
    private static readonly string[] Noise =
    {
        "System.Net.Http.HttpRequestException", "System.Threading.Tasks.TaskCanceledException", "System.OperationCanceledException",
        "System.Net.Sockets.SocketException", "System.Net.WebException", "System.TimeoutException",
    };

    public static string? TypeOf(string text) => ExceptionType().Match(text) is { Success: true } m ? m.Groups["t"].Value : null;

    public static bool IsNoise(string text) => TypeOf(text) is { } t && Noise.Contains(t, StringComparer.Ordinal);

    public static bool HasStack(string text) => Frame().IsMatch(text);

    public static string Compute(string text)
    {
        var frames = Frame().Matches(text).Select(m => m.Groups["m"].Value.Trim()).ToList();
        var own = frames.Where(f => f.StartsWith("PatchRadar", StringComparison.Ordinal)).ToList();
        var key = (TypeOf(text) ?? "unknown") + "|" + string.Join("|", (own.Count > 0 ? own : frames).Take(5));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
    }
}

/// <summary>Removes what identifies the person or the machine before anything is shown or sent.</summary>
public static partial class ProblemScrubber
{
    [GeneratedRegex(@"(?i)[A-Z]:\\Users\\[^\\\s:""']+")] private static partial Regex ProfilePath();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")] private static partial Regex Email();
    [GeneratedRegex(@"\b\d{1,3}(\.\d{1,3}){3}\b")] private static partial Regex Ip();
    [GeneratedRegex(@"(?i)(?<=://)[^/\s:@]+:[^/\s@]*@")] private static partial Regex UrlCredentials();
    [GeneratedRegex(@" in (?:[A-Za-z]:)?[^\s:]*\\(?<f>[^\\\s:]+):line (?<n>\d+)")] private static partial Regex FramePath();

    public static string Scrub(string text, string user, string machine, string? domain = null)
    {
        var s = UrlCredentials().Replace(text, "<credentials>@");
        s = ProfilePath().Replace(s, @"C:\Users\<user>");
        s = FramePath().Replace(s, " in ${f}:line ${n}");
        s = Email().Replace(s, "<email>");
        s = Ip().Replace(s, "<ip>");
        s = Replace(s, machine, "<machine>");
        if (!string.IsNullOrEmpty(domain) && !domain.Equals(machine, StringComparison.OrdinalIgnoreCase)) s = Replace(s, domain, "<domain>");
        return Replace(s, user, "<user>");
    }

    public static string ScrubCurrent(string text) => Scrub(text, Environment.UserName, Environment.MachineName, Environment.UserDomainName);

    private static string Replace(string s, string name, string with) =>
        name.Length < 3 ? s : Regex.Replace(s, @"(?<![\w])" + Regex.Escape(name) + @"(?![\w])", with, RegexOptions.IgnoreCase);
}

public static partial class LogScanner
{
    [GeneratedRegex(@"^(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[(?<l>\w+)\] (?<m>.*)$")]
    private static partial Regex Line();

    /// <summary>Errors since <paramref name="sinceLocal"/> that have a stack trace and are not "offline" noise.</summary>
    public static IReadOnlyList<ProblemEntry> Find(string dir, DateTime sinceLocal, int contextLines = 8)
    {
        var result = new List<ProblemEntry>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in new DirectoryInfo(dir).GetFiles("patchradar-*.log").OrderBy(f => f.Name))
        {
            if (file.LastWriteTime < sinceLocal) continue;
            string[] lines;
            try { lines = ReadShared(file.FullName); }
            catch (IOException) { continue; }
            result.AddRange(Parse(lines, sinceLocal, contextLines));
        }
        return result;
    }

    public static IEnumerable<ProblemEntry> Parse(IReadOnlyList<string> lines, DateTime sinceLocal, int contextLines = 8)
    {
        var headers = new List<(int Index, DateTime Time, string Level)>();
        for (var i = 0; i < lines.Count; i++)
            if (Line().Match(lines[i]) is { Success: true } m && DateTime.TryParse(m.Groups["t"].Value, out var t))
                headers.Add((i, t, m.Groups["l"].Value));

        for (var h = 0; h < headers.Count; h++)
        {
            var (index, time, level) = headers[h];
            if (level != "Error" || time < sinceLocal) continue;
            var end = h + 1 < headers.Count ? headers[h + 1].Index : lines.Count;
            var text = string.Join("\n", lines.Skip(index).Take(end - index)).TrimEnd();
            if (!ProblemHash.HasStack(text) || ProblemHash.IsNoise(text)) continue;
            var ctx = lines.Skip(Math.Max(0, index - contextLines)).Take(Math.Min(contextLines, index)).ToList();
            yield return new ProblemEntry(time, text, ctx);
        }
    }

    private static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } l) lines.Add(l);
        return lines.ToArray();
    }
}

public enum ProblemStatus { Offered, Reported, Dismissed }

/// <summary>What has already been offered to the user, so no problem is raised twice.</summary>
public sealed class ProblemStore(string path, ILog? log = null)
{
    private sealed class Data
    {
        public DateTime LastScanUtc { get; set; }
        public Dictionary<string, Item> Seen { get; set; } = new();
    }

    public sealed class Item
    {
        public ProblemStatus Status { get; set; }
        public DateTime AtUtc { get; set; }
        public int Prompts { get; set; }
        public int? Issue { get; set; }
    }

    private readonly ILog _log = log ?? NullLog.Instance;
    private Data _d = new();
    private bool _loaded;

    private Data Load()
    {
        if (_loaded) return _d;
        _loaded = true;
        try { if (File.Exists(path)) _d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { _log.Warn("problems.json unreadable: " + ex.Message); _d = new(); }
        return _d;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_d));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Could not save problems.json: " + ex.Message); }
    }

    public DateTime LastScanUtc { get => Load().LastScanUtc; set { Load().LastScanUtc = value; Save(); } }
    public Item? Get(string hash) => Load().Seen.GetValueOrDefault(hash);

    public void Set(string hash, ProblemStatus status, int? issue = null)
    {
        var d = Load();
        var item = d.Seen.GetValueOrDefault(hash) ?? new Item();
        item.Status = status; item.AtUtc = DateTime.UtcNow; item.Issue ??= issue;
        if (status == ProblemStatus.Offered) item.Prompts++;
        d.Seen[hash] = item;
        Save();
    }

    /// <summary>A problem to raise now: never seen, or offered but not acted on and last offered over a day ago (at most 3 times).</summary>
    public bool ShouldOffer(string hash, DateTime nowUtc)
    {
        var item = Get(hash);
        if (item is null) return true;
        return item.Status == ProblemStatus.Offered && item.Prompts < 3 && nowUtc - item.AtUtc > TimeSpan.FromDays(1);
    }
}

public sealed record ProblemReport(string Hash, string Title, string Body, DateTime Time);

/// <summary>One distinct problem in the log: how often it occurred, when last, and what the user did with it before (null = never offered).</summary>
public sealed record ProblemGroup(ProblemReport Report, int Count, DateTime Last, ProblemStatus? Status);

public sealed record ExistingIssue(int Number, string Url, bool Open);

public static class ProblemIssue
{
    public const int MaxUrlLength = 7000;

    public static ProblemReport Build(ProblemEntry e, Func<string, string> scrub, string version, string os)
    {
        var hash = ProblemHash.Compute(e.Text);
        var error = scrub(e.Text);
        var headline = error.Split('\n')[0];
        var type = ProblemHash.TypeOf(e.Text) ?? "Error";
        var title = $"[problem {hash}] {type}";
        var sb = new StringBuilder();
        sb.AppendLine("Reported from PatchRadar after the user reviewed this text. Personal names and paths were removed.");
        sb.AppendLine();
        sb.AppendLine($"- Problem hash: `{hash}`");
        sb.AppendLine($"- PatchRadar: {version}");
        sb.AppendLine($"- Windows: {os}");
        sb.AppendLine($"- Logged: {e.Time:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("### Error");
        sb.AppendLine("```");
        sb.AppendLine(error);
        sb.AppendLine("```");
        if (e.Context.Count > 0)
        {
            sb.AppendLine("### Log lines before it");
            sb.AppendLine("```");
            foreach (var c in e.Context) sb.AppendLine(scrub(c));
            sb.AppendLine("```");
        }
        sb.AppendLine("### What were you doing? (optional)");
        return new ProblemReport(hash, title, sb.ToString(), e.Time);
    }

    /// <summary>The "new issue" page with the report filled in. The body is cut to fit a URL; the full text stays on screen.</summary>
    public static string NewIssueUrl(ProblemReport r)
    {
        var body = r.Body;
        string Url(string b) => $"{UpdateRepo.HtmlBase}/issues/new?title={Uri.EscapeDataString(r.Title)}&body={Uri.EscapeDataString(b)}";
        var url = Url(body);
        while (url.Length > MaxUrlLength && body.Length > 400)
        {
            body = body[..(int)(body.Length * 0.85)];
            url = Url(body + "\n...(trimmed to fit a link)");
        }
        return url;
    }

    public static bool IsIssuePage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        u.AbsolutePath.StartsWith($"/{UpdateRepo.Owner}/{UpdateRepo.Name}/issues", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Looks for an issue that already carries this hash. Unauthenticated; any failure just means "unknown".</summary>
public sealed class ProblemLookup(Func<HttpClient> client, ILog? log = null)
{
    private readonly ILog _log = log ?? NullLog.Instance;

    public async Task<ExistingIssue?> FindAsync(string hash, CancellationToken ct)
    {
        try
        {
            var q = Uri.EscapeDataString($"repo:{UpdateRepo.Owner}/{UpdateRepo.Name} is:issue {hash} in:title");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/search/issues?q={q}&per_page=5");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await client().SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { _log.Info($"Issue search returned {(int)resp.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ParseFirst(doc.RootElement, hash);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            _log.Info("Issue search failed: " + ex.Message);
            return null;
        }
    }

    public static ExistingIssue? ParseFirst(JsonElement root, string hash)
    {
        if (!root.TryGetProperty("items", out var items)) return null;
        foreach (var it in items.EnumerateArray())
        {
            var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (!title.Contains(hash, StringComparison.OrdinalIgnoreCase)) continue;
            var url = it.GetProperty("html_url").GetString();
            if (!ProblemIssue.IsIssuePage(url)) continue;
            return new ExistingIssue(it.GetProperty("number").GetInt32(), url!, it.GetProperty("state").GetString() == "open");
        }
        return null;
    }
}
