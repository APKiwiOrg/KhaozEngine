using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellSimRetentionTests
{
    private struct ExtA : IComponent { public int V; }
    private struct ExtB : IComponent { public int V; }

    private const ushort ExtAId = 16;
    private const ushort ExtBId = 17;

    private static ReplicationRegistry Full()
    {
        var r = new ReplicationRegistry();
        r.Register<ExtA>(ExtAId, (c, bw) => bw.Write(c.V), br => new ExtA { V = br.ReadInt32() });
        r.Register<ExtB>(ExtBId, (c, bw) => bw.Write(c.V), br => new ExtB { V = br.ReadInt32() });
        return r;
    }

    // Missing ExtB: a build that dropped one extension registration (a registry downgrade).
    private static ReplicationRegistry Reduced()
    {
        var r = new ReplicationRegistry();
        r.Register<ExtA>(ExtAId, (c, bw) => bw.Write(c.V), br => new ExtA { V = br.ReadInt32() });
        return r;
    }

    private static CellSim Cell(ReplicationRegistry r) => new(new CellCoord(0, 0), 1f / 30f, r, 10f);

    [Fact]
    public void UnknownExtension_SurvivesReducedRegistryRoundTrip_ReappearsUnderFullRegistry()
    {
        // Full registry cell: entity 5 carries ExtA=10 and ExtB=20.
        ReplicationRegistry full = Full();
        CellSim src = Cell(full);
        Entity e = src.World.Spawn();
        src.World.Set(e, new NetId(5));
        src.World.Set(e, new ExtA { V = 10 });
        src.World.Set(e, new ExtB { V = 20 });
        byte[] blob = src.SnapshotOwned(new HashSet<long>());

        // Reduced registry restores: ExtA applies, ExtB is unknown and retained.
        CellSim mid = Cell(Reduced());
        CellRestoreResult r = mid.TryRestoreOwned(blob);
        Assert.True(r.Ok);
        Assert.Equal(new long[] { 5 }, r.NetIds);
        Assert.Equal(1, r.RetainedFrameCount);
        Assert.True(mid.TryGetOwned(5, out Entity me));
        Assert.True(mid.World.TryGet(me, out ExtA a));
        Assert.Equal(10, a.V);

        // Reduced cell re-emits: the retained ExtB frame rides along verbatim.
        byte[] reemit = mid.SnapshotOwned(new HashSet<long>());

        // Full registry restores the re-emitted blob: ExtB is back, intact.
        CellSim dst = Cell(full);
        dst.TryRestoreOwned(reemit);
        Assert.True(dst.TryGetOwned(5, out Entity de));
        Assert.True(dst.World.TryGet(de, out ExtA a2));
        Assert.Equal(10, a2.V);
        Assert.True(dst.World.TryGet(de, out ExtB b2));
        Assert.Equal(20, b2.V);
    }

    [Fact]
    public void CorruptSnapshot_TryRestoreFailsAndRollsBack_NeverThrows()
    {
        CellSim cell = Cell(Reduced());       // knows ExtA (16) only
        byte[] corrupt = BuildCorruptTwoEntity();

        CellRestoreResult res = cell.TryRestoreOwned(corrupt);

        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Empty(res.NetIds);
        Assert.Equal(0, cell.MaxOwnedNetId());     // the partial apply was rolled back: the cell is empty
        Assert.False(cell.TryGetOwned(5, out _));
    }

    [Fact]
    public void RestoreOwned_LegacyOverload_IsNonThrowing_ReturnsEmptyOnCorrupt()
    {
        CellSim cell = Cell(Reduced());
        IReadOnlyList<long> ids = cell.RestoreOwned(BuildCorruptTwoEntity());
        Assert.Empty(ids);
        Assert.Equal(0, cell.MaxOwnedNetId());
    }

    // A snapshot whose first entity is valid but whose second references an unknown BUILT-IN id (throw-on-unknown),
    // so a naive apply would spawn entity 5 then throw on entity 6.
    private static byte[] BuildCorruptTwoEntity()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(2);             // entity count
        bw.Write(5L);            // netId (64-bit)
        bw.Write((ushort)ExtAId);
        bw.Write7BitEncodedInt(4);
        bw.Write(10);
        bw.Write((ushort)0);
        bw.Write(6L);            // netId (64-bit)
        bw.Write((ushort)2);     // built-in id 2: unregistered -> hard mismatch, throws
        bw.Write(123);
        bw.Write((ushort)0);
        bw.Flush();
        return ms.ToArray();
    }
}
