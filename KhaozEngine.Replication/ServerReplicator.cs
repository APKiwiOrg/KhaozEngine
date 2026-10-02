using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using CapturedState = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>
/// Server-side, whole-world baseline+delta replicator - the client-serving delta path for a world where every
/// client sees every entity (no area-of-interest scoping; use <see cref="AoiDeltaReplicator"/> when it does). Each
/// tick the game calls <see cref="Capture"/> to snapshot the world once; per client it calls <see cref="WriteFor"/>
/// to get a delta from the projection last sent to that client's slot to the latest snapshot (a full snapshot on the
/// slot's first serve). Sends only entities/components that changed since that last sent projection.
/// </summary>
/// <remarks>
/// <para>
/// Legacy reliable contract: every returned <see cref="WriteFor"/> payload is a send commitment. Ship each one
/// exactly once, in order, over a reliable-ordered channel, so the receiver always holds the projection the next
/// payload names as its baseline. <see cref="Acknowledge"/> is sequence-only diagnostics and never selects a diff
/// basis, so a value that reverts while an ack is in flight still reaches the receiver. A caller that discards a
/// returned payload, sends it unreliably, changes session or replaces the receiver must <see cref="Forget"/> the slot
/// and serve a fresh receiver world and view. A full legacy entity does not remove a component the receiver already
/// holds, so reusing the old receiver world after <see cref="Forget"/> is unsupported. A send failure likewise needs a
/// new receiver connection.
/// </para>
/// <para>
/// This is a client-serving path, so it honours <see cref="ReplicationChannels"/> exactly as
/// <see cref="SnapshotWriter"/> / <see cref="AoiDeltaReplicator"/> do: <see cref="Capture"/> snapshots only
/// components on the <see cref="ReplicationChannels.Replicate"/> channel (a <see cref="ReplicationChannels.Persist"/>-
/// or <see cref="ReplicationChannels.Migrate"/>-only server component is never captured, so it can never reach a
/// client), and <see cref="WriteFor"/> takes the receiving client's own player net id so an
/// <see cref="ReplicationChannels.OwnerOnly"/> component reaches only its owner. The single shared capture holds
/// every player's owner-only bytes. <see cref="WriteFor"/> projects them per client and stores that exact projection
/// for the slot, so an owner change is diffed against what the receiver really holds and never re-projects older
/// bytes. A registry using only <see cref="ReplicationChannels.Default"/> captures every component for every client
/// regardless of owner, so the wire is byte-identical to before channels existed.
/// </para>
/// <para>
/// The writer retains only the current capture plus one last sent projection per slot. Sequences are one signed
/// counter shared by every slot. <see cref="int.MaxValue"/> is the last capture sequence: after it
/// <see cref="LegacySequenceExhausted"/> is true and <see cref="Capture"/> throws until the owner has ended every
/// connection this writer serves and called <see cref="ResetAfterLegacySequenceExhaustion"/>.
/// </para>
/// <para>
/// Wire (per delta): <c>[baselineSeq][snapshotSeq][removedCount][removedNetId...][changedCount]</c> then per
/// changed/new entity <c>[netId][isNew][removedCompCount][removedTypeId...][(typeId,[len],data)...][0]</c>, the
/// 7-bit <c>len</c> present only for consumer extension components (see
/// <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) so an older client can skip an unknown id. New
/// entities carry all their components; existing ones carry only changed/added components. Snapshots are opaque
/// <c>byte[]</c> the game ships over its session transport (the matching ack flows back the same way).
/// </para>
/// </remarks>
public sealed class ServerReplicator
{
    private readonly ReplicationRegistry registry;
    private readonly bool hasOwnerScopedCodec;
    private CapturedState? current;
    private readonly Dictionary<int, LegacyDeltaSlot> slots = new();
    private int currentSeq;

    // Single-threaded per instance (one server tick thread): the capture scratch and the wire scratch below are reused
    // across ticks and clients with no locking. The capture scratch consolidates each snapshot into one buffer. The
    // wire scratch (stream + reused changed/removed lists) builds each client's delta without a per-call allocation
    // beyond the returned wire array.
    private readonly CaptureScratch captureScratch = new();
    private readonly MemoryStream wireStream = new();
    private readonly BinaryWriter wireWriter;
    private readonly List<long> scratchRemoved = new();
    private readonly List<long> scratchChanged = new();

