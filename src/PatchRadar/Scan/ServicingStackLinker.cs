using System.Text.RegularExpressions;

namespace PatchRadar.Scan;

/// <summary>
/// A servicing stack update (SSU) is usually shipped with a cumulative update, and that update's article says so under
/// "Servicing stack update: Includes KB5127647 (Build ...)". This finds the cumulative update an SSU came with.
/// </summary>
public static class ServicingStackLinker
{
    private static readonly Regex Includes = new(
        @"Servicing stack update\W{0,8}Includes\s+KB(?<kb>\d{6,8})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The SSU KB this article says it includes (for example "KB5127647"), or null.</summary>
    public static string? IncludedSsu(string? articleText)
    {
        if (string.IsNullOrEmpty(articleText)) return null;
        var m = Includes.Match(articleText);
        return m.Success ? "KB" + m.Groups["kb"].Value : null;
    }

    /// <summary>The first candidate whose article names <paramref name="ssuKb"/> as its servicing stack update.</summary>
    public static string? ParentOf(string ssuKb, IEnumerable<(string Kb, string? Text)> candidates)
    {
        foreach (var (kb, text) in candidates)
            if (!kb.Equals(ssuKb, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(IncludedSsu(text), ssuKb, StringComparison.OrdinalIgnoreCase))
                return kb;
        return null;
    }
}
