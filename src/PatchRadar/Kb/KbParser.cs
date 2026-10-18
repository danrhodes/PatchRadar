using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PatchRadar.Core;

namespace PatchRadar.Kb;

/// <summary>
/// Parses a Microsoft KB article into <see cref="KbData"/>. Sections are located by heading text,
/// never by CSS class. All output is plain text.
/// </summary>
public static class KbParser
{
    private static readonly Regex NoIssues = new(@"not currently aware of any issues", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResolvedRx = new(
        @"((this|the) issue (has been|was|is) (now )?resolved|^\s*resolved\b|\bresolved:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PartialRx = new(@"partially resolved", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OsBuildRx = new(@"OS Builds?\s+(\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"^\s*([A-Z][a-z]+ \d{1,2}, \d{4})", RegexOptions.Compiled);
    private static readonly Regex LeadingTag = new(@"^\s*\[(?<t>[^\]]+)\]\s*(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    public static KbData Parse(string kb, string html, string source, string? canonicalUrl, DateTime fetchedUtc)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var data = new KbData
        {
            Kb = kb, Source = source, CanonicalUrl = canonicalUrl, FetchedUtc = fetchedUtc, Status = KbStatus.Ok,
        };

        var title = Text(doc.DocumentNode.SelectSingleNode("//h1")) ;
        if (string.IsNullOrEmpty(title))
            title = Text(doc.DocumentNode.SelectSingleNode("//title")).Replace(" | Microsoft Support", "");
        data.Title = title;
        var tm = OsBuildRx.Match(title);
        if (tm.Success) data.OsBuild = tm.Groups[1].Value;
        var dm = DateRx.Match(title);
        if (dm.Success) data.ReleaseDate = dm.Groups[1].Value;
        var dash = title.IndexOfAny(new[] { '—', '–' });
        data.AppliesTo = dash >= 0 && dash + 1 < title.Length ? title[(dash + 1)..].Trim() : null;

        var root = doc.DocumentNode.SelectSingleNode("//main") ?? doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        data.RawText = Clean(Text(root));

        var improvementsHead = FindHeading(root, h => h.StartsWith("improvements", StringComparison.OrdinalIgnoreCase));
        var knownHead = FindHeading(root, h => h.StartsWith("known issues", StringComparison.OrdinalIgnoreCase));
        var summaryHead = improvementsHead is null
            ? FindHeading(root, h => h.Equals("summary", StringComparison.OrdinalIgnoreCase) || h.Equals("highlights", StringComparison.OrdinalIgnoreCase))
            : null;

        data.Structure = improvementsHead is not null || knownHead is not null ? KbStructure.Standard : KbStructure.Unknown;

        // Empty shell / bot challenge: nothing recognisable and almost no text.
        // A real article has an h1 and a "| Microsoft Support" title even when it has no KB sections
        // (platform and app updates). Only a page with neither is treated as a challenge or empty shell.
        var looksLikeArticle = title.Length > 0 && doc.DocumentNode.SelectSingleNode("//h1") is not null;
        if (improvementsHead is null && knownHead is null && summaryHead is null && !looksLikeArticle && data.RawText.Length < 1500)
        {
            data.Status = KbStatus.Blocked;
            data.LastError = "Page has no recognisable KB content (possible bot challenge or empty shell)";
            return data;
        }

        var impHead = improvementsHead ?? summaryHead;
        if (impHead is not null) data.Improvements = ParseImprovements(SectionNodes(impHead));
        else data.Improvements = ParseFreeText(root);

        if (knownHead is not null) data.KnownIssues = ParseKnownIssues(SectionNodes(knownHead));

        data.ContentHash = Hash(data);
        return data;
    }

    // ---- headings & sections -------------------------------------------------------------

    private static int Level(HtmlNode n) =>
        n.Name.Length == 2 && n.Name[0] == 'h' && char.IsDigit(n.Name[1]) ? n.Name[1] - '0' : 0;

    private static bool IsBoldPara(HtmlNode n)
    {
        if (n.Name != "p") return false;
        var kids = n.ChildNodes.Where(c => !(c is HtmlTextNode t && string.IsNullOrWhiteSpace(t.Text))).ToList();
        return kids.Count == 1 && (kids[0].Name is "strong" or "b");
    }

    private static HtmlNode? FindHeading(HtmlNode root, Func<string, bool> match)
    {
        foreach (var n in root.Descendants())
        {
            if (Level(n) is >= 1 and <= 4 || IsBoldPara(n))
            {
                var text = Clean(Text(n)).TrimEnd(':');
                if (text.Length is > 0 and < 80 && match(text)) return n;
            }
        }
        return null;
    }

    private static List<HtmlNode> SectionNodes(HtmlNode heading)
    {
        var level = Level(heading) > 0 ? Level(heading) : 7;
        var nodes = new List<HtmlNode>();
        for (var n = heading.NextSibling; n is not null; n = n.NextSibling)
        {
            if (n.NodeType != HtmlNodeType.Element) continue;
            var l = Level(n);
            if (l > 0 && l <= Math.Min(level, 6)) break;
            nodes.Add(n);
        }
        return nodes;
    }

    // ---- improvements ----------------------------------------------------------------------

    private static List<KbImprovement> ParseImprovements(List<HtmlNode> nodes)
    {
        var list = new List<KbImprovement>();
        var lis = nodes.SelectMany(n => n.Name == "li" ? new[] { n } : n.Descendants("li")).ToList();
        IEnumerable<HtmlNode> items = lis.Count > 0 ? lis : nodes.SelectMany(n => n.Name == "p" ? new[] { n } : n.Descendants("p"));
        foreach (var item in items)
        {
            // Skip nested list containers: their child li are enumerated separately.
            if (item.Name == "p" && IsBoldPara(item)) continue; // sub-heading, not an item
            var text = Clean(Text(item));
            if (text.Length < 4) continue;
            // A bare link entry (list of previously released updates) is not an improvement.
            if (item.Name == "li" && item.Descendants("a").Count() == 1 && Clean(Text(item.Descendants("a").First())) == text) continue;
            var imp = new KbImprovement { Links = Links(item) };
            var m = LeadingTag.Match(text);
            if (m.Success) { imp.Category = StripSuffixTags(m.Groups["t"].Value); imp.Text = text; }
            else imp.Text = text;
            list.Add(imp);
        }
        return list;
    }

    // "[Windows Explorer (Known issue)] Fixed" -> category "Windows Explorer (Known issue)" is kept whole.
    private static string StripSuffixTags(string s) => s.Trim();

    private static List<KbImprovement> ParseFreeText(HtmlNode root)
    {
        var list = new List<KbImprovement>();
        foreach (var p in root.Descendants().Where(n => n.Name is "p" or "li"))
        {
            var t = Clean(Text(p));
            if (t.Length >= 40) list.Add(new KbImprovement { Text = t, Links = Links(p) });
        }
        return list;
    }

    private static List<string> Links(HtmlNode n) =>
        n.Descendants("a").Select(a => a.GetAttributeValue("href", "")).Where(h => h.Length > 0).Distinct().ToList();

    // ---- known issues ----------------------------------------------------------------------

    private static List<KbIssue> ParseKnownIssues(List<HtmlNode> nodes)
    {
        var full = Clean(string.Join(" ", nodes.Select(Text)));
        if (full.Length == 0 || (NoIssues.IsMatch(full) && !nodes.Any(n => n.Name is "table" or "details")))
            return new List<KbIssue>();

        var issues = new List<KbIssue>();

        foreach (var table in nodes.SelectMany(n => n.Name == "table" ? new[] { n } : n.Descendants("table")))
            issues.AddRange(ParseTable(table));

        foreach (var det in nodes.SelectMany(n => n.Name == "details" ? new[] { n } : n.Descendants("details")))
            issues.Add(ParseDetails(det));

        if (issues.Count == 0)
        {
            var paras = nodes.SelectMany(n => n.Name == "p" ? new[] { n } : n.Descendants("p"))
                .Select(p => Clean(Text(p))).Where(t => t.Length > 0).ToList();
            var workaroundParas = paras.Where(p => p.Contains("workaround", StringComparison.OrdinalIgnoreCase)).ToList();
            if (workaroundParas.Count > 0 && !NoIssues.IsMatch(full))
            {
                // Split on paragraphs mentioning "Workaround": the paragraph before is the symptom.
                for (var i = 0; i < paras.Count; i++)
                {
                    if (!paras[i].Contains("workaround", StringComparison.OrdinalIgnoreCase)) continue;
                    var symptom = i > 0 ? paras[i - 1] : "";
                    issues.Add(Finish(new KbIssue { Symptom = symptom, Workaround = paras[i] }));
                }
            }
            else if (!NoIssues.IsMatch(full))
            {
                // Unrecognised structure: keep the raw section text, never drop data.
                issues.Add(Finish(new KbIssue { Symptom = full }));
            }
        }
        return issues;
    }

    private static IEnumerable<KbIssue> ParseTable(HtmlNode table)
    {
        var headers = table.Descendants("th").Select(th => Clean(Text(th))).ToList();
        int sym = headers.FindIndex(h => h.StartsWith("symptom", StringComparison.OrdinalIgnoreCase));
        int wrk = headers.FindIndex(h => h.StartsWith("workaround", StringComparison.OrdinalIgnoreCase)
                                      || h.StartsWith("resolution", StringComparison.OrdinalIgnoreCase));
        int app = headers.FindIndex(h => h.StartsWith("applies", StringComparison.OrdinalIgnoreCase));
        if (sym < 0) yield break;

        foreach (var tr in table.Descendants("tr"))
        {
            var cells = tr.Elements("td").ToList();
            if (cells.Count <= sym) continue;
            var issue = new KbIssue
            {
                Symptom = Clean(Text(cells[sym])),
                Workaround = wrk >= 0 && cells.Count > wrk ? Clean(Text(cells[wrk])) : "",
                AppliesTo = app >= 0 && cells.Count > app ? Clean(Text(cells[app])) : null,
            };
            if (issue.Symptom.Length == 0) continue;
            yield return Finish(issue);
        }
    }

    private static KbIssue ParseDetails(HtmlNode det)
    {
        var issue = new KbIssue { Title = Clean(Text(det.Element("summary"))) };

        // Layout 1: tab groups with sections data-tab="symptom" / "workaround".
        var symSec = det.Descendants("section").FirstOrDefault(s => s.GetAttributeValue("data-tab", "") == "symptom");
        var wrkSec = det.Descendants("section").FirstOrDefault(s => s.GetAttributeValue("data-tab", "") == "workaround");
        if (symSec is not null)
        {
            issue.Symptom = Clean(Text(symSec));
            issue.Workaround = wrkSec is null ? "" : Clean(Text(wrkSec));
            return Finish(issue);
        }

        // Layout 2: bold paragraphs "Symptoms" / "Resolution" / "Workaround" / "Next steps".
        var sb = new StringBuilder();
        string? mode = "symptom";
        var sym = new StringBuilder();
        var wrk = new StringBuilder();
        var sawLabel = false;
        foreach (var child in det.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element && c.Name != "summary"))
        {
            if (IsBoldPara(child))
            {
                var label = Clean(Text(child)).TrimEnd(':').ToLowerInvariant();
                if (label.StartsWith("symptom")) { mode = "symptom"; sawLabel = true; continue; }
                if (label.StartsWith("resolution") || label.StartsWith("workaround") || label.StartsWith("mitigation"))
                { mode = "workaround"; sawLabel = true; continue; }
            }
            var t = Clean(Text(child));
            if (t.Length == 0) continue;
            (mode == "workaround" ? wrk : sym).Append(t).Append('\n');
        }
        _ = sb;
        if (!sawLabel && sym.Length == 0 && wrk.Length == 0) sym.Append(Clean(Text(det)));
        issue.Symptom = sym.ToString().Trim();
        issue.Workaround = wrk.ToString().Trim();
        return Finish(issue);
    }

    private static KbIssue Finish(KbIssue issue)
    {
        var all = $"{issue.Title} {issue.Symptom} {issue.Workaround}";
        issue.Status = ResolvedRx.IsMatch(all) && !PartialRx.IsMatch(all) ? IssueStatus.Resolved : IssueStatus.Active;
        return issue;
    }

    // ---- text helpers ------------------------------------------------------------------------

    private static string Text(HtmlNode? node)
    {
        if (node is null) return "";
        var sb = new StringBuilder();
        Walk(node, sb);
        return sb.ToString();
    }

    private static void Walk(HtmlNode n, StringBuilder sb)
    {
        switch (n.NodeType)
        {
            case HtmlNodeType.Text:
                sb.Append(HtmlEntity.DeEntitize(((HtmlTextNode)n).Text));
                return;
            case HtmlNodeType.Comment:
                return;
        }
        if (n.Name is "script" or "style" or "noscript") return;
        var block = n.Name is "p" or "li" or "div" or "tr" or "br" or "h1" or "h2" or "h3" or "h4" or "section" or "details" or "summary" or "table";
        if (block) sb.Append(' ');
        foreach (var c in n.ChildNodes) Walk(c, sb);
        if (block) sb.Append(' ');
        else if (n.Name is "td" or "th") sb.Append(' ');
    }

    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    private static string Clean(string s) => Ws.Replace(s.Replace(' ', ' '), " ").Trim();

    public static string Hash(KbData d)
    {
        var sb = new StringBuilder();
        foreach (var i in d.Improvements) sb.Append(i.Text).Append('\u001f');
        foreach (var k in d.KnownIssues) sb.Append(k.Title).Append('|').Append(k.Symptom).Append('|').Append(k.Workaround).Append('|').Append(k.Status).Append('\u001f');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