    /// <summary>Creates a whole-world legacy delta writer.</summary>
    /// <param name="registry">The replicated component registry.</param>
    /// <param name="historyDepth">Kept for source compatibility and still validated as positive. The legacy diff
    /// retains only the current capture and one last sent projection per slot, so it no longer bounds a history.</param>
    public ServerReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        wireWriter = new BinaryWriter(wireStream);
        // Whether any registered component is owner-scoped. If none is, the shared capture is already the same for
        // every client, so WriteFor can skip per-client projection entirely and stay allocation-free + byte-identical.
        foreach (ComponentCodec codec in registry.Ordered)
            if ((codec.Channels & ReplicationChannels.OwnerOnly) != 0) { hasOwnerScopedCodec = true; break; }
    }

    /// <summary>The latest captured snapshot sequence (0 before the first <see cref="Capture"/>).</summary>
    public int CurrentSeq => currentSeq;

    /// <summary>
    /// True once <see cref="CurrentSeq"/> has reached <see cref="int.MaxValue"/>, the last legacy sequence. A further
    /// <see cref="Capture"/> throws without scanning or incrementing. Serving the final capture is still allowed.
    /// </summary>
    public bool LegacySequenceExhausted => currentSeq == int.MaxValue;

    /// <summary>
    /// Restarts the writer after <see cref="LegacySequenceExhausted"/>: clears the shared sequence counter to zero, the
    /// retained capture and every slot's last sent state. The next <see cref="Capture"/> is sequence 1 and every
    /// slot's next serve is full state with baseline -1. Precondition: the owner has already ended every connection
    /// this writer served and will serve only fresh receivers. Replication cannot verify or end transports itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sequence is not exhausted.</exception>
    public void ResetAfterLegacySequenceExhaustion()
    {
        if (!LegacySequenceExhausted)
            throw new InvalidOperationException("The legacy sequence is not exhausted, so the writer cannot be reset.");
        currentSeq = 0;
        current = null;
        slots.Clear();
    }

    /// <summary>
    /// Drops <paramref name="slot"/>'s last sent state (call on disconnect, slot recycle, or any payload that was not
    /// delivered). Its next serve is full state with baseline -1, valid only for a fresh receiver world and view.
    /// Never resets the shared sequence counter.
    /// </summary>
    public void Forget(int slot) => slots.Remove(slot);

    // Test seam: moves the shared counter forward so a test can reach the exhaustion boundary without walking the
    // signed range. It never moves backwards, so it cannot revive a sequence a receiver already holds.
    internal void SeedLegacySequenceForTest(int sequence)
    {
        if (sequence < currentSeq) throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "must not move backwards");
        currentSeq = sequence;
    }

    // Test seams: slots holding last sent state, and retained captures (at most one, the current capture).
    internal int LegacySlotCount => slots.Count;

    internal int LegacyHistoryCount => current is null ? 0 : 1;

    /// <summary>
    /// Snapshots the world's <see cref="NetId"/> entities and their <see cref="ReplicationChannels.Replicate"/>-channel
    /// components into a new seq. Persist-/Migrate-only server components are NOT captured (they never go on a client
    /// wire); owner-only components ARE captured (keyed under their entity) and scoped per client in
    /// <see cref="WriteFor"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The legacy sequence is exhausted (see
    /// <see cref="LegacySequenceExhausted"/>). Nothing is scanned and the counter does not move.</exception>
    public int Capture(World world)
    {
        if (LegacySequenceExhausted)
            throw new InvalidOperationException(
                "The legacy sequence is exhausted. End every connection, then call ResetAfterLegacySequenceExhaustion.");

        // One shared buffer for the whole snapshot, owner-only components are captured here (they carry Replicate) and
        // stripped per non-owning client in WriteFor. A slot's last sent projection references the buffer of the
        // capture it was built from, so a buffer lives as long as any such projection - the CaptureScratch never
        // returns it to a pool.
        current = captureScratch.CaptureReplicate(world, registry);
        currentSeq++;
        return currentSeq;
    }

    /// <summary>
    /// Records that <paramref name="slot"/> applied up to <paramref name="seq"/>. Sequence-only diagnostics: an
    /// acknowledgement never selects the diff basis, which is always the slot's last sent projection.
    /// </summary>
    public void Acknowledge(int slot, int seq)
    {
        if (slots.TryGetValue(slot, out LegacyDeltaSlot? state)) state.Acknowledge(seq);
    }

    /// <summary>
    /// Builds the delta for <paramref name="slot"/> from the projection last sent to it to the latest snapshot, then
    /// stores this payload's projection as the slot's new last sent state. On the
    /// <see cref="ReplicationChannels.Replicate"/> channel an <see cref="ReplicationChannels.OwnerOnly"/> component is
    /// included only for the entity whose net id equals <paramref name="ownerNetId"/> (this slot's own player), and
    /// for nobody when it is null (an unowned serve). The stored projection keeps the owner scope it was sent with,
    /// so an owner change removes the old owner's private components explicitly. A registry with no owner-only
    /// component ignores <paramref name="ownerNetId"/> (the wire is then byte-identical to before channels existed).
    /// The returned payload is a send commitment (see the type remarks).
    /// </summary>
    public byte[] WriteFor(int slot, long? ownerNetId = null)
    {
        if (current is null) throw new InvalidOperationException("Capture at least once before WriteFor.");

        LegacyDeltaSlot? previous = slots.GetValueOrDefault(slot);
        CapturedState? baseline = previous?.Projection;
        int baselineSeq = previous?.Sequence ?? -1;
        // Owner-scope the current capture so OwnerOnly bytes never leak and the diff reflects only what THIS client
        // can see (a no-op returning the shared state when the registry has no owner-only component).
        CapturedState projected = Project(current, ownerNetId);

        wireStream.SetLength(0); // reset the reused wire scratch, keep its capacity
        BinaryWriter bw = wireWriter;
        bw.Write(baselineSeq);
        bw.Write(currentSeq);

        // Removed entities: present in the last sent projection, gone from current. Reused scratch lists, cleared per call.
        scratchRemoved.Clear();
        if (baseline is not null)
            foreach (long netId in baseline.Keys)
                if (!projected.ContainsKey(netId)) scratchRemoved.Add(netId);
        bw.Write(scratchRemoved.Count);
        foreach (long netId in scratchRemoved) bw.Write(netId);

        // New or changed entities.
        scratchChanged.Clear();
        foreach (long netId in projected.Keys)
        {
            if (baseline is null || !baseline.ContainsKey(netId)) { scratchChanged.Add(netId); continue; }
            if (DeltaEncoding.EntityChanged(baseline[netId], projected[netId])) scratchChanged.Add(netId);
        }
        bw.Write(scratchChanged.Count);
        foreach (long netId in scratchChanged)
        {
            bool isNew = baseline is null || !baseline.ContainsKey(netId);
            DeltaEncoding.WriteChangedEntity(bw, registry, netId, isNew, isNew ? null : baseline![netId], projected[netId]);
        }

        bw.Flush();
        byte[] payload = wireStream.ToArray(); // fresh exact-size array, the caller owns it
        // Only a completely built payload becomes the slot's last sent state.
        if (previous is null) slots[slot] = new LegacyDeltaSlot(currentSeq, projected);
        else previous.Sent(currentSeq, projected);
        return payload;
    }

    /// <summary>
    /// Owner-scopes a shared capture for one client: drops each <see cref="ReplicationChannels.OwnerOnly"/> component
    /// whose entity this client does not own (its net id != <paramref name="ownerNetId"/>). Plain
    /// <see cref="ReplicationChannels.Replicate"/> components pass through for everyone, so the result is exactly the
    /// replicate-channel state this client is entitled to - the baseline+current diff can then never put another
    /// player's owner-only bytes on this client's wire. Returns the input unchanged (no allocation) when the registry
    /// has no owner-only component, keeping the common case byte-identical to before channels existed.
    /// </summary>
    private CapturedState Project(CapturedState raw, long? ownerNetId)
    {
        if (!hasOwnerScopedCodec) return raw; // nothing owner-scoped: the shared capture is already per-client-correct

        var projected = new CapturedState(raw.Count);
        foreach (KeyValuePair<long, CapturedComponents> entity in raw)
            projected[entity.Key] = CaptureProjection.OwnerScope(entity.Value, registry, entity.Key, ownerNetId);
        return projected;
    }
}
