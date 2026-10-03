using System;
using System.Collections.Generic;

namespace KhaozEngine.Replication;

/// <summary>
/// One writer slot's format 2 stream inside one epoch: the bounded retention table, the unsent candidate, the
/// acknowledged baseline, a committed keyframe awaiting its exact acknowledgement, and the committed sends that may
/// still be acknowledged. A new epoch replaces the whole object, so retired state can never leak into it.
/// </summary>
/// <remarks>
/// Pins handed to retention are the acknowledged baseline, the pending keyframe and the candidate, at most three.
/// Committed sends are unpinned and pruned oldest insertion first. Every committed send gets an ordinal in commit
/// order, so the no-ack count survives pruning. Entries made obsolete by an acknowledgement are removed as soon as
/// retention no longer pins them.
/// </remarks>
internal sealed class RebuildDeltaSlot
{
    private readonly List<(ReplicationPacketId Id, long Ordinal)> committed = new();   // insertion order
    private readonly List<ReplicationPacketId> obsolete = new();
    private readonly HashSet<ReplicationPacketId> pinScratch = new();
    private long commitCount;
    private long acknowledgedOrdinal;
    private bool ownerBound;
    private bool candidateIsKeyframe;

    public RebuildDeltaSlot(ulong epoch, DeltaRebuildOptions options)
    {
        Epoch = epoch;
        Options = options;
        Retention = new ProjectionRetention(options);
    }

    public ulong Epoch { get; }

    public DeltaRebuildOptions Options { get; }

    public ProjectionRetention Retention { get; }

    /// <summary>The sequence the next build uses. Starts at 1 and wraps through <see cref="uint.MaxValue"/> to 0.</summary>
    public uint NextSequence { get; set; } = 1;

    public long? OwnerNetId { get; private set; }

    public ReplicationPacketId? Candidate { get; private set; }

    public ReplicationPacketId? Acknowledged { get; private set; }

    public ReplicationPacketId? PendingKeyframe { get; private set; }

    /// <summary>Backing bytes of the newest built projection, the estimate for the next one.</summary>
    public long LatestBytes { get; private set; }

    /// <summary>Committed sends after the acknowledged one, or every committed send when none is acknowledged.</summary>
    public int NewSentCount => (int)(commitCount - acknowledgedOrdinal);

    /// <summary>The newest committed send still eligible for acknowledgement, or null.</summary>
    public ReplicationPacketId? NewestCommitted => committed.Count == 0 ? null : committed[^1].Id;

    /// <summary>True when <paramref name="ownerNetId"/> matches the owner the epoch's first build bound.</summary>
    public bool OwnerMatches(long? ownerNetId) => !ownerBound || OwnerNetId == ownerNetId;

    /// <summary>The pins a new candidate <paramref name="id"/> is retained with.</summary>
    public IReadOnlySet<ReplicationPacketId> PinsWith(ReplicationPacketId id)
    {
        pinScratch.Clear();
        if (Acknowledged is ReplicationPacketId acked) pinScratch.Add(acked);
        if (PendingKeyframe is ReplicationPacketId pending) pinScratch.Add(pending);
        pinScratch.Add(id);
        return pinScratch;
    }

    /// <summary>Distinct backing bytes of the projections pinned beside a new candidate.</summary>
    public long PinnedBytes()
    {
        // Writer projections are compact, so two different pinned projections never share a backing array.
        ReplicationProjection? acked = null;
        long bytes = 0;
        if (Acknowledged is ReplicationPacketId a && Retention.TryGet(a, out acked)) bytes += acked.BackingBytes;
        if (PendingKeyframe is ReplicationPacketId k && Retention.TryGet(k, out ReplicationProjection pending)
            && !ReferenceEquals(pending, acked))
            bytes += pending.BackingBytes;
        return bytes;
    }

    /// <summary>Records a successful retention of <paramref name="id"/> as the new unsent candidate.</summary>
    public void Built(ReplicationPacketId id, bool keyframe, long? ownerNetId, long projectionBytes)
    {
        Candidate = id;
        candidateIsKeyframe = keyframe;
        NextSequence = unchecked(id.Sequence + 1);
        OwnerNetId = ownerNetId;
        ownerBound = true;
        LatestBytes = projectionBytes;
        for (int i = committed.Count - 1; i >= 0; i--)
            if (!Retention.TryGet(committed[i].Id, out _)) committed.RemoveAt(i);
        SweepObsolete();
    }

    /// <summary>Commits the exact candidate after a successful transport handoff.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> is not the unsent candidate.</exception>
    public void Sent(ReplicationPacketId id)
    {
        if (Candidate != id)
            throw new InvalidOperationException($"Format 2 packet {id} is not this slot's unsent candidate.");
        Candidate = null;
        committed.Add((id, ++commitCount));
        if (candidateIsKeyframe) PendingKeyframe = id;
    }

    /// <summary>Promotes a committed, retained, strictly newer id of this epoch. Anything else is ignored.</summary>
    public void Acknowledge(ReplicationPacketId id)
    {
        if (id.Epoch != Epoch || !Retention.TryGet(id, out _)) return;
        if (Acknowledged is ReplicationPacketId current && !ReplicationSequence.IsNewer(id.Sequence, current.Sequence))
            return;
        int index = committed.Count - 1;
        while (index >= 0 && committed[index].Id != id) index--;
        if (index < 0) return;

        if (Acknowledged is ReplicationPacketId previous) obsolete.Add(previous);
        for (int i = 0; i < index; i++) obsolete.Add(committed[i].Id);
        acknowledgedOrdinal = committed[index].Ordinal;
        committed.RemoveRange(0, index + 1);
        Acknowledged = id;
        if (PendingKeyframe is ReplicationPacketId pending
            && (pending == id || ReplicationSequence.IsNewer(id.Sequence, pending.Sequence)))
        {
            if (pending != id) obsolete.Add(pending);
            PendingKeyframe = null;
        }
        SweepObsolete();
    }

    // Removes obsolete entries retention no longer pins, keeps the pinned ones for the next sweep, and forgets ids
    // retention has already pruned.
    private void SweepObsolete()
    {
        for (int i = obsolete.Count - 1; i >= 0; i--)
        {
            ReplicationPacketId id = obsolete[i];
            if (id == Acknowledged || id == PendingKeyframe || id == Candidate) obsolete.RemoveAt(i);
            else if (!Retention.TryGet(id, out _) || Retention.Remove(id)) obsolete.RemoveAt(i);
        }
    }
}
