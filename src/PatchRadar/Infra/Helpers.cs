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
    public static string KbUrl(string kb) => $"https://support.microsoft.com/help/{kb.Trim().ToUpperInvariant().Replace("KB", "")}";

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
        sb.AppendLine($"{u.Kb} - {u.Title}");
        sb.AppendLine($"Installed: {u.InstalledOn.LocalDateTime:yyyy-MM-dd HH:mm}   Risk: {row.Severity}");
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
            }
        }
        if (row.Kb is null || row.Kb.Status != KbStatus.Ok)
            sb.AppendLine().AppendLine("KB data unavailable: " + (row.Kb?.LastError ?? "not fetched yet"));
        sb.AppendLine().AppendLine(UrlGuard.KbUrl(u.Kb));
        return sb.ToString();
    }

    public static string MenuText(UpdateRow row, int maxTitle = 60)
    {
        var u = row.Update;
        var title = u.Title.Length > maxTitle ? u.Title[..(maxTitle - 1)] + "…" : u.Title;
        var date = u.InstalledOn.LocalDateTime.ToString("d MMM", CultureInfo.GetCultureInfo("en-GB"));
        return $"{u.Kb} · {date} · {title}";
    }
}
