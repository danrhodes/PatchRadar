using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PatchRadar.Core;

namespace PatchRadar.Uninstall;

public enum UninstallMethod
{
    /// <summary>Removal is not possible (or not safe) for this update. No button is shown.</summary>
    None,
    /// <summary><c>wusa.exe /uninstall /kb:N</c>: standalone MSU packages such as .NET Framework updates.</summary>
    Wusa,
    /// <summary><c>dism.exe /online /remove-package</c>: the newest cumulative (rollup) update. wusa cannot remove these.</summary>
    DismRemovePackage,
}

public sealed record UninstallPlan(UninstallMethod Method, string Reason, string Kb, string? PackageName = null, bool IsSecurityUpdate = false)
{
    public bool CanUninstall => Method != UninstallMethod.None;

    /// <summary>Exactly what will be run, shown to the user before they confirm.</summary>
    public string Command => Method switch
    {
        UninstallMethod.Wusa => $"wusa.exe /uninstall /kb:{Kb.Substring(2)} /quiet /norestart",
        UninstallMethod.DismRemovePackage => $"dism.exe /online /remove-package /packagename:{PackageName} /quiet /norestart",
        _ => "",
    };

    public string Executable => Method == UninstallMethod.Wusa ? "wusa.exe" : "dism.exe";

    public string Arguments => Command.Length > Executable.Length ? Command[(Executable.Length + 1)..] : "";

    public static UninstallPlan No(string kb, string reason, bool security = false) => new(UninstallMethod.None, reason, kb, null, security);
}

/// <summary>One servicing package from Component Based Servicing (what Windows itself uses to uninstall).</summary>
public sealed record CbsPackage(string Name, string? Kb, bool IsRollup, int State, Version? Version, DateTime? InstalledUtc)
{
    public const int Installed = 112;          // 0x70
    public bool IsInstalled => State == Installed;
    /// <summary>The "revision" part of a rollup's version, for example 9711 in 26100.9711.1.30.</summary>
    public int? Revision => Version?.Minor;
}

public interface ICbsPackageSource
{
    IReadOnlyList<CbsPackage> GetPackages();
}

