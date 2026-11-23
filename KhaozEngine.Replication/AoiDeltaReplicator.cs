using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using Comps = KhaozEngine.Replication.CapturedComponents;
using AoiBaseline = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>
/// Per-client, area-of-interest-scoped, <see cref="NetId"/>-keyed baseline+delta encoder: the fusion of
/// <see cref="ServerReplicator"/>'s delta compression with per-client AoI filtering. Each server tick the game calls
/// <see cref="BeginTick"/> once, then <see cref="WriteFor"/> per client with that client's current interest set (the
/// net ids within its AoI). Against the projection last sent to the client's slot it emits: an entity that
/// <b>entered</b> the interest set as a full spawn, one that <b>stayed and changed</b> as only its changed
/// components, one that <b>left</b> (or was despawned) as a removal, and an unchanged in-AoI entity as nothing.
/// The wire is byte-identical to <see cref="ServerReplicator.WriteFor"/> (a full snapshot is the <c>baseline -1</c>
/// delta), so <see cref="ClientReplicationView.ApplyDelta"/> decodes both unchanged.
/// </summary>
/// <remarks>
/// <para>The baseline is keyed by <see cref="NetId"/>, not by any owning cell, so an entity that stays in a client's AoI
/// while changing owning cell (a seamless handoff in the sharding layer) reads as a component delta, never a
/// despawn+respawn.</para>
/// <para>Legacy reliable contract: every returned <see cref="WriteFor"/> payload is a send commitment. Ship each one
/// exactly once, in order, over a reliable-ordered channel, so the receiver always holds the projection the next
/// payload names as its baseline. Presence, values and owner scope all follow what was last sent. An entity removed
/// and back in interest returns whole, and one sent and gone again is removed once, whatever the ack state.
/// <see cref="Acknowledge"/> is sequence-only diagnostics and never selects a diff basis. A caller that discards a
/// returned payload, sends it unreliably, changes session or replaces the receiver must <see cref="Forget"/> the slot
/// and serve a fresh receiver world and view. Each slot keeps its last sent projection and may keep an empty
/// spare for the next build.</para>
/// <para>Format 2 acknowledged rebuild streams (<see cref="StartRebuild"/>, <see cref="BuildRebuildFor"/>,
/// <see cref="RecordRebuildSent"/>, <see cref="AcknowledgeRebuild"/>) share the same per-world tick capture as legacy
/// viewers but keep separate per-slot state: an unsigned per-slot sequence inside a nonzero epoch, one unsent
/// candidate, an acknowledged baseline and a bounded history of committed sends, all as compact viewer-only copies
/// filtered before retention. A delta diffs from the acknowledged baseline, never from presence records.</para>
/// <para>Sequences are one signed counter shared by every slot. <see cref="int.MaxValue"/> is the last tick sequence:
/// after it <see cref="LegacySequenceExhausted"/> is true and <see cref="BeginTick"/> throws until the owner has ended
/// every connection this writer serves and called <see cref="ResetAfterLegacySequenceExhaustion"/>.</para>
/// </remarks>
public sealed class AoiDeltaReplicator
{
    private readonly ReplicationRegistry registry;
    private int currentSeq;

    // Per slot: the sequence and exact AoI-scoped, owner-scoped projection of the last payload returned for it, which
    // is what a reliable-ordered client holds. The next payload diffs from it.
    private readonly Dictionary<int, AoiLegacyDeltaSlot> slots = new();

    // Shared per-tick capture: the whole-world Replicate-channel snapshot (netId -> components), captured ONCE per
    // distinct world per seq and projected per client in WriteFor. Keyed by World because the sharded server serves
    // different clients from different home-cell worlds within one tick. The non-sharded server passes one world, so
    // this collapses to a single capture per tick. captureSeq marks which seq the cache belongs to (cleared lazily on
    // the first WriteFor of a new seq, so BeginTick stays a cheap seq bump).
    private readonly Dictionary<World, AoiBaseline> captureByWorld = new();
    private int captureSeq;

