using PatchRadar.Settings;

namespace PatchRadar.State;

/// <summary>Decides when to remind the user to restart, and remembers when it last did.</summary>
public sealed class RebootReminder(StateStore store, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    /// <summary>
    /// Due when enabled and up for at least AfterDays, and either no reminder has been shown since this start-up,
    /// or the repeat interval has passed (RepeatDays 0 = never repeat within one start-up).
    /// </summary>
    public static bool IsDue(RebootReminderSettings s, TimeSpan uptime, DateTime nowUtc, DateTime? lastReminderUtc, bool isServer = false)
    {
        if (!Applies(s, isServer)) return false;
        if (uptime.TotalDays < s.AfterDaysFor(isServer)) return false;
        if (lastReminderUtc is not { } last) return true;
        var bootUtc = nowUtc - uptime;
        if (last < bootUtc) return true;                       // last reminder was before this start-up
        return s.RepeatDays > 0 && nowUtc - last >= TimeSpan.FromDays(s.RepeatDays);
    }

    /// <summary>Reminders are on, and (on a server) the user has opted in to them there.</summary>
    public static bool Applies(RebootReminderSettings s, bool isServer) =>
        s.Enabled && s.AfterDaysFor(isServer) >= 1 && (!isServer || s.RemindOnServers);

    /// <summary>True (and recorded) when a reminder should be shown now.</summary>
    public bool TryTake(RebootReminderSettings s, TimeSpan uptime, bool isServer = false)
    {
        var due = false;
        store.Update(state =>
        {
            var n = _now();
            if (!IsDue(s, uptime, n, state.LastRebootReminderUtc, isServer)) return;
            state.LastRebootReminderUtc = n;
            due = true;
        });
        return due;
    }
}
