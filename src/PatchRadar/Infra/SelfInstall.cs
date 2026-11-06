using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PatchRadar.Infra;

/// <summary>
/// A copy run from the Downloads folder moves itself into %LocalAppData%\PatchRadar\bin and removes the download,
/// so self-updates are written somewhere that survives a Downloads clean-up.
/// </summary>
public static class SelfInstall
{
    public const string RelocatedArgument = "--relocated";
    public const string InstalledFileName = "PatchRadar.exe";

    public static string InstallDirectory => Path.Combine(Paths.LocalAppData, "bin");
    public static string InstalledPath => Path.Combine(InstallDirectory, InstalledFileName);

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

    /// <summary>Only a real PatchRadar exe inside Downloads qualifies; dotnet.exe under "dotnet run" never does.</summary>
    public static bool ShouldRelocate(string? exePath, IEnumerable<string?> downloadFolders)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        if (!Path.GetFileName(exePath).StartsWith("PatchRadar", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsUnder(exePath, InstallDirectory)) return false;
        return downloadFolders.Any(f => IsUnder(exePath, f));
    }

    /// <summary>The user's Downloads folder (honours a moved folder) plus the default under the profile.</summary>
    public static IEnumerable<string?> DownloadFolders()
    {
        yield return KnownDownloads();
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public enum Outcome { NotNeeded, Relocated, UsedExisting, Failed }

    public sealed record Result(Outcome Outcome, string? NewPath = null, string? Error = null);

    /// <summary>
    /// Copies the exe into the install directory and starts it. An already installed copy that is the same or newer is kept.
    /// The caller shows the alert, then calls <see cref="RemoveOriginal"/> and ends the process.
    /// </summary>
    public static Result Relocate(string exePath, ILog? log = null)
    {
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            var target = InstalledPath;
            var useExisting = false;
            if (File.Exists(target))
            {
                useExisting = Version(target) >= Version(exePath);
                if (!useExisting)
                {
                    try { File.Copy(exePath, target, overwrite: true); }
                    catch (IOException ex) { log?.Warn("Installed copy is in use; keeping it: " + ex.Message); useExisting = true; }
                }
            }
            else File.Copy(exePath, target);

            Process.Start(new ProcessStartInfo(target, $"{RelocatedArgument} \"{exePath}\"") { UseShellExecute = false });
            log?.Info($"Relocated from {exePath} to {target} (existing kept: {useExisting})");
            return new Result(useExisting ? Outcome.UsedExisting : Outcome.Relocated, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log?.Error("Could not move PatchRadar out of Downloads; running from there", ex);
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

    private static string? KnownDownloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
