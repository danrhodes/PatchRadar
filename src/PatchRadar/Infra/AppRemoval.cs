using System.Diagnostics;
using System.IO;
using System.Text;

namespace PatchRadar.Infra;

/// <summary>
/// Removes PatchRadar itself: the run-at-login entry, the exe and its leftovers, and optionally settings and history.
/// A running exe cannot delete itself, so a small batch file waits for the process to exit and then does the deleting.
/// </summary>
public static class AppRemoval
{
    /// <summary>The batch script: retries the exe delete until the process has exited, then removes leftovers and itself.</summary>
    public static string BuildScript(string? exePath, IEnumerable<string> directories)
    {
        static string Q(string path) => "\"" + path.Replace("%", "%%") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        if (!string.IsNullOrEmpty(exePath))
        {
            sb.AppendLine("set n=0");
            sb.AppendLine(":wait");
            sb.AppendLine($"del /f /q {Q(exePath)} >nul 2>&1");
            sb.AppendLine($"if exist {Q(exePath)} (");
            sb.AppendLine("  set /a n+=1");
            sb.AppendLine("  if %n% lss 30 (");
            sb.AppendLine("    ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("    goto wait");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            foreach (var ext in new[] { ".old", ".failed" })
                sb.AppendLine($"del /f /q {Q(exePath + ext)} >nul 2>&1");
        }
        foreach (var dir in directories)
            sb.AppendLine($"rmdir /s /q {Q(dir)} >nul 2>&1");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>
    /// Remove the run-at-login entry and start the cleanup script. The caller must then end the process.
    /// <paramref name="exePath"/> is left alone when it is not a PatchRadar exe (for example dotnet.exe under "dotnet run").
    /// </summary>
    public static void Start(IRunKey runKey, string? exePath, bool removeData, ILog? log = null)
    {
        try { runKey.Delete(Autostart.ValueName); }
        catch (Exception ex) { log?.Warn("Could not remove the Run key: " + ex.Message); }

        var isOwnExe = exePath is not null && Path.GetFileName(exePath).StartsWith("PatchRadar", StringComparison.OrdinalIgnoreCase);
        var dirs = removeData ? new[] { Paths.AppData, Paths.LocalAppData } : Array.Empty<string>();
        var script = Path.Combine(Path.GetTempPath(), $"patchradar-remove-{Environment.ProcessId}.cmd");
        File.WriteAllText(script, BuildScript(isOwnExe ? exePath : null, dirs));
        log?.Info($"Removing PatchRadar (exe: {(isOwnExe ? exePath : "kept")}, data: {removeData})");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
    }
}
