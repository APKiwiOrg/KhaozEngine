using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The acked-baseline self-heal contract on <see cref="ClientReplicationView.ApplyDelta"/>: a delta built from a
/// baseline at or before the client's last applied seq is a valid rebuild (idempotent), only a baseline AHEAD of it
/// is a gap that throws, and a <c>baseline -1</c> delta is a full snapshot (despawns tracked entities absent from it).
/// </summary>
public class ClientReplicationViewHealTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static Entity Spawn(World w, int netId, float x, float y)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    private static HashSet<int> Aoi(params int[] ids) => new(ids);

    // A header-only delta [baselineSeq][snapshotSeq][removedCount=0][changedCount=0].
    private static byte[] EmptyDelta(int baselineSeq, int snapshotSeq)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(baselineSeq);
        bw.Write(snapshotSeq);
        bw.Write(0);
        bw.Write(0);
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Delta_from_an_older_baseline_self_heals_and_converges()
    {
        // The full loss-tolerance path: an ack is dropped, so the next delta is built from an OLDER acked baseline
        // than what the client already applied. It must apply (not throw) and converge to the latest state.
        var registry = NewRegistry();
        var world = new World();
        Entity e1 = Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        repl.Acknowledge(0, seq1);

        // seq2 applied by the client, but its ACK is lost (never Acknowledged on the server).
        world.Set(e1, new Pos { X = 5, Y = 5 });
        repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));

        // seq3 is therefore built from the seq1 baseline (< the client's last applied seq2). Must self-heal.
        world.Set(e1, new Pos { X = 9, Y = 9 });
        repl.BeginTick();
        byte[] d3 = repl.WriteFor(0, world, Aoi(1, 2));

        view.TryApplyDelta(client, d3, out string? error);
        Assert.Null(error);                                          // no throw
        Assert.Equal(9f, client.Get<Pos>(view.Entities[1]).X);      // converged to latest
        Assert.Equal(2f, client.Get<Pos>(view.Entities[2]).X);      // untouched
    }

    [Fact]
    public void Baseline_minus_one_delta_despawns_absent_tracked_entities()
    {
        // A baseline -1 delta is a full snapshot: anything the client still tracks but the delta omits is gone.
        var registry = NewRegistry();
        var world = new World();
        Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));  // base -1: client learns 1 and 2
        Assert.True(view.TryGetEntity(2, out _));

        // A fresh slot (no baseline) with only entity 1 in interest -> another base -1 delta omitting entity 2.
        repl.BeginTick();
        byte[] onlyOne = repl.WriteFor(99, world, Aoi(1));
        (int baseSeq, _, _, _) = ReadHeader(onlyOne);
        Assert.Equal(-1, baseSeq);

        view.ApplyDelta(client, onlyOne);
        Assert.True(view.TryGetEntity(1, out _));
        Assert.False(view.TryGetEntity(2, out _));                   // full-state semantics despawn it
    }

    [Fact]
    public void Delta_from_a_future_baseline_throws()
    {
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);   // LastAppliedSeq = -1

        // A delta whose baseline is ahead of anything the client applied is a genuine gap, not a self-heal.
        byte[] ahead = EmptyDelta(baselineSeq: 5, snapshotSeq: 6);
        Assert.Throws<InvalidOperationException>(() => view.ApplyDelta(client, ahead));
    }

    private static (int baseSeq, int snapSeq, int removed, int changed) ReadHeader(byte[] d)
    {
        using var br = new BinaryReader(new MemoryStream(d));
        int b = br.ReadInt32();
        int s = br.ReadInt32();
        int removed = br.ReadInt32();
        for (int i = 0; i < removed; i++) br.ReadInt32();
        int changed = br.ReadInt32();
        return (b, s, removed, changed);
    }
}
