using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using Comps = System.Collections.Generic.Dictionary<ushort, byte[]>;
using AoiBaseline = System.Collections.Generic.Dictionary<long, System.Collections.Generic.Dictionary<ushort, byte[]>>;

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
/// </remarks>
public sealed class AoiDeltaReplicator
{
    private readonly ReplicationRegistry registry;
    private readonly int historyDepth;
    private int currentSeq;

    // Per slot: the AoI-scoped state the client has acknowledged (netId -> components) and its seq. This is "what
    // THIS client knows inside its interest set, and at which seq": the per-client, AoI-aware baseline.
    private readonly Dictionary<int, AoiBaseline> ackedBaselineBySlot = new();
    private readonly Dictionary<int, int> ackedSeqBySlot = new();
    // Per slot: projections not yet acked, keyed by the snapshot seq that would promote them to the baseline. Bounded
    // to historyDepth in ascending-seq insertion order.
    private readonly Dictionary<int, Dictionary<int, AoiBaseline>> pendingBySlot = new();
    private readonly Dictionary<int, Queue<int>> pendingOrderBySlot = new();

    public AoiDeltaReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        this.historyDepth = historyDepth;
    }

    /// <summary>The latest snapshot sequence (0 before the first <see cref="BeginTick"/>).</summary>
    public int CurrentSeq => currentSeq;

    /// <summary>Opens a new snapshot sequence for this server tick. Call once per tick, before the per-client
    /// <see cref="WriteFor"/> pass. Returns the new seq (the value a client acks after applying this tick's delta).</summary>
    public int BeginTick() => ++currentSeq;

    /// <summary>
    /// Builds the AoI delta for <paramref name="slot"/> from its acknowledged baseline to the entities of
    /// <paramref name="world"/> whose <see cref="NetId"/> is in <paramref name="interestSet"/>. Full snapshot
    /// (baseline -1) until the client acks. Must be called after <see cref="BeginTick"/>. This is the client-serving
    /// (<see cref="ReplicationChannels.Replicate"/>) path, so only replicated components are captured, and an
    /// <see cref="ReplicationChannels.OwnerOnly"/> component is captured only for the entity whose net id equals
    /// <paramref name="ownerNetId"/> (this slot's own player). Because the per-slot baseline stores exactly what was
    /// captured for THIS slot, owner-only visibility falls out of the delta diff automatically.
    /// </summary>
    public byte[] WriteFor(int slot, World world, IReadOnlySet<long> interestSet, long? ownerNetId = null)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (interestSet is null) throw new ArgumentNullException(nameof(interestSet));
        if (currentSeq == 0) throw new InvalidOperationException("Call BeginTick before WriteFor.");

        // Capture the in-AoI entities that exist in this world (netId -> components), reusing the codec capture.
        // Filter to the Replicate channel (owner-scoped), so this slot's baseline holds only what it was actually
        // sent - an OwnerOnly component on another player's entity is never captured, so it can never leak here.
        var projected = new AoiBaseline();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (!interestSet.Contains(id.Value)) return;
            var comps = new Comps();
            foreach (ComponentCodec codec in registry.Ordered)
            {
                if (!codec.ShouldWrite(ReplicationChannels.Replicate, id.Value, ownerNetId)) continue;
                byte[]? data = codec.CaptureData(world, e);
                if (data is not null) comps[codec.TypeId] = data;
            }
            projected[id.Value] = comps;
        });

        AoiBaseline? baseline = ackedBaselineBySlot.GetValueOrDefault(slot);
        int baselineSeq = baseline is null ? -1 : ackedSeqBySlot[slot];

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(baselineSeq);
        bw.Write(currentSeq);

        // Removed: the client knew it (baseline) but it is gone from the interest set (left AoI or despawned).
        var removed = new List<long>();
        if (baseline is not null)
            foreach (long netId in baseline.Keys)
                if (!projected.ContainsKey(netId)) removed.Add(netId);
        bw.Write(removed.Count);
        foreach (long netId in removed) bw.Write(netId);

        // New (entered) or changed (stayed + component delta).
        var changed = new List<long>();
        foreach (long netId in projected.Keys)
        {
            if (baseline is null || !baseline.ContainsKey(netId)) { changed.Add(netId); continue; }
            if (DeltaEncoding.EntityChanged(baseline[netId], projected[netId])) changed.Add(netId);
        }
        bw.Write(changed.Count);
        foreach (long netId in changed)
        {
            bool isNew = baseline is null || !baseline.ContainsKey(netId);
            DeltaEncoding.WriteChangedEntity(bw, registry, netId, isNew, isNew ? null : baseline![netId], projected[netId]);
        }

        bw.Flush();
        RecordPending(slot, currentSeq, projected);
        return ms.ToArray();
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
    }

    /// <summary>Drops all per-client state for <paramref name="slot"/> (call on disconnect / slot recycle).</summary>
    public void Forget(int slot)
    {
        ackedBaselineBySlot.Remove(slot);
        ackedSeqBySlot.Remove(slot);
        pendingBySlot.Remove(slot);
        pendingOrderBySlot.Remove(slot);
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