/// <summary>Reads CBS packages from the registry. Readable by standard users, so the button can be decided without elevation.</summary>
public sealed class RegistryCbsPackageSource : ICbsPackageSource
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";
    private static readonly Regex KbName = new(@"^Package_for_KB(?<kb>\d{6,8})~", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<CbsPackage> GetPackages()
    {
        var list = new List<CbsPackage>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(Key);
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                var rollup = name.StartsWith("Package_for_RollupFix~", StringComparison.OrdinalIgnoreCase);
                var m = KbName.Match(name);
                if (!rollup && !m.Success) continue;
                using var k = root.OpenSubKey(name);
                if (k is null) continue;
                list.Add(Parse(name, m.Success ? "KB" + m.Groups["kb"].Value : null, rollup,
                    k.GetValue("CurrentState") is int st ? st : 0,
                    k.GetValue("InstallTimeHigh"), k.GetValue("InstallTimeLow")));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    public static CbsPackage Parse(string name, string? kb, bool rollup, int state, object? high, object? low)
    {
        Version? ver = null;
        var tail = name.Split('~')[^1];
        if (Version.TryParse(tail, out var v)) ver = v;
        DateTime? when = null;
        try
        {
            if (high is int h && low is int l && (h != 0 || l != 0))
                when = DateTime.FromFileTimeUtc(((long)(uint)h << 32) | (uint)l);
        }
        catch (ArgumentOutOfRangeException) { }
        return new CbsPackage(name, kb, rollup, state, ver, when);
    }
}

/// <summary>
/// Decides, per update, whether it can really be uninstalled and by which method. Pure and table-tested.
/// The button is only offered when the result is not <see cref="UninstallMethod.None"/>.
/// </summary>
public sealed class UninstallPlanner(ICbsPackageSource packages)
{
    private static readonly Regex KbShape = new(@"^KB\d{6,8}$", RegexOptions.Compiled);
    private static readonly Regex PackageShape = new(@"^[A-Za-z0-9_.~\-]+$", RegexOptions.Compiled);
    private static readonly Regex BuildInTitle = new(@"\((?<build>\d+)\.(?<rev>\d+)\)", RegexOptions.Compiled);
    private static readonly Regex DefenderLike = new(
        @"Security Intelligence|Definition Update|Malicious Software Removal|Defender Antivirus (antimalware )?platform|Windows Security platform",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModernDotNet = new(@"\.NET\s+\d+\.\d+(\.\d+)?\b(?!.*Framework)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public UninstallPlan Plan(InstalledUpdate u)
    {
        var kb = u.Kb;
        var security = u.Title.Contains("Security", StringComparison.OrdinalIgnoreCase) || u.Category is UpdateCategory.Security or UpdateCategory.Cumulative;

        if (!KbShape.IsMatch(kb)) return UninstallPlan.No(kb, "The KB number is not in a recognised form.");
        if (u.State != UpdateState.Installed) return UninstallPlan.No(kb, "This update is not currently installed.");

        if (u.Category == UpdateCategory.Definition || DefenderLike.IsMatch(u.Title))
            return UninstallPlan.No(kb, "Definition, platform and malware-removal updates are replaced by Windows rather than uninstalled.");
        if (u.Category == UpdateCategory.ServicingStack || u.Title.Contains("Servicing Stack", StringComparison.OrdinalIgnoreCase))
            return UninstallPlan.No(kb, "Servicing stack updates cannot be removed. Windows needs the newest one to install anything else.");
        if (u.Category == UpdateCategory.Driver)
            return UninstallPlan.No(kb, "Driver updates are rolled back in Device Manager, not by KB.");
        if (u.Category == UpdateCategory.Office)
            return UninstallPlan.No(kb, "Office updates are removed through Office or Installed apps.");
        if (u.Category == UpdateCategory.DotNet && ModernDotNet.IsMatch(u.Title) && !u.Title.Contains("Framework", StringComparison.OrdinalIgnoreCase))
            return UninstallPlan.No(kb, "Modern .NET runtime updates are removed through Installed apps, not by KB.");

        var all = packages.GetPackages();

        // Cumulative (rollup) updates: only DISM can remove them, and only the newest one.
        var m = BuildInTitle.Match(u.Title);
        if (m.Success || u.Category == UpdateCategory.Cumulative)
        {
            if (!m.Success)
                return UninstallPlan.No(kb, "This cumulative update cannot be matched to an installed package (its title has no build number).", security);
            var rev = int.Parse(m.Groups["rev"].Value);
            var rollups = all.Where(p => p.IsRollup && p.IsInstalled && p.Version is not null).OrderByDescending(p => p.Version).ToList();
            var match = rollups.FirstOrDefault(p => p.Revision == rev);
            if (match is null)
            {
                var superseded = all.Any(p => p.IsRollup && p.Revision == rev);
                return UninstallPlan.No(kb, superseded
                    ? "A newer cumulative update has replaced this one (superseded). Only the newest cumulative update can be removed."
                    : "No installed cumulative package matches this update.", security);
            }
            if (!ReferenceEquals(match, rollups[0]))
                return UninstallPlan.No(kb, $"Only the newest cumulative update can be removed, and {rollups.Count(p => p.Version > match.Version)} newer one(s) are installed.", security);
            if (!PackageShape.IsMatch(match.Name)) return UninstallPlan.No(kb, "The package name is not in a safe form.", security);
            return new UninstallPlan(UninstallMethod.DismRemovePackage,
                "Newest cumulative update: removed with DISM. Only the cumulative part is removed; the servicing stack stays.", kb, match.Name, security);
        }

        // Standalone MSU packages (for example .NET Framework updates): wusa, if Windows has a package for that KB.
        var pkg = all.FirstOrDefault(p => !p.IsRollup && p.IsInstalled && string.Equals(p.Kb, kb, StringComparison.OrdinalIgnoreCase));
        if (pkg is not null)
            return new UninstallPlan(UninstallMethod.Wusa, "Standalone update: removed with the Windows Update Standalone Installer (wusa).", kb, pkg.Name, security);

        return UninstallPlan.No(kb, "Windows has no removable package for this update (it is not listed under Installed updates).", security);
    }
}