    // Single caller per instance: the capture scratch consolidates each world's capture into one buffer.
    // The wire writer reuses its body storage across slots. The owning API copies that body before committing.
    private readonly CaptureScratch captureScratch = new();
    private readonly LegacyDeltaBodyWriter wire = new();
    private bool wireInUse;
    private Exception? legacyBodyFailureForTests;
    private readonly AoiProjectionBuilder projector;
    private readonly RebuildDeltaWriter rebuild;

    // Test seam: how many world scans the shared capture has actually run (one per distinct world per tick). A tick
    // that serves C clients from one world scans once, not C times - what this whole change buys.
    internal long WorldScanCount { get; private set; }

    // Test seams: slots holding last sent state, and the shared captures retained for the current seq (one per
    // distinct world served this seq, until the next seq's first WriteFor drops them).
    internal int LegacySlotCount => slots.Count;

    internal int LegacyHistoryCount => captureByWorld.Count;

    internal Func<int> ObserveCandidateCountForTests(int slot) => slots[slot].ObserveCandidateCountForTests();

    internal (WeakReference<Comps> Source, WeakReference<Comps>? Public, WeakReference<byte[]> Payload)
        CaptureReferencesForTests(World world, long netId) => captureByWorld[world][netId].ReferencesForTests();

    internal LegacyDeltaAllocationProbe? LegacyAllocationsForTests { get; set; }
    internal void FailNextLegacyBodyCompletionForTests(Exception failure) =>
        legacyBodyFailureForTests = failure ?? throw new ArgumentNullException(nameof(failure));

