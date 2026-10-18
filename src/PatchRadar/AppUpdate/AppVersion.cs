using System.Reflection;
using System.Text.RegularExpressions;

namespace PatchRadar.AppUpdate;

/// <summary>
/// SemVer 2.0 with one project rule: tags look like <c>X.Y.Z-pre-release.N-sha7</c> and N is compared numerically.
/// A plain string compare would rank <c>10-abc</c> below <c>9-abc</c>.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private static readonly Regex Rx = new(
        @"^v?(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)(?:-(?<pre>[0-9A-Za-z.\-]+))?(?:\+(?<build>[0-9A-Za-z.\-]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PreN = new(@"^pre-release\.(?<n>\d+)(?:-|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Pre-release label without the leading dash, or null for a full release.</summary>
    public string? Pre { get; }
    public bool IsPreRelease => Pre is not null;

    private AppVersion(int maj, int min, int pat, string? pre) { Major = maj; Minor = min; Patch = pat; Pre = pre; }

    public static bool TryParse(string? s, out AppVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = Rx.Match(s.Trim());
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["maj"].Value, out var a) || !int.TryParse(m.Groups["min"].Value, out var b) || !int.TryParse(m.Groups["pat"].Value, out var c))
            return false;
        version = new AppVersion(a, b, c, m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
        return true;
    }

    public static AppVersion Parse(string s) =>
        TryParse(s, out var v) ? v : throw new FormatException($"Not a valid version: '{s}'");

    /// <summary>Tag text without the leading v, for settings.skippedVersion and display.</summary>
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre is null ? "" : "-" + Pre);

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre is null && other.Pre is null) return 0;
        if (Pre is null) return 1;          // a full release outranks its own pre-releases
        if (other.Pre is null) return -1;

        var a = PreN.Match(Pre);
        var b = PreN.Match(other.Pre);
        if (a.Success && b.Success)
            return long.Parse(a.Groups["n"].Value).CompareTo(long.Parse(b.Groups["n"].Value));
        return ComparePre(Pre, other.Pre);
    }

    // SemVer 2.0 section 11 for everything that is not our own pre-release naming.
    private static int ComparePre(string x, string y)
    {
        var xs = x.Split('.');
        var ys = y.Split('.');
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            var xn = long.TryParse(xs[i], out var xv);
            var yn = long.TryParse(ys[i], out var yv);
            int c;
            if (xn && yn) c = xv.CompareTo(yv);
            else if (xn) c = -1;
            else if (yn) c = 1;
            else c = string.CompareOrdinal(xs[i], ys[i]);
            if (c != 0) return c;
        }
        return xs.Length.CompareTo(ys.Length);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as AppVersion);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Pre is null ? 0 : PreN.Match(Pre) is { Success: true } m ? m.Groups["n"].Value.GetHashCode() : Pre.GetHashCode());
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    // ---- the running build ---------------------------------------------------------------

    /// <summary>The version a build without <c>-p:Version</c> gets. Treated as a development build.</summary>
    public const string DevVersion = "1.0.0";

    public static AppVersion Running(Assembly? asm = null)
    {
        asm ??= typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParse(info?.Split('+')[0], out var v) ? v : Parse(DevVersion);
    }

    /// <summary>"full" (self-contained) or "slim" (framework-dependent), baked in at build time.</summary>
    public static string Flavour(Assembly? asm = null)
    {
        asm ??= typeof(AppVersion).Assembly;
        var f = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateFlavour")?.Value;
        return string.Equals(f, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "full";
    }

    public bool IsDevBuild => ToString() == DevVersion;
}
