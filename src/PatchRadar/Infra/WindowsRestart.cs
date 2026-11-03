using System.Diagnostics;
using System.IO;

namespace PatchRadar.Infra;

/// <summary>
/// Restarts or schedules a restart of Windows through shutdown.exe, so Windows shows its own countdown and
/// <c>shutdown /a</c> (or Cancel in the tray) can abort it. Needs no elevation where the user may shut the PC down.
/// </summary>
public static class WindowsRestart
{
    public const string Comment = "PatchRadar: restarting to finish installing Windows updates.";

    /// <summary>The delay that makes "now" a short countdown, long enough to abort by mistake.</summary>
    public const int NowSeconds = 10;

    public static string RestartArguments(int seconds) =>
        $"/r /t {Math.Max(0, seconds)} /c \"{Comment}\"";

    public const string AbortArguments = "/a";

    /// <summary>Seconds from <paramref name="now"/> until the next occurrence of <paramref name="hour"/>:00 (always in the future).</summary>
    public static int SecondsUntilNext(DateTime now, int hour)
    {
        var target = now.Date.AddHours(hour);
        if (target <= now) target = target.AddDays(1);
        return (int)Math.Ceiling((target - now).TotalSeconds);
    }

    public static bool Restart(int seconds, ILog log) => Run(RestartArguments(seconds), $"restart in {seconds}s", log);

    public static bool Cancel(ILog log) => Run(AbortArguments, "abort restart", log);

    private static bool Run(string args, string what, ILog log)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { log.Warn($"shutdown.exe did not start ({what})"); return false; }
            var err = p.StandardError.ReadToEnd().Trim();
            if (!p.WaitForExit(10000)) { log.Warn($"shutdown.exe timed out ({what})"); return false; }
            if (p.ExitCode == 0) { log.Info($"shutdown.exe: {what}"); return true; }
            log.Warn($"shutdown.exe ({what}) exited {p.ExitCode}: {err}");
            return false;
        }
        catch (Exception ex)
        {
            log.Warn($"Could not run shutdown.exe ({what}): {ex.Message}");
            return false;
        }
    }
}
