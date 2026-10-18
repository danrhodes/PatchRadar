namespace PatchRadar.State;

public enum ReviewStatus
{
    None,
    Reviewed,
    /// <summary>Was reviewed, but Microsoft has edited the article since (its content hash differs).</summary>
    ChangedSinceReviewed,
}

/// <summary>Per-KB "I have looked at this" flags, tied to the KB content hash.</summary>
public sealed class ReviewStore(StateStore store, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    /// <summary>One read of state.json for a whole list, instead of one per row.</summary>
    public ReviewSnapshot Snapshot() => new(store.Load().Reviewed);

    public void Mark(string kb, string contentHash) =>
        store.Update(s => s.Reviewed[kb] = new ReviewEntry { ReviewedUtc = _now(), ContentHash = contentHash });

    public void Unmark(string kb) => store.Update(s => s.Reviewed.Remove(kb));
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
