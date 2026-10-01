using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using Comps = KhaozEngine.Replication.CapturedComponents;
using AoiBaseline = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>
/// Per-client, area-of-interest-scoped, <see cref="NetId"/>-keyed baseline+delta encoder: the fusion of
/// <see cref="ServerReplicator"/>'s acked-baseline delta compression with per-client AoI filtering. Each server tick
/// the game calls <see cref="BeginTick"/> once, then <see cref="WriteFor"/> per client with that client's current
/// interest set (the net ids within its AoI). Against the client's last acknowledged baseline it emits: an entity
/// that <b>entered</b> the interest set as a full spawn, one that <b>stayed and changed</b> as only its changed
/// components, one that <b>left</b> (or was despawned) as a removal, and an unchanged in-AoI entity as nothing.
/// The wire is byte-identical to <see cref="ServerReplicator.WriteFor"/> (a full snapshot is the <c>baseline -1</c>
/// delta), so <see cref="ClientReplicationView.ApplyDelta"/> decodes both unchanged.
/// </summary>
/// <remarks>
/// The baseline is keyed by <see cref="NetId"/>, not by any owning cell, so an entity that stays in a client's AoI
/// while changing owning cell (a seamless handoff in the sharding layer) reads as a component delta, never a
/// despawn+respawn. Reliability is phase 1: the delta is built from the client's last <see cref="Acknowledge"/>d
/// baseline, so a dropped delta on a reliable-ordered channel self-heals on the next tick (the server keeps diffing
/// from the acked baseline until a newer ack advances it). Per-client memory is bounded by
/// <c>historyDepth × players</c>: up to <c>historyDepth</c> pending per-seq projections per slot, dropped
/// on <see cref="Acknowledge"/> / <see cref="Forget"/>.
/// <para>Presence follows what was last sent, not what was last acked. An entity removed from a client's delta and
/// back in its interest before that removal is acknowledged is written whole (a full spawn), not diffed against the
/// acked baseline that still holds it. An entity sent whole and gone before that entry is acknowledged is written as
/// a removal, although the acked baseline never held it. A removal repeats until it is acknowledged.</para>
/// </remarks>
public sealed class AoiDeltaReplicator
{
    private readonly ReplicationRegistry registry;
    private readonly int historyDepth;
    private readonly bool hasOwnerScopedCodec;
    private int currentSeq;

    // Per slot: the AoI-scoped state the client has acknowledged (netId -> components) and its seq. This is "what
    // THIS client knows inside its interest set, and at which seq": the per-client, AoI-aware baseline.
    private readonly Dictionary<int, AoiBaseline> ackedBaselineBySlot = new();
    private readonly Dictionary<int, int> ackedSeqBySlot = new();
    // Per slot: projections not yet acked, keyed by the snapshot seq that would promote them to the baseline. Bounded
    // to historyDepth in ascending-seq insertion order.
    private readonly Dictionary<int, Dictionary<int, AoiBaseline>> pendingBySlot = new();
    private readonly Dictionary<int, Queue<int>> pendingOrderBySlot = new();
    // Per slot: the latest seq at which each net id was written as a removal and as a whole entry (see
    // AoiPresenceRecord). The diff runs from the ACKED baseline, but the client holds what was last SENT. An id removed
    // at seq R and back before R is acked still sits in the baseline, so it is written whole while R is newer than
    // the baseline seq. An id sent whole at seq W and gone before W is acked is missing from the baseline, so it is
    // removed when the client still holds it. Pruned on Acknowledge, cleared on Forget, reused per slot.
    private readonly Dictionary<int, AoiPresenceRecord> presenceBySlot = new();

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

    // Test seam: how many world scans the shared capture has actually run (one per distinct world per tick). A tick
    // that serves C clients from one world scans once, not C times - what this whole change buys.
    internal long WorldScanCount { get; private set; }

    // Test seams: how many removal and whole-entry records the slot holds, to prove Acknowledge prunes them and Forget
    // clears them.
    internal int RemovalRecordCount(int slot) =>
        presenceBySlot.TryGetValue(slot, out AoiPresenceRecord? r) ? r.Removed.Count : 0;
    internal int WholeRecordCount(int slot) =>
        presenceBySlot.TryGetValue(slot, out AoiPresenceRecord? r) ? r.Whole.Count : 0;

