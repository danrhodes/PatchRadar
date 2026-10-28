using System.Diagnostics;
using System.Globalization;
using System.Text;
using PatchRadar.Core;
using PatchRadar.Scan;

namespace PatchRadar.Infra;

/// <summary>Named mutex so only one tray instance runs.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsFirst { get; }

    public SingleInstance(string name = @"Global\PatchRadar", TimeSpan? wait = null)
    {
        _mutex = new Mutex(false, name);
        try { IsFirst = _mutex.WaitOne(wait ?? TimeSpan.Zero); }
        catch (AbandonedMutexException) { IsFirst = true; }
    }

    public void Dispose()
    {
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}

public static class UrlGuard
{
    public static string KbUrl(string kb) =>
        kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? $"https://support.microsoft.com/help/{kb.Trim().ToUpperInvariant().Replace("KB", "")}" : "";

    /// <summary>Microsoft's page listing Defender antivirus platform, engine and security intelligence releases.</summary>
    public const string DefenderUpdatesUrl = "https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-updates";

    private static readonly System.Text.RegularExpressions.Regex DefenderTitle = new(
        @"Security Intelligence|Defender Antivirus|(Microsoft|Windows) Defender|Antimalware (platform|client)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsDefenderUpdate(InstalledUpdate u) => DefenderTitle.IsMatch(u.Title);

    /// <summary>Defender definition/platform updates often have a KB number whose support page 404s: Microsoft documents them on one page instead.</summary>
    public static bool UseDefenderPage(UpdateRow row) =>
        row.Update.HasKb && row.Kb?.Status == KbStatus.NotFound && IsDefenderUpdate(row.Update);

    /// <summary>Microsoft's explanation of servicing stack updates.</summary>
    public const string ServicingStackUrl = "https://learn.microsoft.com/en-gb/windows/deployment/update/servicing-stack-updates";

    /// <summary>
    /// A servicing stack update whose own article is missing (404): send the user to the update it shipped with, whose
    /// article describes it, or to Microsoft's servicing stack page when that update is unknown.
    /// </summary>
    public static bool UseSsuFallback(UpdateRow row) =>
        row.Update.HasKb && row.IsServicingStack && row.Kb?.Status == KbStatus.NotFound;

    /// <summary>Where "open the article" should go for this update.</summary>
    public static string ArticleUrl(UpdateRow row) =>
        UseDefenderPage(row) ? DefenderUpdatesUrl
        : UseSsuFallback(row) ? (row.SsuParentKb is { } parent ? KbUrl(parent) : ServicingStackUrl)
        : KbUrl(row.Update.Kb);

    /// <summary>Only https links on Microsoft hosts may be opened.</summary>
    public static bool IsSafe(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps &&
               (u.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                u.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase));
    }

    public static void OpenInBrowser(string? url, ILog? log = null)
    {
        if (!IsSafe(url)) { log?.Warn($"Refused to open URL: {url}"); return; }
        try { Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true }); }
        catch (Exception ex) { log?.Error("Could not open browser", ex); }
    }
}

public static class SummaryFormatter
{
    /// <summary>Plain-text summary suitable for pasting into a ticket.</summary>
    public static string ForTicket(UpdateRow row)
    {
        var sb = new StringBuilder();
        var u = row.Update;
        sb.AppendLine($"{(u.HasKb ? u.Kb + " - " : "")}{u.Title}");
        sb.AppendLine(row.IsPending
            ? $"Pending (not installed yet), offered since {u.InstalledOn.LocalDateTime:yyyy-MM-dd}   Risk: {row.Severity}"
            : $"Installed: {u.InstalledOn.LocalDateTime:yyyy-MM-dd HH:mm}   Risk: {row.Severity}");
        if (row.Kb?.OsBuild is { } b) sb.AppendLine($"OS build: {b}");
        if (row.Result.MatchedKeywords.Count > 0) sb.AppendLine("Matched: " + string.Join(", ", row.Result.MatchedKeywords));
        if (row.Result.BehaviourChanges.Count > 0)
        {
            sb.AppendLine().AppendLine("Behaviour changes:");
            foreach (var c in row.Result.BehaviourChanges) sb.AppendLine("- " + c.Text);
        }
        if (row.Result.ActiveIssues.Count > 0)
        {
            sb.AppendLine().AppendLine("Known issues:");
            foreach (var i in row.Result.ActiveIssues)
            {
                sb.AppendLine("- " + (string.IsNullOrEmpty(i.Title) ? i.Symptom : i.Title + ": " + i.Symptom));
                if (i.Workaround.Length > 0) sb.AppendLine("  Workaround: " + i.Workaround);
                if (i.Resolution.Length > 0) sb.AppendLine("  Resolution: " + i.Resolution);
                if (i.ResolvedInKb is not null) sb.AppendLine($"  {(i.PartialFix ? "Partial fix" : "Fix")}: {i.ResolvedInKb}{(i.OutOfBandFix ? " (out-of-band)" : "")}");
                else if (i.Workaround.Length == 0 && i.Resolution.Length == 0) sb.AppendLine("  No fix or workaround published yet");
            }
        }
        if (row.Kb is null || row.Kb.Status != KbStatus.Ok)
            sb.AppendLine().AppendLine(u.HasKb ? "KB data unavailable: " + (row.Kb?.LastError ?? "not fetched yet") : "No KB article is published for this update.");
        if (u.HasKb) sb.AppendLine().AppendLine(UrlGuard.ArticleUrl(row));
        return sb.ToString();
    }

    public static string MenuText(UpdateRow row, int maxTitle = 60)
    {
        var u = row.Update;
        var title = u.Title.Length > maxTitle ? u.Title[..(maxTitle - 1)] + "…" : u.Title;
        var date = u.InstalledOn.LocalDateTime.ToString("d MMM", CultureInfo.GetCultureInfo("en-GB"));
        return u.HasKb ? $"{u.Kb} · {date} · {title}" : $"{date} · {title}";
    }
}