    /// <summary>Creates an area-of-interest legacy delta writer.</summary>
    /// <param name="registry">The replicated component registry.</param>
    /// <param name="historyDepth">Kept for source compatibility and still validated as positive. The legacy diff
    /// retains one last sent projection per slot, so it no longer bounds a pending history.</param>
    public AoiDeltaReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        projector = new AoiProjectionBuilder(registry);
        rebuild = new RebuildDeltaWriter(registry);
    }

    /// <summary>The latest snapshot sequence (0 before the first <see cref="BeginTick"/>).</summary>
    public int CurrentSeq => currentSeq;

    /// <summary>
    /// True once <see cref="CurrentSeq"/> has reached <see cref="int.MaxValue"/>, the last legacy sequence. A further
    /// <see cref="BeginTick"/> throws without incrementing. Serving the final tick is still allowed.
    /// </summary>
    public bool LegacySequenceExhausted => currentSeq == int.MaxValue;

    /// <summary>
    /// Restarts the writer after <see cref="LegacySequenceExhausted"/>: clears the shared sequence counter to zero, the
    /// shared per-tick captures and every slot's last sent state, format 2 state included. The next
    /// <see cref="BeginTick"/> is sequence 1 and every slot's next serve is full state with baseline -1. Precondition:
    /// the owner has already ended every connection this writer served and will serve only fresh receivers.
    /// Replication cannot verify or end transports itself. The reset remembers no format 2 epoch, so the owner must
    /// start each new stream with an epoch greater than any it issued before.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sequence is not exhausted.</exception>
    public void ResetAfterLegacySequenceExhaustion()
    {
        if (!LegacySequenceExhausted)
            throw new InvalidOperationException("The legacy sequence is not exhausted, so the writer cannot be reset.");
        currentSeq = 0;
        captureByWorld.Clear();
        captureSeq = 0;
        slots.Clear();
        rebuild.Clear();
    }

    // Test seam: moves the shared counter forward so a test can reach the exhaustion boundary without walking the
    // signed range. It never moves backwards, so it cannot revive a sequence a receiver already holds.
    internal void SeedLegacySequenceForTest(int sequence)
    {
        if (sequence < currentSeq) throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "must not move backwards");
        currentSeq = sequence;
    }

    /// <summary>Opens a new snapshot sequence for this server tick. Call once per tick, before the per-client
    /// <see cref="WriteFor"/> pass. Returns the new seq (the value a client acks after applying this tick's delta).
    /// The shared per-tick capture is invalidated lazily on the first <see cref="WriteFor"/> of the new seq, so this
    /// stays a cheap sequence bump.</summary>
    /// <exception cref="InvalidOperationException">The legacy sequence is exhausted (see
    /// <see cref="LegacySequenceExhausted"/>). The counter does not move.</exception>
    public int BeginTick()
    {
        if (LegacySequenceExhausted)
            throw new InvalidOperationException(
                "The legacy sequence is exhausted. End every connection, then call ResetAfterLegacySequenceExhaustion.");
        return ++currentSeq;
    }

    /// <summary>
    /// Builds the AoI delta for <paramref name="slot"/> from the projection last sent to it to the entities of
    /// <paramref name="world"/> whose <see cref="NetId"/> is in <paramref name="interestSet"/>, then stores this
    /// payload's projection as the slot's new last sent state. Full snapshot (baseline -1) on the slot's first serve.
    /// Must be called after <see cref="BeginTick"/>. This is the client-serving
    /// (<see cref="ReplicationChannels.Replicate"/>) path, so only replicated components are captured, and an
    /// <see cref="ReplicationChannels.OwnerOnly"/> component is served only to the entity whose net id equals
    /// <paramref name="ownerNetId"/> (this slot's own player). Because the per-slot state stores exactly what was
    /// projected for THIS slot, owner-only visibility falls out of the delta diff automatically. The returned payload
    /// is a send commitment (see the type remarks).
    /// </summary>
    /// <remarks>
    /// The world is scanned and captured ONCE per <paramref name="world"/> per tick (the first <see cref="WriteFor"/>
    /// after a <see cref="BeginTick"/> that sees it), shared across every client served from that world. The projection iterates this client's
    /// interest set and resolves each entity's captured components in O(1), ordering the selection by capture position so the
    /// changed-entity wire order is identical to walking the whole capture. So the per-client cost scales with the
    /// client's area of interest, not the world population. The wire is byte-identical to capturing per client.
    /// </remarks>
    public byte[] WriteFor(int slot, World world, IReadOnlySet<long> interestSet, long? ownerNetId = null)
    {
        if (wireInUse)
            return BuildFor(new LegacyDeltaBodyWriter(), slot, world, interestSet, ownerNetId,
                prefixLength: 0, owned: true)!;

        wireInUse = true;
        try { return BuildFor(wire, slot, world, interestSet, ownerNetId, prefixLength: 0, owned: true)!; }
        finally { wireInUse = false; }
    }

    // NetWorld fills the reserved prefix and hands these bytes to a synchronous transport call. A nested
    // build while this writer is in use takes the owning path with independent scratch and an owned frame.
    internal ServeFrame BorrowForServe(int slot, World world, IReadOnlySet<long> interestSet, long ownerNetId,
        int prefixLength)
    {
        if (wireInUse)
            return new ServeFrame(null, BuildFor(new LegacyDeltaBodyWriter(), slot, world, interestSet, ownerNetId,
                prefixLength, owned: true)!);

        wireInUse = true;
        try
        {
            BuildFor(wire, slot, world, interestSet, ownerNetId, prefixLength, owned: false);
            return new ServeFrame(this, wire.WrittenSpan);
        }
        catch
        {
            wireInUse = false;
            throw;
        }
    }

    internal void CompleteLegacyServePass(long passOrdinal, int largestFrame)
    {
        if (!wireInUse) wire.ReleaseOversize(passOrdinal, largestFrame);
    }

    internal readonly ref struct ServeFrame(AoiDeltaReplicator? lender, Span<byte> bytes)
    {
        internal Span<byte> Bytes { get; } = bytes;

        public void Dispose()
        {
            if (lender is not null) lender.wireInUse = false;
        }
    }

    private byte[]? BuildFor(LegacyDeltaBodyWriter destination, int slot, World world, IReadOnlySet<long> interestSet,
        long? ownerNetId, int prefixLength, bool owned)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (interestSet is null) throw new ArgumentNullException(nameof(interestSet));
        if (currentSeq == 0) throw new InvalidOperationException("Call BeginTick before WriteFor.");

        LegacyDeltaAllocationProbe? probe = LegacyAllocationsForTests;
        long phaseStart = LegacyDeltaAllocationProbe.Start(probe);
        AoiBaseline capture = CaptureFor(world);
        probe?.Record(LegacyDeltaPhase.Capture, phaseStart);

        phaseStart = LegacyDeltaAllocationProbe.Start(probe);
        bool recycle = ReferenceEquals(destination, wire);
        AoiBaseline projected = recycle && slots.TryGetValue(slot, out AoiLegacyDeltaSlot? state)
            ? state.PrepareCandidate()
            : new AoiBaseline();
        bool committed = false;
        try
        {
            projector.Fill(projected, capture, interestSet, ownerNetId, useSharedScratch: recycle);
            probe?.Record(LegacyDeltaPhase.Projection, phaseStart);
            phaseStart = LegacyDeltaAllocationProbe.Start(probe);

            // The interest set can run caller code. Resolve the committed baseline after its enumeration.
            AoiLegacyDeltaSlot? previous = slots.GetValueOrDefault(slot);
            destination.Write(registry, projected, previous?.Projection, previous?.Sequence ?? -1,
                currentSeq, prefixLength, ref legacyBodyFailureForTests);
            byte[]? payload = owned ? destination.ToArray() : null;

            // Only a completely built payload becomes the slot's last sent state.
            if (previous is null) slots[slot] = new AoiLegacyDeltaSlot(currentSeq, projected);
            else previous.Commit(currentSeq, projected, recycle);
            committed = true;
            probe?.Record(LegacyDeltaPhase.Body, phaseStart);
            return payload;
        }
        finally
        {
            if (!committed) projected.Clear();
        }
    }

    /// <summary>
    /// The shared whole-world capture for <paramref name="world"/> at the current seq: every <see cref="NetId"/>
    /// entity's <see cref="ReplicationChannels.Replicate"/>-channel components (owner-only ones included, keyed under
    /// their entity, to be scoped in each viewer projection). Captured once per world per tick and reused by
    /// every later <see cref="WriteFor"/> in the same tick. Insertion order is the world's <c>ForEach</c> order, which
    /// the per-client projection preserves so the changed-entity wire order is unchanged.
    /// </summary>
    private AoiBaseline CaptureFor(World world)
    {
        if (captureSeq != currentSeq)
        {
            captureByWorld.Clear();   // a new tick: the previous tick's captures are stale, drop them
            captureSeq = currentSeq;
        }
        if (captureByWorld.TryGetValue(world, out AoiBaseline? cached)) return cached;

        // One shared buffer per world capture. Owner-only components are scoped in the viewer projection.
        // A slot's last sent projection references the buffer until that slot is successfully served
        // again or forgotten - each capture's payload buffer is a fresh array, so it is never reused while a slot
        // still references it.
        AoiBaseline state = captureScratch.CaptureReplicate(world, registry);
        captureByWorld[world] = state;
        WorldScanCount++;
        return state;
    }

    /// <summary>
    /// Records that <paramref name="slot"/> applied up to <paramref name="seq"/>. Sequence-only diagnostics: an
    /// acknowledgement never selects the diff basis, which is always the slot's last sent projection.
    /// </summary>
    public void Acknowledge(int slot, int seq)
    {
        if (slots.TryGetValue(slot, out AoiLegacyDeltaSlot? state)) state.Acknowledge(seq);
    }

    /// <summary>
    /// Drops all per-client state for <paramref name="slot"/> (call on disconnect, slot recycle, or any payload that
    /// was not delivered), format 2 state included. Its next serve is full state with baseline -1, valid only for a
    /// fresh receiver world and view. Never resets the shared sequence counter.
    /// </summary>
    public void Forget(int slot)
    {
        slots.Remove(slot);
        rebuild.Forget(slot);
    }

    /// <summary>
    /// Starts or replaces <paramref name="slot"/>'s format 2 stream with <paramref name="epoch"/> and
    /// <paramref name="options"/>, clearing its candidate, acknowledged baseline, pins and committed history. A slot
    /// that already holds format 2 state accepts only a greater epoch. Never touches the legacy sequence counter.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The epoch is zero or not greater than the slot's current one, or
    /// <paramref name="options"/> is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public void StartRebuild(int slot, ulong epoch, DeltaRebuildOptions options) =>
        rebuild.Start(slot, epoch, options);

    /// <summary>
    /// Builds <paramref name="slot"/>'s next format 2 packet from this tick's shared capture of
    /// <paramref name="world"/>, filtered to <paramref name="interestSet"/> and owner-scoped exactly like
    /// <see cref="WriteFor"/>, and retains it as the slot's one unsent candidate, replacing any older unsent candidate.
    /// The packet is a delta from the acknowledged baseline, or a keyframe from empty state when
    /// <paramref name="keyframe"/> is set or nothing is acknowledged yet. Check <see cref="RebuildNeedsRepair"/> before
    /// every build. A failed build leaves the slot unchanged.
    /// </summary>
    /// <param name="slot">The started slot.</param>
    /// <param name="world">The world this slot is served from this tick.</param>
    /// <param name="interestSet">The net ids this slot may see.</param>
    /// <param name="ownerNetId">This slot's own player net id, which must stay the same for the whole epoch.</param>
    /// <param name="keyframe">True to build from empty state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> or <paramref name="interestSet"/> is
    /// null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="BeginTick"/> was never called, the slot has no started
    /// stream, or the owner differs from the epoch's owner.</exception>
    /// <exception cref="DeltaRebuildException">The projection can never fit
    /// (<see cref="DeltaRebuildFailure.CapacityExceeded"/>), pins leave no room for it
    /// (<see cref="DeltaRebuildFailure.RetentionPressure"/>), or the next sequence is not ordered after the baseline
    /// (<see cref="DeltaRebuildFailure.SequenceAmbiguous"/>).</exception>
    public ReplicationDeltaPacket BuildRebuildFor(int slot, World world, IReadOnlySet<long> interestSet,
        long? ownerNetId = null, bool keyframe = false)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (interestSet is null) throw new ArgumentNullException(nameof(interestSet));
        if (currentSeq == 0) throw new InvalidOperationException("Call BeginTick before BuildRebuildFor.");
        rebuild.RequireBuildable(slot, ownerNetId);
        AoiBaseline capture = CaptureFor(world);
        var projected = new AoiBaseline();
        projector.Fill(projected, capture, interestSet, ownerNetId, useSharedScratch: !wireInUse);
        return rebuild.Build(slot, projected, ownerNetId, keyframe);
    }

    /// <summary>
    /// Commits <paramref name="slot"/>'s unsent candidate after its packet was handed to transport, or after every
    /// keyframe chunk was. A committed keyframe stays pinned until its exact acknowledgement.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> is not the slot's unsent candidate.</exception>
    public void RecordRebuildSent(int slot, ReplicationPacketId id) => rebuild.RecordSent(slot, id);

    /// <summary>
    /// Promotes <paramref name="id"/> to <paramref name="slot"/>'s acknowledged baseline when it is a committed send of
    /// the slot's current epoch, still retained and strictly newer than the current baseline. Stale, duplicate,
    /// future, pruned, unsent, other-slot and retired-epoch ids are ignored.
    /// </summary>
    public void AcknowledgeRebuild(int slot, ReplicationPacketId id) => rebuild.Acknowledge(slot, id);

    /// <summary>
    /// True when <paramref name="slot"/> should start keyframe repair before its next build: the effective no-ack
    /// window from the options passed to <see cref="StartRebuild"/> has been consumed by committed sends. A byte
    /// pressure check (pinned projections plus one more the size of the newest) stays as a defensive guard that the
    /// writer cannot trigger: every retained writer projection is smaller than its complete keyframe, a new candidate
    /// sits beside at most two pins, and <see cref="DeltaRebuildOptions.Validate"/> requires room for four keyframes.
    /// False for a slot without a started stream.
    /// </summary>
    public bool RebuildNeedsRepair(int slot) => rebuild.NeedsRepair(slot);

    // Test seams over the slot's format 2 state.
    internal RebuildUsage RebuildUsageForTest(int slot) => rebuild.Usage(slot);

    internal bool TryGetRetainedProjectionForTest(int slot, ReplicationPacketId id, out ReplicationProjection projection) =>
        rebuild.TryGetRetained(slot, id, out projection);

    internal void SeedRebuildSequenceForTest(int slot, uint sequence) => rebuild.SeedSequence(slot, sequence);
}
