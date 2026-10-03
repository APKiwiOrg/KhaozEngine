using System;
using System.Collections.Generic;
using System.IO;
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
/// and serve a fresh receiver world and view. Per-client memory is one last sent projection per slot.</para>
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
    private readonly bool hasOwnerScopedCodec;
    private int currentSeq;

    // Per slot: the sequence and exact AoI-scoped, owner-scoped projection of the last payload returned for it, which
    // is what a reliable-ordered client holds. The next payload diffs from it.
    private readonly Dictionary<int, LegacyDeltaSlot> slots = new();

    // Shared per-tick capture: the whole-world Replicate-channel snapshot (netId -> components), captured ONCE per
    // distinct world per seq and projected per client in WriteFor. Keyed by World because the sharded server serves
    // different clients from different home-cell worlds within one tick. The non-sharded server passes one world, so
    // this collapses to a single capture per tick. captureSeq marks which seq the cache belongs to (cleared lazily on
    // the first WriteFor of a new seq, so BeginTick stays a cheap seq bump).
    private readonly Dictionary<World, AoiBaseline> captureByWorld = new();
    private int captureSeq;

    // Single-threaded per instance (one server tick thread): the capture scratch consolidates each world capture into
    // one buffer, and the wire scratch (stream + reused changed/removed lists) builds each client's delta without a
    // per-call allocation beyond the returned wire array and the retained projection. No locking.
    private readonly CaptureScratch captureScratch = new();
    private readonly MemoryStream wireStream = new();
    private readonly BinaryWriter wireWriter;
    private readonly List<long> scratchRemoved = new();
    private readonly List<long> scratchChanged = new();
    // Project's reused ordering scratch: the interest-set entities present in the capture, tagged with their capture
    // order, sorted so the projection is built in world ForEach order (byte-identical to the old full-capture walk).
    // Reused across every WriteFor, so a steady-state projection allocates only the exact-size projected baseline.
    private readonly List<(int order, long netId, Comps comps)> projectScratch = new();
    private static readonly Comparison<(int order, long netId, Comps comps)> ByCaptureOrder =
        static (a, b) => a.order.CompareTo(b.order);
    private readonly RebuildDeltaWriter rebuild;

    // Test seam: how many world scans the shared capture has actually run (one per distinct world per tick). A tick
    // that serves C clients from one world scans once, not C times - what this whole change buys.
    internal long WorldScanCount { get; private set; }

    // Test seams: slots holding last sent state, and the shared captures retained for the current seq (one per
    // distinct world served this seq, until the next seq's first WriteFor drops them).
    internal int LegacySlotCount => slots.Count;

    internal int LegacyHistoryCount => captureByWorld.Count;

    /// <summary>Creates an area-of-interest legacy delta writer.</summary>
    /// <param name="registry">The replicated component registry.</param>
    /// <param name="historyDepth">Kept for source compatibility and still validated as positive. The legacy diff
    /// retains one last sent projection per slot, so it no longer bounds a pending history.</param>
    public AoiDeltaReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        wireWriter = new BinaryWriter(wireStream);
        rebuild = new RebuildDeltaWriter(registry);
        // Whether any registered component is owner-scoped. If none is, every client projects to the exact same
        // Replicate-channel state, so WriteFor can reference the shared capture's component dictionaries directly
        // (they are immutable once captured) instead of building a filtered per-client copy - byte-identical, fewer
        // allocations. Mirrors ServerReplicator's fast path.
        foreach (ComponentCodec codec in registry.Ordered)
            if ((codec.Channels & ReplicationChannels.OwnerOnly) != 0) { hasOwnerScopedCodec = true; break; }
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
    /// after a <see cref="BeginTick"/> that sees it), shared across every client served from that world. On top of
    /// that shared capture, <see cref="Project"/> is O(interestSet): it iterates this client's interest set and
    /// resolves each entity's captured components in O(1), ordering the selection by capture position so the
    /// changed-entity wire order is identical to walking the whole capture. So the per-client cost scales with the
    /// client's area of interest, not the world population. The wire is byte-identical to capturing per client.
    /// </remarks>
    public byte[] WriteFor(int slot, World world, IReadOnlySet<long> interestSet, long? ownerNetId = null)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (interestSet is null) throw new ArgumentNullException(nameof(interestSet));
        if (currentSeq == 0) throw new InvalidOperationException("Call BeginTick before WriteFor.");

        // Project the shared whole-world capture down to what THIS client is entitled to: the in-AoI entities, with
        // each entity's components filtered to the Replicate channel owner-scoped to this slot. An OwnerOnly component
        // on another player's entity is stripped here (never sent), so this slot's state holds only what it was
        // actually sent and owner-only visibility falls out of the diff.
        AoiBaseline capture = CaptureFor(world);
        AoiBaseline projected = Project(capture, interestSet, ownerNetId);

        LegacyDeltaSlot? previous = slots.GetValueOrDefault(slot);
        AoiBaseline? baseline = previous?.Projection;
        int baselineSeq = previous?.Sequence ?? -1;

        wireStream.SetLength(0); // reset the reused wire scratch, keep its capacity
        BinaryWriter bw = wireWriter;
        bw.Write(baselineSeq);
        bw.Write(currentSeq);

        // Removed: in the last sent projection, gone from the interest set (left AoI or despawned). A full snapshot
        // (no baseline) removes implicitly, so it lists nothing. Reused scratch list, cleared per call.
        scratchRemoved.Clear();
        if (baseline is not null)
            foreach (long netId in baseline.Keys)
                if (!projected.ContainsKey(netId)) scratchRemoved.Add(netId);
        bw.Write(scratchRemoved.Count);
        foreach (long netId in scratchRemoved) bw.Write(netId);

        // New (absent from the last sent projection, so written whole) or changed (stayed + component delta).
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
    /// The shared whole-world capture for <paramref name="world"/> at the current seq: every <see cref="NetId"/>
    /// entity's <see cref="ReplicationChannels.Replicate"/>-channel components (owner-only ones included, keyed under
    /// their entity, to be scoped per client in <see cref="Project"/>). Captured once per world per tick and reused by
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

        // One shared buffer per world capture, only Replicate-channel components (owner-only included, scoped per
        // client in Project) are captured. A slot's last sent projection references it until that slot is served
        // again or forgotten - each capture's payload buffer is a fresh array, so it is never reused while a slot
        // still references it.
        AoiBaseline state = captureScratch.CaptureReplicate(world, registry);
        captureByWorld[world] = state;
        WorldScanCount++;
        return state;
    }

    /// <summary>
    /// Projects the shared <paramref name="capture"/> down to one client: keeps only entities in
    /// <paramref name="interestSet"/> and, per entity, only the components this slot may see (an
    /// <see cref="ReplicationChannels.OwnerOnly"/> component only when its net id equals <paramref name="ownerNetId"/>).
    /// O(interestSet): it iterates the (usually far smaller) interest set and resolves each entity's captured
    /// components in O(1), instead of walking the whole shared capture and filtering. The selected entities are then
    /// ordered by their capture position (<see cref="CapturedComponents.Order"/>, the world <c>ForEach</c> order) so
    /// the resulting baseline - and thus the changed-entity order on the wire - matches a full capture walk exactly
    /// (byte-identical to the pre-index projection). When no registered component is owner-scoped, each in-AoI
    /// entity's captured component set is referenced directly (it is immutable and identical for every client),
    /// avoiding a per-client copy. When one is, the owner still gets the captured set itself and every other viewer
    /// gets the entity's one shared public view (<see cref="CaptureProjection.OwnerScope"/>), so owner scoping costs
    /// at most one filtered copy per entity per tick, not one per entity per client.
    /// </summary>
    private AoiBaseline Project(AoiBaseline capture, IReadOnlySet<long> interestSet, long? ownerNetId)
    {
        // Collect the in-AoI entities present in the capture, tagged with their capture order, then sort by that order
        // to reproduce the world ForEach order the old full-capture walk emitted. Capture orders are unique per entity,
        // so the sort has no ties and is deterministic regardless of interest-set enumeration order.
        projectScratch.Clear();
        foreach (long netId in interestSet)
            if (capture.TryGetValue(netId, out Comps? comps))
                projectScratch.Add((comps.Order, netId, comps));
        projectScratch.Sort(ByCaptureOrder);

        var projected = new AoiBaseline(projectScratch.Count); // exact-size: no growth reallocations
        foreach ((int _, long netId, Comps comps) in projectScratch)
            projected[netId] = hasOwnerScopedCodec
                ? CaptureProjection.OwnerScope(comps, registry, netId, ownerNetId)
                : comps; // shared, immutable: no owner scoping to apply, so no copy needed
        return projected;
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
        return rebuild.Build(slot, Project(CaptureFor(world), interestSet, ownerNetId), ownerNetId, keyframe);
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
