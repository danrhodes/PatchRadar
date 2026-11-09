using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PatchRadar.Infra;

/// <summary>
/// Detects a Windows Update "target feature release" policy (Intune / Group Policy) that holds the PC on a release
/// when a newer one is out. Such a pin can stop Windows Update offering newer-release updates, so PatchRadar
/// (and sometimes Windows itself) shows fewer updates than expected.
/// </summary>
public static class ReleasePin
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    // Windows 11 feature releases by the first build that belongs to them. Only used to name the running release;
    // whether a newer release exists is decided by asking Microsoft (see NewestAfterAsync), so this never gates the warning.
    private static readonly (int Build, string Release)[] Releases =
    {
        (22000, "21H2"), (22621, "22H2"), (22631, "23H2"), (26100, "24H2"), (26200, "25H2"), (26300, "26H2"),
    };

    private static readonly Regex ReleaseName = new(@"^(\d{2})H([12])$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>"26H2" for build 26300; null for Windows 10 and anything older than Windows 11.</summary>
    public static string? RunningRelease(int build) => build < Releases[0].Build ? null : Releases.Last(r => r.Build <= build).Release;

    /// <summary>24H2 -> 242, 26H2 -> 262; -1 when the text is not a release name.</summary>
    public static int Rank(string? release)
    {
        var m = release is null ? Match.Empty : ReleaseName.Match(release.Trim());
        return m.Success ? int.Parse(m.Groups[1].Value) * 10 + int.Parse(m.Groups[2].Value) : -1;
    }

    /// <summary>The release after this one: 24H2 -> 25H1, 25H1 -> 25H2. Null for a name that is not a release.</summary>
    public static string? Next(string release)
    {
        var r = Rank(release);
        if (r < 0) return null;
        var year = r / 10;
        return r % 10 == 1 ? $"{year:00}H2" : $"{year + 1:00}H1";
    }

    /// <summary>The pinned release, or null when no pin is set.</summary>
    public static string? ReadPinnedRelease()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (k is null || k.GetValue("TargetReleaseVersion") is not int on || on != 1) return null;
            return k.GetValue("TargetReleaseVersionInfo") as string is { Length: > 0 } v ? v.Trim() : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>True when the pin names a release older than the one running (so a newer release is proven to exist).</summary>
    public static bool IsOutdated(int build, string? pinned)
    {
        var running = RunningRelease(build);
        var pin = Rank(pinned);
        return running is not null && pin >= 0 && pin < Rank(running);
    }

    /// <summary>
    /// The newest release after <paramref name="pinned"/> that <paramref name="exists"/> confirms (a few half-years are tried, since Windows
    /// does not ship both halves every year). Null when none is out. A lookup that throws is not "no": it propagates so the caller can stay silent.
    /// </summary>
    public static async Task<string?> NewestAfterAsync(string pinned, Func<string, Task<bool>> exists, int steps = 6)
    {
        string? newest = null;
        var candidate = pinned;
        for (var i = 0; i < steps; i++)
        {
            candidate = Next(candidate);
            if (candidate is null) break;
            if (await exists(candidate).ConfigureAwait(false)) newest = candidate;
        }
        return newest;
    }

    public static string Message(int build, string pinned, string newest) =>
        $"Windows Update on this PC is pinned to {pinned} by policy (Intune or Group Policy: \"Select the target Feature Update version\"), " +
        $"but Windows 11 {newest} has been released" + (RunningRelease(build) is { } running ? $" and this PC is running {running} (build {build})." : ".") +
        "\n\nA pin to an older release can stop newer updates, such as preview updates, being offered, so Windows Update and PatchRadar may list fewer updates than expected.\n\n" +
        "Ask your IT team to raise or remove the pin.";
}
