using System.Globalization;
using System.Text;
using System.Text.Json;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Scan;

namespace PatchRadar.Export;

public enum ExportFormat { Csv, Json, Markdown }

public static class Exporters
{
    public static string Extension(ExportFormat f) => f switch { ExportFormat.Csv => ".csv", ExportFormat.Json => ".json", _ => ".md" };

    public static string Render(ExportFormat f, IEnumerable<UpdateRow> rows) => f switch
    {
        ExportFormat.Csv => CsvExporter.Render(rows),
        ExportFormat.Json => JsonExporter.Render(rows),
        _ => MarkdownExporter.Render(rows),
    };

    internal static string Date(DateTimeOffset d) => d.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static string Result(UpdateRow r) => r.Update.State.ToString();
    internal static string Summary(UpdateRow r) => r.Result.Summary ?? "";
}

public static class CsvExporter
{
    public static readonly string[] Header =
        { "Severity", "KB", "Title", "Category", "Installed", "Result", "OS build", "Matched keywords", "Summary", "URL" };

    public static string Render(IEnumerable<UpdateRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header)).Append("\r\n");
        foreach (var r in rows)
        {
            var cells = new[]
            {
                r.Severity.ToString(), r.Update.Kb, r.Update.Title, r.Update.Category.ToString(), Exporters.Date(r.Update.InstalledOn),
                Exporters.Result(r), r.Kb?.OsBuild ?? "", string.Join("; ", r.Result.MatchedKeywords), Exporters.Summary(r), UrlGuard.KbUrl(r.Update.Kb),
            };
            sb.Append(string.Join(",", cells.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>RFC 4180 quoting, plus a leading apostrophe on cells a spreadsheet would run as a formula.</summary>
    public static string Escape(string v)
    {
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

public static class JsonExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Render(IEnumerable<UpdateRow> rows) => JsonSerializer.Serialize(rows.Select(r => new
    {
        severity = r.Severity.ToString(),
        kb = r.Update.Kb,
        title = r.Update.Title,
        category = r.Update.Category.ToString(),
        installed = r.Update.InstalledOn.ToString("o", CultureInfo.InvariantCulture),
        result = Exporters.Result(r),
        osBuild = r.Kb?.OsBuild,
        url = UrlGuard.KbUrl(r.Update.Kb),
        matchedKeywords = r.Result.MatchedKeywords,
        summary = r.Result.Summary,
        behaviourChanges = r.Result.BehaviourChanges.Select(c => c.Text),
        knownIssues = r.Result.ActiveIssues.Select(i => new { i.Title, i.AppliesTo, i.Symptom, i.Workaround }),
        kbStatus = r.Kb?.Status.ToString() ?? "NotFetched",
    }), Options);
}

public static class MarkdownExporter
{
    /// <summary>A GitHub-style table that pastes cleanly into most ticketing systems.</summary>
    public static string Render(IEnumerable<UpdateRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Risk | KB | Installed | Title | Notes |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var note = Exporters.Summary(r);
            if (r.Result.MatchedKeywords.Count > 0) note += (note.Length > 0 ? " " : "") + "(" + string.Join(", ", r.Result.MatchedKeywords) + ")";
            sb.Append("| ").Append(r.Severity)
              .Append(" | [").Append(r.Update.Kb).Append("](").Append(UrlGuard.KbUrl(r.Update.Kb)).Append(')')
              .Append(" | ").Append(Exporters.Date(r.Update.InstalledOn))
              .Append(" | ").Append(Cell(r.Update.Title))
              .Append(" | ").Append(Cell(note)).AppendLine(" |");
        }
        return sb.ToString();
    }

    private static string Cell(string s) =>
        s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
}
