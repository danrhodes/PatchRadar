using System.Text.RegularExpressions;
using PatchRadar.Core;
using PatchRadar.Settings;

namespace PatchRadar.Updates;

public interface IUpdateHistoryProvider
{
    Task<IReadOnlyList<InstalledUpdate>> GetAsync(CancellationToken ct);
}

/// <summary>Updates Windows Update has found but not installed. They come back as <see cref="UpdateState.Pending"/>.</summary>
public interface IPendingUpdateProvider
{
    /// <summary>online = ask Windows Update (or WSUS) over the network; otherwise read what Windows last found.</summary>
    Task<IReadOnlyList<InstalledUpdate>> GetAsync(bool online, CancellationToken ct);
}

/// <summary>Raw WUA search result row for a not-yet-installed update, before mapping.</summary>
public sealed record WuaPendingEntry(
    string Title, IReadOnlyList<string> KbArticleIds, DateTimeOffset Available,
    IReadOnlyList<string> Categories, string? SupportUrl);

/// <summary>Raw WUA history row, before mapping.</summary>
public sealed record WuaEntry(
    string Title, DateTimeOffset Date, int ResultCode, int Operation,
    IReadOnlyList<string> Categories, string? SupportUrl, int HResult = 0);

public static class UpdateMerger
{
    private static readonly Regex KbInParens = new(@"\(KB(\d{6,8})\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KbAnywhere = new(@"KB(\d{6,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractKb(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var m = KbInParens.Match(title);
        if (!m.Success) m = KbAnywhere.Match(title);
        return m.Success ? "KB" + m.Groups[1].Value : null;
    }

    public static UpdateCategory Classify(string title, IEnumerable<string>? categories)
    {
        var cats = string.Join(" ", categories ?? Array.Empty<string>());
        bool T(string s) => title.Contains(s, StringComparison.OrdinalIgnoreCase);
        bool C(string s) => cats.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (T("Security Intelligence Update") || T("Definition Update") || C("Definition Updates")) return UpdateCategory.Definition;
        if (T("Servicing Stack") || C("Servicing Stack")) return UpdateCategory.ServicingStack;
        if (T(".NET") || C(".NET")) return UpdateCategory.DotNet;
        if (C("Drivers") || C("Driver") || T("Driver")) return UpdateCategory.Driver;
        if (T("Office") || C("Office")) return UpdateCategory.Office;
        if (T("Cumulative Update")) return UpdateCategory.Cumulative;
        if (T("Security Update") || C("Security Updates")) return UpdateCategory.Security;
        return UpdateCategory.Other;
    }

    /// <summary>Map a WUA history row to an update, or null when it has no KB number.</summary>
    public static InstalledUpdate? FromWua(WuaEntry e)
    {
        var kb = ExtractKb(e.Title);
        if (kb is null) return null;
        var state = e.Operation == 2 && e.ResultCode is 2 or 3 ? UpdateState.Uninstalled
            : e.ResultCode is 2 or 3 ? UpdateState.Installed
            : UpdateState.Failed;
        if (e.Operation == 2 && state == UpdateState.Failed) state = UpdateState.Installed; // failed uninstall leaves it installed
        return new InstalledUpdate(kb, e.Title, e.Date, state, Classify(e.Title, e.Categories), e.SupportUrl, "WUA",
            state == UpdateState.Failed ? e.HResult : 0);
    }

    /// <summary>Map a WUA search result to a pending update, or null when it has no KB number.</summary>
    public static InstalledUpdate? FromPending(WuaPendingEntry e)
    {
        var id = e.KbArticleIds.Select(k => k?.Trim()).FirstOrDefault(k => !string.IsNullOrEmpty(k));
        var kb = ExtractKb(id is not null ? "KB" + id.Replace("KB", "", StringComparison.OrdinalIgnoreCase) : e.Title);
        // Most drivers have no KB. Key them by title so the same driver offered twice is one row.
        if (kb is null)
        {
            if (string.IsNullOrWhiteSpace(e.Title)) return null;
            kb = "WU-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(e.Title.Trim().ToLowerInvariant())))[..8];
        }
        return new InstalledUpdate(kb, e.Title, e.Available, UpdateState.Pending, Classify(e.Title, e.Categories), e.SupportUrl, "WUA");
    }

    /// <summary>Dedupe by KB, newest entry wins. On a tie WUA (richer data) wins.</summary>
    public static IReadOnlyList<InstalledUpdate> Merge(params IEnumerable<InstalledUpdate>[] sources)
    {
        var best = new Dictionary<string, InstalledUpdate>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in sources.SelectMany(s => s))
        {
            if (!best.TryGetValue(u.Kb, out var cur) || u.InstalledOn > cur.InstalledOn
                || (u.InstalledOn == cur.InstalledOn && u.Source == "WUA" && cur.Source != "WUA"))
                best[u.Kb] = u;
        }
        return best.Values.OrderByDescending(u => u.InstalledOn).ToList();
    }

    /// <summary>Apply history window, category, title regex and failed-install filters.</summary>
    public static IReadOnlyList<InstalledUpdate> Filter(IEnumerable<InstalledUpdate> updates, UpdatesSettings s, DateTimeOffset now, bool allTime = false)
    {
        var regexes = ExcludeRegexes(s);
        var cutoff = s.HistoryDays > 0 && !allTime ? now.AddDays(-s.HistoryDays) : DateTimeOffset.MinValue;

        return updates
            .Where(u => u.State != UpdateState.Uninstalled)
            .Where(u => s.ShowFailed || u.State != UpdateState.Failed)
            .Where(u => u.InstalledOn >= cutoff)
            .Where(u => s.IncludeCategories.Contains(u.Category))
            .Where(u => !regexes.Any(r => r.IsMatch(u.Title)))
            .OrderByDescending(u => u.InstalledOn)
            .ToList();
    }

    /// <summary>
    /// Pending updates keep the category and title filters but not the history window: an update that has
    /// waited a long time is exactly the one the user needs to hear about. Already-installed KBs are dropped.
    /// </summary>
    public static IReadOnlyList<InstalledUpdate> FilterPending(IEnumerable<InstalledUpdate> pending, UpdatesSettings s, Func<string, bool> isInstalled)
    {
        var regexes = ExcludeRegexes(s);
        return pending
            .Where(u => !isInstalled(u.Kb))
            .Where(u => s.IncludeCategories.Contains(u.Category))
            .Where(u => !regexes.Any(r => r.IsMatch(u.Title)))
            .GroupBy(u => u.Kb, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(u => u.InstalledOn)
            .ToList();
    }

    private static List<Regex> ExcludeRegexes(UpdatesSettings s)
    {
        var regexes = new List<Regex>();
        foreach (var rx in s.ExcludeTitleRegex)
        {
            try { regexes.Add(new Regex(rx, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))); } catch (ArgumentException) { }
        }
        return regexes;
    }
}
