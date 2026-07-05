using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using CapturedState = System.Collections.Generic.Dictionary<long, System.Collections.Generic.Dictionary<ushort, byte[]>>;

namespace KhaozEngine.Replication;

/// <summary>
/// Server-side, whole-world baseline+delta replicator - the client-serving delta path for a world where every
/// client sees every entity (no area-of-interest scoping; use <see cref="AoiDeltaReplicator"/> when it does). Each
/// tick the game calls <see cref="Capture"/> to snapshot the world once; per client it calls <see cref="WriteFor"/>
/// to get a delta from that client's last acknowledged baseline to the latest snapshot (a full snapshot when the
/// client has no baseline). The client acks the snapshot seq it applied via <see cref="Acknowledge"/>, advancing its
/// baseline. Sends only entities/components that changed since the client's baseline.
/// </summary>
/// <remarks>
/// <para>
/// This is a client-serving path, so it honours <see cref="ReplicationChannels"/> exactly as
/// <see cref="SnapshotWriter"/> / <see cref="AoiDeltaReplicator"/> do: <see cref="Capture"/> snapshots only
/// components on the <see cref="ReplicationChannels.Replicate"/> channel (a <see cref="ReplicationChannels.Persist"/>-
/// or <see cref="ReplicationChannels.Migrate"/>-only server component is never captured, so it can never reach a
/// client), and <see cref="WriteFor"/> takes the receiving client's own player net id so an
/// <see cref="ReplicationChannels.OwnerOnly"/> component reaches only its owner. The single shared capture holds
/// every player's owner-only bytes; <see cref="WriteFor"/> projects them per client, so pass a <b>stable</b> owner net
/// id for a given slot across ticks (it is a player's fixed net id for the session) - the acked baseline is
/// re-projected under the owner passed each call. A registry using only <see cref="ReplicationChannels.Default"/>
/// captures every component for every client regardless of owner, so the wire is byte-identical to before channels
/// existed.
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
    private readonly int historyDepth;
    private readonly bool hasOwnerScopedCodec;
    private readonly Dictionary<int, CapturedState> history = new();
    private readonly Dictionary<int, int> baselineSeqBySlot = new();
    private readonly Queue<int> seqOrder = new();
    private int currentSeq;

    public ServerReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        this.historyDepth = historyDepth;
        // Whether any registered component is owner-scoped. If none is, the shared capture is already the same for
        // every client, so WriteFor can skip per-client projection entirely and stay allocation-free + byte-identical.
        foreach (ComponentCodec codec in registry.Ordered)
            if ((codec.Channels & ReplicationChannels.OwnerOnly) != 0) { hasOwnerScopedCodec = true; break; }
    }

    /// <summary>The latest captured snapshot sequence (0 before the first <see cref="Capture"/>).</summary>
    public int CurrentSeq => currentSeq;

    /// <summary>
    /// Snapshots the world's <see cref="NetId"/> entities and their <see cref="ReplicationChannels.Replicate"/>-channel
    /// components into a new seq. Persist-/Migrate-only server components are NOT captured (they never go on a client
    /// wire); owner-only components ARE captured (keyed under their entity) and scoped per client in
    /// <see cref="WriteFor"/>.
    /// </summary>
    public int Capture(World world)
    {
        var state = new CapturedState();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            var comps = new Dictionary<ushort, byte[]>();
            foreach (ComponentCodec codec in registry.Ordered)
            {
                // Client-serving path: only Replicate-channel components ever go on the wire. Owner-only components
                // are captured here too (they carry Replicate) and stripped per non-owning client in WriteFor.
                if ((codec.Channels & ReplicationChannels.Replicate) == 0) continue;
                byte[]? data = codec.CaptureData(world, e);
                if (data is not null) comps[codec.TypeId] = data;
            }
            state[id.Value] = comps;
        });

        currentSeq++;
        history[currentSeq] = state;
        seqOrder.Enqueue(currentSeq);
        while (seqOrder.Count > historyDepth)
            history.Remove(seqOrder.Dequeue());
        return currentSeq;
    }

    /// <summary>Records that <paramref name="slot"/> applied up to <paramref name="seq"/>, advancing its baseline (if still retained).</summary>
    public void Acknowledge(int slot, int seq)
    {
        if (history.ContainsKey(seq)) baselineSeqBySlot[slot] = seq;
    }

    /// <summary>
    /// Builds the delta for <paramref name="slot"/> from its baseline to the latest snapshot. On the
    /// <see cref="ReplicationChannels.Replicate"/> channel an <see cref="ReplicationChannels.OwnerOnly"/> component is
    /// included only for the entity whose net id equals <paramref name="ownerNetId"/> (this slot's own player), and
    /// for nobody when it is null (an unowned serve). Pass a stable owner net id for a given slot across ticks: the
    /// acked baseline is re-projected under the owner supplied here, so the diff stays consistent with what the client
    /// actually holds. A registry with no owner-only component ignores <paramref name="ownerNetId"/> (the wire is then
    /// byte-identical to before channels existed).
    /// </summary>
    public byte[] WriteFor(int slot, long? ownerNetId = null)
    {
        if (currentSeq == 0) throw new InvalidOperationException("Capture at least once before WriteFor.");

        int baselineSeq = baselineSeqBySlot.GetValueOrDefault(slot, -1);
        CapturedState? rawBaseline = baselineSeq >= 0 && history.TryGetValue(baselineSeq, out CapturedState? b) ? b : null;
        if (rawBaseline is null) baselineSeq = -1; // baseline pruned or none -> full snapshot
        // Owner-scope both ends identically so OwnerOnly bytes never leak and the diff reflects only what THIS client
        // can see (a no-op returning the shared state when the registry has no owner-only component).
        CapturedState current = Project(history[currentSeq], ownerNetId);
        CapturedState? baseline = rawBaseline is null ? null : Project(rawBaseline, ownerNetId);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(baselineSeq);
        bw.Write(currentSeq);

        // Removed entities: present in the baseline, gone from current.
        var removed = new List<long>();
        if (baseline is not null)
            foreach (long netId in baseline.Keys)
                if (!current.ContainsKey(netId)) removed.Add(netId);
        bw.Write(removed.Count);
        foreach (long netId in removed) bw.Write(netId);

        // New or changed entities.
        var changed = new List<long>();
        foreach (long netId in current.Keys)
        {
            if (baseline is null || !baseline.ContainsKey(netId)) { changed.Add(netId); continue; }
            if (DeltaEncoding.EntityChanged(baseline[netId], current[netId])) changed.Add(netId);
        }
        bw.Write(changed.Count);
        foreach (long netId in changed)
        {
            bool isNew = baseline is null || !baseline.ContainsKey(netId);
            DeltaEncoding.WriteChangedEntity(bw, registry, netId, isNew, isNew ? null : baseline![netId], current[netId]);
        }

        bw.Flush();
        return ms.ToArray();
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
        foreach (KeyValuePair<long, Dictionary<ushort, byte[]>> entity in raw)
        {
            long netId = entity.Key;
            var comps = new Dictionary<ushort, byte[]>(entity.Value.Count);
            foreach (KeyValuePair<ushort, byte[]> comp in entity.Value)
                if (registry.TryGet(comp.Key, out ComponentCodec codec)
                    && codec.ShouldWrite(ReplicationChannels.Replicate, netId, ownerNetId))
                    comps[comp.Key] = comp.Value;
            projected[netId] = comps;
        }
        return projected;
    }
}
