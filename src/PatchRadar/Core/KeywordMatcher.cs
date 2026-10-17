using System.Text.RegularExpressions;

namespace PatchRadar.Core;

/// <summary>Case-insensitive, word-boundary keyword matching. A "re:" prefix means regex.</summary>
public sealed class KeywordMatcher
{
    private readonly List<(string Keyword, Regex Rx)> _rules = new();

    public KeywordMatcher(IEnumerable<string> keywords, bool wordBoundary = true)
    {
        foreach (var k in keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            try
            {
                Regex rx;
                if (k.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
                    rx = new Regex(k[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                else
                {
                    var body = Regex.Escape(k.Trim());
                    var pattern = wordBoundary ? $@"(?<!\w){body}(?!\w)" : $@"(?<!\w){body}";
                    rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                _rules.Add((k.Trim(), rx));
            }
            catch (ArgumentException) { /* invalid user regex: skip */ }
        }
    }

    public IReadOnlyList<string> Match(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var hits = new List<string>();
        foreach (var (kw, rx) in _rules)
        {
            try { if (rx.IsMatch(text)) hits.Add(kw); }
            catch (RegexMatchTimeoutException) { }
        }
        return hits;
    }

    public bool IsMatch(string? text) => Match(text).Count > 0;
}
