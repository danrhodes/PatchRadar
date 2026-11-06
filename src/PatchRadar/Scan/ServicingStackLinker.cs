using System.Text.RegularExpressions;

namespace PatchRadar.Scan;

/// <summary>
/// A servicing stack update (SSU) is usually shipped with a cumulative update, and that update's article says so under
/// "Servicing stack update: Includes KB5127647 (Build ...)". This finds the cumulative update an SSU came with.
/// </summary>
public static class ServicingStackLinker
{
    // Microsoft words this differently from article to article:
    //   "Servicing stack update (SSU) Includes KB5124009 (Build ...)"
    //   "Component updates Windows 11 servicing stack update (KB5124007) - 22621.9441"
    //   "download the file information for SSU KB5124007" / "for the SSU (KB5124009)"
    private static readonly Regex[] Patterns =
    {
        new(@"Servicing stack update(?:\s*\(SSU\))?\W{0,8}Includes\s+KB(?<kb>\d{6,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"servicing stack update\s*\(KB(?<kb>\d{6,8})\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bSSU\W{0,4}KB(?<kb>\d{6,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>Every SSU KB this article says it includes (for example "KB5127647").</summary>
    public static IReadOnlyList<string> IncludedSsus(string? articleText)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(articleText)) return found;
        foreach (var re in Patterns)
            foreach (Match m in re.Matches(articleText))
            {
                var kb = "KB" + m.Groups["kb"].Value;
                if (!found.Contains(kb, StringComparer.OrdinalIgnoreCase)) found.Add(kb);
            }
        return found;
    }

    /// <summary>The first SSU KB this article says it includes, or null.</summary>
    public static string? IncludedSsu(string? articleText) => IncludedSsus(articleText).FirstOrDefault();

    /// <summary>The first candidate whose article names <paramref name="ssuKb"/> as its servicing stack update.</summary>
    public static string? ParentOf(string ssuKb, IEnumerable<(string Kb, string? Text)> candidates)
    {
        foreach (var (kb, text) in candidates)
            if (!kb.Equals(ssuKb, StringComparison.OrdinalIgnoreCase) &&
                IncludedSsus(text).Contains(ssuKb, StringComparer.OrdinalIgnoreCase))
                return kb;
        return null;
    }
}
