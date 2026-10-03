using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Legacy reader compatibility on <see cref="ClientReplicationView.ApplyDelta"/>: a delta whose baseline is at or
/// before the client's last applied seq is accepted and overlaid, only a baseline AHEAD of it is a gap that throws,
/// and a <c>baseline -1</c> delta is a full snapshot (despawns tracked entities absent from it). The legacy writers now
/// name their last sent projection, so an older baseline reaches this reader only from a hand-built or older-writer
/// delta. Accepting one is reader compatibility, not proof that overlaying it is safe after loss.
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

    private static HashSet<long> Aoi(params long[] ids) => new(ids);

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

    // [baselineSeq][snapshotSeq][removedCount=0][changedCount=1] then one existing entity carrying only Pos.
    private static byte[] PosDelta(int baselineSeq, int snapshotSeq, long netId, float x, float y)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(baselineSeq);
        bw.Write(snapshotSeq);
        bw.Write(0);              // removed entities
        bw.Write(1);              // changed entities
        bw.Write(netId);
        bw.Write((byte)0);        // isNew
        bw.Write(0);              // removed components
        bw.Write((ushort)1);      // Pos, a built-in, so unframed
        bw.Write(x);
        bw.Write(y);
        bw.Write((ushort)0);      // end of entity
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Hand_built_delta_from_an_older_baseline_is_accepted_and_overlaid()
    {
        var registry = NewRegistry();
        var world = new World();
        Entity e1 = Spawn(world, 1, 1, 1);
        Spawn(world, 2, 2, 2);

        var repl = new AoiDeltaReplicator(registry);
        var client = new World();
        var view = new ClientReplicationView(registry);

        int seq1 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        world.Set(e1, new Pos { X = 5, Y = 5 });
        int seq2 = repl.BeginTick();
        view.ApplyDelta(client, repl.WriteFor(0, world, Aoi(1, 2)));
        Assert.Equal(seq2, view.LastAppliedSeq);

        // A legacy delta naming seq1, older than the applied seq2, is not rejected. Its listed changes overlay the
        // current state, and entities it does not mention keep their applied values.
        byte[] older = PosDelta(baselineSeq: seq1, snapshotSeq: seq2 + 1, netId: 1, x: 9, y: 9);
        Assert.True(view.TryApplyDelta(client, older, out string? error));
        Assert.Null(error);
        Assert.Equal(seq2 + 1, view.LastAppliedSeq);
        Assert.Equal(9f, client.Get<Pos>(view.Entities[1]).X);
        Assert.Equal(2f, client.Get<Pos>(view.Entities[2]).X);
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
        Assert.Equal(-1, LegacyDeltaWire.ReadHeader(onlyOne).Baseline);

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

        // A delta whose baseline is ahead of anything the client applied is a genuine gap and throws.
        byte[] ahead = EmptyDelta(baselineSeq: 5, snapshotSeq: 6);
        Assert.Throws<InvalidOperationException>(() => view.ApplyDelta(client, ahead));
    }
}
