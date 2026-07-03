using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using CapturedState = System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<ushort, byte[]>>;

namespace KhaozEngine.Replication;

/// <summary>
/// Server-side baseline+delta replicator. Each tick the game calls <see cref="Capture"/> to snapshot the
/// world; per client it calls <see cref="WriteFor"/> to get a delta from that client's last acknowledged
/// baseline to the latest snapshot (a full snapshot when the client has no baseline). The client acks the
/// snapshot seq it applied via <see cref="Acknowledge"/>, advancing its baseline. Sends only entities/components
/// that changed since the client's baseline.
/// </summary>
/// <remarks>
/// Wire (per delta): <c>[baselineSeq][snapshotSeq][removedCount][removedNetId...][changedCount]</c> then per
/// changed/new entity <c>[netId][isNew][removedCompCount][removedTypeId...][(typeId,[len],data)...][0]</c>, the
/// 7-bit <c>len</c> present only for consumer extension components (see
/// <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) so an older client can skip an unknown id. New
/// entities carry all their components; existing ones carry only changed/added components. Snapshots are opaque
/// <c>byte[]</c> the game ships over its session transport (the matching ack flows back the same way).
/// </remarks>
public sealed class ServerReplicator
{
    private readonly ReplicationRegistry registry;
    private readonly int historyDepth;
    private readonly Dictionary<int, CapturedState> history = new();
    private readonly Dictionary<int, int> baselineSeqBySlot = new();
    private readonly Queue<int> seqOrder = new();
    private int currentSeq;

    public ServerReplicator(ReplicationRegistry registry, int historyDepth = 32)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (historyDepth <= 0) throw new ArgumentOutOfRangeException(nameof(historyDepth), historyDepth, "must be positive");
        this.historyDepth = historyDepth;
    }

    /// <summary>The latest captured snapshot sequence (0 before the first <see cref="Capture"/>).</summary>
    public int CurrentSeq => currentSeq;

    /// <summary>Snapshots the world's <see cref="NetId"/> entities + registered components into a new seq.</summary>
    public int Capture(World world)
    {
        var state = new CapturedState();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            var comps = new Dictionary<ushort, byte[]>();
            foreach (ComponentCodec codec in registry.Ordered)
            {
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

    /// <summary>Builds the delta for <paramref name="slot"/> from its baseline to the latest snapshot.</summary>
    public byte[] WriteFor(int slot)
    {
        if (currentSeq == 0) throw new InvalidOperationException("Capture at least once before WriteFor.");

        int baselineSeq = baselineSeqBySlot.GetValueOrDefault(slot, -1);
        CapturedState? baseline = baselineSeq >= 0 && history.TryGetValue(baselineSeq, out CapturedState? b) ? b : null;
        if (baseline is null) baselineSeq = -1; // baseline pruned or none -> full snapshot
        CapturedState current = history[currentSeq];

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(baselineSeq);
        bw.Write(currentSeq);

        // Removed entities: present in the baseline, gone from current.
        var removed = new List<int>();
        if (baseline is not null)
            foreach (int netId in baseline.Keys)
                if (!current.ContainsKey(netId)) removed.Add(netId);
        bw.Write(removed.Count);
        foreach (int netId in removed) bw.Write(netId);

        // New or changed entities.
        var changed = new List<int>();
        foreach (int netId in current.Keys)
        {
            if (baseline is null || !baseline.ContainsKey(netId)) { changed.Add(netId); continue; }
            if (DeltaEncoding.EntityChanged(baseline[netId], current[netId])) changed.Add(netId);
        }
        bw.Write(changed.Count);
        foreach (int netId in changed)
        {
            bool isNew = baseline is null || !baseline.ContainsKey(netId);
            DeltaEncoding.WriteChangedEntity(bw, registry, netId, isNew, isNew ? null : baseline![netId], current[netId]);
        }

        bw.Flush();
        return ms.ToArray();
    }
}
