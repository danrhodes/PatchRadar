using PatchRadar.Core;
using PatchRadar.Scan;

namespace PatchRadar.State;

/// <summary>
/// Decides which red updates deserve a toast: once per KB per content hash, persisted so a restart never repeats it.
/// Updates installed outside the recent window are recorded silently, so a first run does not toast the whole history.
/// </summary>
public sealed class ToastTracker(StateStore store, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<UpdateRow> TakeNew(IEnumerable<UpdateRow> rows, int recentWindowDays)
    {
        var state = store.Load();
        var toast = new List<UpdateRow>();
        var changed = false;
        foreach (var r in rows)
        {
            if (r.Severity != Severity.Red || r.Kb is null || r.Kb.ContentHash.Length == 0) continue;
            if (state.Notified.TryGetValue(r.Update.Kb, out var h) && h == r.Kb.ContentHash) continue;
            state.Notified[r.Update.Kb] = r.Kb.ContentHash;
            changed = true;
            var recent = (_now() - r.Update.InstalledOn) <= TimeSpan.FromDays(recentWindowDays);
            if (recent) toast.Add(r);
        }
        if (changed) store.Save(state);
        return toast;
    }
}
