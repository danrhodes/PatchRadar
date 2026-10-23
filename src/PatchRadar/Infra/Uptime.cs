namespace PatchRadar.Infra;

/// <summary>How long Windows has been running, the same figure Task Manager shows (Fast Startup does not reset it).</summary>
public static class Uptime
{
    public static TimeSpan Current() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static string Describe(TimeSpan up)
    {
        if (up.TotalDays >= 1) { var d = (int)up.TotalDays; return d == 1 ? "1 day" : $"{d} days"; }
        if (up.TotalHours >= 1) { var h = (int)up.TotalHours; return h == 1 ? "1 hour" : $"{h} hours"; }
        return "under an hour";
    }

    /// <summary>True when the PC has run at least <paramref name="warnDays"/> days without a restart (0 turns the warning off).</summary>
    public static bool IsLong(TimeSpan up, int warnDays) => warnDays > 0 && up.TotalDays >= warnDays;
}