    public AoiDeltaReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        this.historyDepth = historyDepth;
        wireWriter = new BinaryWriter(wireStream);
        // Whether any registered component is owner-scoped. If none is, every client projects to the exact same
        // Replicate-channel state, so WriteFor can reference the shared capture's component dictionaries directly
        // (they are immutable once captured) instead of building a filtered per-client copy - byte-identical, fewer
        // allocations. Mirrors ServerReplicator's fast path.
        foreach (ComponentCodec codec in registry.Ordered)
            if ((codec.Channels & ReplicationChannels.OwnerOnly) != 0) { hasOwnerScopedCodec = true; break; }
    }

    /// <summary>The latest snapshot sequence (0 before the first <see cref="BeginTick"/>).</summary>
    public int CurrentSeq => currentSeq;

    /// <summary>Opens a new snapshot sequence for this server tick. Call once per tick, before the per-client
    /// <see cref="WriteFor"/> pass. Returns the new seq (the value a client acks after applying this tick's delta).
    /// The shared per-tick capture is invalidated lazily on the first <see cref="WriteFor"/> of the new seq, so this
    /// stays a cheap sequence bump.</summary>
    public int BeginTick() => ++currentSeq;

    /// <summary>
    /// Builds the AoI delta for <paramref name="slot"/> from its acknowledged baseline to the entities of
    /// <paramref name="world"/> whose <see cref="NetId"/> is in <paramref name="interestSet"/>. Full snapshot
    /// (baseline -1) until the client acks. Must be called after <see cref="BeginTick"/>. This is the client-serving
    /// (<see cref="ReplicationChannels.Replicate"/>) path, so only replicated components are captured, and an
    /// <see cref="ReplicationChannels.OwnerOnly"/> component is served only to the entity whose net id equals
    /// <paramref name="ownerNetId"/> (this slot's own player). Because the per-slot baseline stores exactly what was
    /// projected for THIS slot, owner-only visibility falls out of the delta diff automatically.
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
        // on another player's entity is stripped here (never sent), so this slot's baseline holds only what it was
        // actually sent and owner-only visibility falls out of the diff.
        AoiBaseline capture = CaptureFor(world);
        AoiBaseline projected = Project(capture, interestSet, ownerNetId);

        AoiBaseline? baseline = ackedBaselineBySlot.GetValueOrDefault(slot);
        int baselineSeq = baseline is null ? -1 : ackedSeqBySlot[slot];

        wireStream.SetLength(0); // reset the reused wire scratch, keep its capacity
        BinaryWriter bw = wireWriter;
        bw.Write(baselineSeq);
        bw.Write(currentSeq);

        // Removed: gone from the interest set (left AoI or despawned) while the client may hold it, because the
        // baseline holds it or it was sent whole since the baseline. Like every other part of the diff, a removal is
        // repeated each tick until a baseline that lacks the id is acked. Reused scratch list, cleared per call.
        AoiPresenceRecord? presence = presenceBySlot.GetValueOrDefault(slot);
        scratchRemoved.Clear();
        if (baseline is not null)
            foreach (long netId in baseline.Keys)
                if (!projected.ContainsKey(netId)) scratchRemoved.Add(netId);
        if (presence is not null)
            foreach (KeyValuePair<long, int> kv in presence.Whole)
                if (kv.Value > baselineSeq && !projected.ContainsKey(kv.Key)
                    && (baseline is null || !baseline.ContainsKey(kv.Key))) // baseline ids were listed above
                    scratchRemoved.Add(kv.Key);
        // A full snapshot (no baseline) removes implicitly, so it writes no list, but the records still learn it.
        bw.Write(baseline is null ? 0 : scratchRemoved.Count);
        if (baseline is not null)
            foreach (long netId in scratchRemoved) bw.Write(netId);
        if (scratchRemoved.Count > 0)
        {
            presence ??= PresenceFor(slot);
            foreach (long netId in scratchRemoved) presence.Removed[netId] = currentSeq;
        }

        // New (entered, or re-entered after an unacked removal) or changed (stayed + component delta).
        scratchChanged.Clear();
        foreach (long netId in projected.Keys)
        {
            if (IsWholeEntry(baseline, baselineSeq, presence, netId)) { scratchChanged.Add(netId); continue; }
            if (DeltaEncoding.EntityChanged(baseline![netId], projected[netId])) scratchChanged.Add(netId);
        }
        bw.Write(scratchChanged.Count);
        foreach (long netId in scratchChanged)
        {
            bool isNew = IsWholeEntry(baseline, baselineSeq, presence, netId);
            DeltaEncoding.WriteChangedEntity(bw, registry, netId, isNew, isNew ? null : baseline![netId], projected[netId]);
            if (isNew) (presence ??= PresenceFor(slot)).Whole[netId] = currentSeq;
        }

        bw.Flush();
        RecordPending(slot, currentSeq, projected);
        return wireStream.ToArray(); // fresh exact-size array, the caller owns it
    }

    /// <summary>
    /// True when <paramref name="netId"/> must be written as a whole entity: the baseline lacks it, or it was written as
    /// a removal at a seq newer than <paramref name="baselineSeq"/>. In the second case the client may already have
    /// despawned it, so a diff against the baseline would leave it missing or partial.
    /// </summary>
    private static bool IsWholeEntry(AoiBaseline? baseline, int baselineSeq, AoiPresenceRecord? presence, long netId) =>
        baseline is null || !baseline.ContainsKey(netId)
        || AoiPresenceRecord.RemovedSince(presence, baselineSeq, netId);

    /// <summary>The slot's presence record, created on its first use and reused for the seat's lifetime.</summary>
    private AoiPresenceRecord PresenceFor(int slot)
    {
        if (!presenceBySlot.TryGetValue(slot, out AoiPresenceRecord? record))
        {
            record = new AoiPresenceRecord();
            presenceBySlot[slot] = record;
        }
        return record;
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
        // client in Project) are captured. The capture is referenced by per-client baselines for up to historyDepth
        // ticks - each capture's payload buffer is a fresh array, so it is never reused while a baseline still
        // references it.
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

    /// <summary>Records that <paramref name="slot"/> applied up to <paramref name="seq"/>, advancing its AoI baseline
    /// (if still retained). Deltas built afterwards diff from this new baseline.</summary>
    public void Acknowledge(int slot, int seq)
    {
        if (ackedSeqBySlot.TryGetValue(slot, out int cur) && seq <= cur) return; // ignore stale / duplicate ack
        if (!pendingBySlot.TryGetValue(slot, out Dictionary<int, AoiBaseline>? map)
            || !map.TryGetValue(seq, out AoiBaseline? projected)) return;         // seq pruned or never sent
        ackedBaselineBySlot[slot] = projected;
        ackedSeqBySlot[slot] = seq;
        // Everything at or before the acked seq is superseded (reliable-ordered acks advance monotonically).
        Queue<int> order = pendingOrderBySlot[slot];
        while (order.Count > 0 && order.Peek() <= seq) map.Remove(order.Dequeue());
        // A removal or whole entry at or before the acked seq is now in the baseline, so it no longer decides
        // presence.
        if (presenceBySlot.TryGetValue(slot, out AoiPresenceRecord? presence)) presence.Prune(seq);
    }

    /// <summary>Drops all per-client state for <paramref name="slot"/> (call on disconnect / slot recycle).</summary>
    public void Forget(int slot)
    {
        ackedBaselineBySlot.Remove(slot);
        ackedSeqBySlot.Remove(slot);
        pendingBySlot.Remove(slot);
        pendingOrderBySlot.Remove(slot);
        if (presenceBySlot.TryGetValue(slot, out AoiPresenceRecord? presence)) presence.Clear(); // kept for reuse
    }

    private void RecordPending(int slot, int seq, AoiBaseline projected)
    {
        if (!pendingBySlot.TryGetValue(slot, out Dictionary<int, AoiBaseline>? map))
        {
            map = new Dictionary<int, AoiBaseline>();
            pendingBySlot[slot] = map;
            pendingOrderBySlot[slot] = new Queue<int>();
        }
        if (!map.ContainsKey(seq)) pendingOrderBySlot[slot].Enqueue(seq);
        map[seq] = projected;
        Queue<int> order = pendingOrderBySlot[slot];
        while (order.Count > historyDepth) map.Remove(order.Dequeue());
    }
}
