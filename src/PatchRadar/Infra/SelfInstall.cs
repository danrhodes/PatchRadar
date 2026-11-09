using System.Diagnostics;
using System.IO;

namespace PatchRadar.Infra;

/// <summary>
/// Wherever PatchRadar is run from, it moves itself into %LocalAppData%\PatchRadarin and removes the original,
/// so self-updates are written somewhere stable. "--customdir &lt;path&gt;" installs to that folder instead, and the
/// choice is remembered so later launches from there are left alone.
/// </summary>
public static class SelfInstall
{
    public const string RelocatedArgument = "--relocated";
    public const string CustomDirArgument = "--customdir";
    public const string InstalledFileName = "PatchRadar.exe";

    public static string InstallDirectory => Path.Combine(Paths.LocalAppData, "bin");
    public static string InstalledPath => Path.Combine(InstallDirectory, InstalledFileName);
    public static string CustomDirMarker => Path.Combine(Paths.LocalAppData, "customdir.txt");

    /// <summary>The folder after --customdir (quotes allowed), or null when not given.</summary>
    public static string? ParseCustomDir(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, CustomDirArgument, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        var v = args[i + 1].Trim().Trim('"');
        return v.Length == 0 ? null : v;
    }

    public static string? ReadSavedCustomDir()
    {
        try { return File.Exists(CustomDirMarker) ? File.ReadAllText(CustomDirMarker).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void SaveCustomDir(string? dir)
    {
        try
        {
            Directory.CreateDirectory(Paths.LocalAppData);
            if (dir is null) { if (File.Exists(CustomDirMarker)) File.Delete(CustomDirMarker); }
            else File.WriteAllText(CustomDirMarker, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (any depth).</summary>
    public static bool IsUnder(string path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>
    /// Any real PatchRadar exe outside its install folder qualifies; dotnet.exe under "dotnet run" never does.
    /// <paramref name="customDir"/> is the --customdir request; <paramref name="savedCustomDir"/> is a remembered earlier choice.
    /// </summary>
    public static bool ShouldRelocate(string? exePath, string? customDir = null, string? savedCustomDir = null)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        if (!Path.GetFileName(exePath).StartsWith("PatchRadar", StringComparison.OrdinalIgnoreCase)) return false;
        // Release builds are single-file. A PatchRadar.dll beside the exe means a development build output (bin\Debug): never move or delete that.
        try { if (Path.GetDirectoryName(exePath) is { Length: > 0 } d && File.Exists(Path.Combine(d, "PatchRadar.dll"))) return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        // A versioned name (PatchRadar-v1.2.1-win-x64.exe) inside the install folder is renamed to PatchRadar.exe.
        var wrongName = !Path.GetFileName(exePath).Equals(InstalledFileName, StringComparison.OrdinalIgnoreCase);
        if (customDir is not null) return !IsUnder(exePath, customDir) || wrongName;
        if (IsUnder(exePath, InstallDirectory)) return wrongName;
        return !IsUnder(exePath, savedCustomDir) || wrongName;
    }

    /// <summary>The folder to install into: --customdir if given, else the remembered custom folder when running from it, else null (AppData).</summary>
    public static string? EffectiveCustomDir(string exePath, string? customDir, string? savedCustomDir)
        => customDir ?? (IsUnder(exePath, savedCustomDir) ? savedCustomDir : null);

    /// <summary>Where the exe should live: the custom folder when requested, otherwise the AppData folder.</summary>
    public static string TargetDirectory(string? customDir) => customDir is null ? InstallDirectory : Path.GetFullPath(customDir);

    public enum Outcome { NotNeeded, Relocated, UsedExisting, Failed }

    public sealed record Result(Outcome Outcome, string? NewPath = null, string? Error = null);

    /// <summary>
    /// Copies the exe into the install directory and starts it. An already installed copy that is the same or newer is kept.
    /// The caller shows the alert, then calls <see cref="RemoveOriginal"/> and ends the process.
    /// </summary>
    public static Result Relocate(string exePath, string? customDir = null, ILog? log = null)
    {
        try
        {
            var dir = TargetDirectory(customDir);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, InstalledFileName);
            var useExisting = false;
            if (File.Exists(target) && !target.Equals(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            {
                useExisting = Version(target) >= Version(exePath);
                if (!useExisting)
                {
                    try { File.Copy(exePath, target, overwrite: true); }
                    catch (IOException ex) { log?.Warn("Installed copy is in use; keeping it: " + ex.Message); useExisting = true; }
                }
            }
            else File.Copy(exePath, target);

            SaveCustomDir(customDir is null ? null : dir);
            Process.Start(new ProcessStartInfo(target, $"{RelocatedArgument} \"{exePath}\"") { UseShellExecute = false });
            log?.Info($"Relocated from {exePath} to {target} (existing kept: {useExisting})");
            return new Result(useExisting ? Outcome.UsedExisting : Outcome.Relocated, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log?.Error("Could not install PatchRadar to the target folder; running from where it is", ex);
            return new Result(Outcome.Failed, null, ex.Message);
        }
    }

    /// <summary>Starts the batch file that deletes the downloaded exe once this process has exited.</summary>
    public static void RemoveOriginal(string exePath, ILog? log = null)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"patchradar-relocate-{Environment.ProcessId}.cmd");
            File.WriteAllText(script, AppRemoval.BuildScript(exePath, Array.Empty<string>()));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log?.Warn("Could not start the cleanup of the downloaded copy: " + ex.Message);
        }
    }

    private static Version Version(string path)
    {
        try { return System.Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var v) ? v : new Version(0, 0); }
        catch (Exception ex) when (ex is IOException or FileNotFoundException) { return new Version(0, 0); }
    }
}
