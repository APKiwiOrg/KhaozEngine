using System.Collections.Generic;

namespace KhaozEngine.Replication;

/// <summary>
/// One slot's record of what <see cref="AoiDeltaReplicator"/> told the client about presence after the acked
/// baseline: the latest seq at which each net id was written as a removal, and the latest seq at which it was written
/// as a whole entry. The diff runs from the ACKED baseline, but a reliable-ordered client applies every delta in
/// order, so what it holds is what was last sent, not what was last acked. These records close that gap in both
/// directions.
/// </summary>
/// <remarks>
/// A record at or before the acked seq is already reflected in the baseline, so <see cref="Prune"/> drops it on
/// acknowledge. One instance per slot, reused across a seat's lifetime and cleared on forget, so steady state
/// allocates nothing.
/// </remarks>
internal sealed class AoiPresenceRecord
{
    public Dictionary<long, int> Removed { get; } = new();
    public Dictionary<long, int> Whole { get; } = new();

    /// <summary>True when a removal of <paramref name="netId"/> newer than <paramref name="baselineSeq"/> was sent,
    /// so the client may have despawned an entity the baseline still holds.</summary>
    public static bool RemovedSince(AoiPresenceRecord? record, int baselineSeq, long netId) =>
        record is not null && record.Removed.TryGetValue(netId, out int removedAt) && removedAt > baselineSeq;

    /// <summary>Drops every record at or before <paramref name="ackedSeq"/>. Remove during enumeration is allowed on
    /// <see cref="Dictionary{TKey, TValue}"/> and allocates nothing.</summary>
    public void Prune(int ackedSeq)
    {
        foreach (KeyValuePair<long, int> kv in Removed)
            if (kv.Value <= ackedSeq) Removed.Remove(kv.Key);
        foreach (KeyValuePair<long, int> kv in Whole)
            if (kv.Value <= ackedSeq) Whole.Remove(kv.Key);
    }

    public void Clear()
    {
        Removed.Clear();
        Whole.Clear();
    }
}
