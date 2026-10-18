using System.IO;
using System.Text.Json;
using PatchRadar.Core;
using PatchRadar.Kb;
using PatchRadar.Settings;

namespace PatchRadar.State;

public enum ReviewStatus
{
    None,
    Reviewed,
    /// <summary>Was reviewed, but Microsoft has edited the article since (its content hash differs).</summary>
    ChangedSinceReviewed,
}

/// <summary>Per-KB "I have looked at this" flags, tied to the KB content hash.</summary>
public sealed class ReviewStore(StateStore store, Func<DateTime>? now = null, string? snapshotDir = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    private string? SnapshotPath(string kb) => snapshotDir is null ? null : Path.Combine(snapshotDir, kb.ToUpperInvariant() + ".json");

    /// <summary>The article as it was when the user reviewed it: the baseline for before-and-after.</summary>
    public KbSnapshot? LoadSnapshot(string kb)
    {
        var p = SnapshotPath(kb);
        if (p is null || !File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<KbSnapshot>(File.ReadAllText(p), SettingsStore.JsonOptions); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>Review with a content snapshot, so a later edit can be shown as before and after.</summary>
    public void Mark(string kb, KbData data)
    {
        Mark(kb, data.ContentHash);
        var p = SnapshotPath(kb);
        if (p is null) return;
        try
        {
            Directory.CreateDirectory(snapshotDir!);
            File.WriteAllText(p, JsonSerializer.Serialize(KbSnapshot.Of(data, _now()), SettingsStore.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* review still recorded; just no diff baseline */ }
    }

    /// <summary>One read of state.json for a whole list, instead of one per row.</summary>
    public ReviewSnapshot Snapshot() => new(store.Load().Reviewed);

    public void Mark(string kb, string contentHash) =>
        store.Update(s => s.Reviewed[kb] = new ReviewEntry { ReviewedUtc = _now(), ContentHash = contentHash });

    public void Unmark(string kb)
    {
        store.Update(s => s.Reviewed.Remove(kb));
        try { if (SnapshotPath(kb) is { } p && File.Exists(p)) File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class ReviewSnapshot(IReadOnlyDictionary<string, ReviewEntry> entries)
{
    public static readonly ReviewSnapshot Empty = new(new Dictionary<string, ReviewEntry>());

    public ReviewStatus StatusOf(string kb, string? currentHash)
    {
        if (!entries.TryGetValue(kb, out var e)) return ReviewStatus.None;
        // No fresh data to compare (fetch failed this time): keep showing what the user did.
        if (string.IsNullOrEmpty(currentHash)) return ReviewStatus.Reviewed;
        return string.Equals(e.ContentHash, currentHash, StringComparison.Ordinal) ? ReviewStatus.Reviewed : ReviewStatus.ChangedSinceReviewed;
    }

    public DateTime? ReviewedUtc(string kb) => entries.TryGetValue(kb, out var e) ? e.ReviewedUtc : null;
}
