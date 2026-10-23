using System.Text.RegularExpressions;
using PatchRadar.Core;

namespace PatchRadar.Updates;

/// <summary>Drivers have no KB article, but their title carries the vendor, device and version.</summary>
public static class DriverInfo
{
    private static readonly Regex Titled = new(@"^(?<name>.+?)\s*\((?<ver>\d+(?:\.\d+){1,3})\)\s*$", RegexOptions.Compiled);

    public static (string Name, Version Version)? Parse(string title)
    {
        var m = Titled.Match(title.Trim());
        if (!m.Success || !Version.TryParse(m.Groups["ver"].Value, out var v)) return null;
        return (m.Groups["name"].Value.Trim(), v);
    }

    /// <summary>
    /// Compare a pending driver with the newest installed version of the same driver in the update history.
    /// Returns null when the title has no version to compare.
    /// </summary>
    public static string? Compare(InstalledUpdate pending, IEnumerable<InstalledUpdate> installed)
    {
        if (pending.Category != UpdateCategory.Driver || Parse(pending.Title) is not { } p) return null;

        Version? current = null;
        foreach (var u in installed)
        {
            if (u.Category != UpdateCategory.Driver || Parse(u.Title) is not { } i) continue;
            if (!i.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (current is null || i.Version > current) current = i.Version;
        }

        if (current is null) return "No earlier version of this driver is in this PC's update history.";
        if (p.Version < current) return $"Older than the version installed here ({current}). Installing it would be a downgrade.";
        if (p.Version == current) return $"Same version as the one already installed ({current}).";
        return p.Version.Major != current.Major
            ? $"Major version change: {current} to {p.Version}. Check the device afterwards."
            : $"Newer than the version installed here ({current}).";
    }
}
