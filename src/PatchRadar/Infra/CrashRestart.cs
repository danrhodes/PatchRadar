using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PatchRadar.Infra;

/// <summary>
/// Starts PatchRadar again after a crash, but never more than once in a short window, so a crash on startup
/// cannot turn into an endless restart loop.
/// </summary>
public static class CrashRestart
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public const string Argument = "--restarted";

    public static string StampPath => Path.Combine(Paths.LocalAppData, "last-crash-restart.txt");

    /// <summary>True when no restart has happened inside the window. Records this one when it returns true.</summary>
    public static bool ShouldRestart(string stampPath, DateTime nowUtc)
    {
        try
        {
            if (File.Exists(stampPath) &&
                long.TryParse(File.ReadAllText(stampPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                nowUtc - new DateTime(ticks, DateTimeKind.Utc) < Window)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
            File.WriteAllText(stampPath, nowUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void TryRestart(string? exePath, ILog? log)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !Path.GetFileName(exePath).StartsWith("PatchRadar", StringComparison.OrdinalIgnoreCase)) return;
            if (!ShouldRestart(StampPath, DateTime.UtcNow)) { log?.Warn("Crashed again within 5 minutes; not restarting automatically"); return; }
            log?.Info("Restarting PatchRadar after a crash");
            Process.Start(new ProcessStartInfo(exePath, Argument) { UseShellExecute = false });
        }
        catch (Exception ex) { log?.Warn("Could not restart after a crash: " + ex.Message); }
    }
}
