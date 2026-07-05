using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class ClientReplicationViewRetentionTests
{
    private struct ExtA : IComponent { public int V; }
    private struct ExtB : IComponent { public int V; }
    private struct Builtin : IComponent { public int V; }

    private const ushort ExtAId = 16;
    private const ushort ExtBId = 17;
    private const ushort BuiltinId = 1;

    private static ReplicationRegistry Full()
    {
        var r = new ReplicationRegistry();
        r.Register<ExtA>(ExtAId, (c, bw) => bw.Write(c.V), br => new ExtA { V = br.ReadInt32() });
        r.Register<ExtB>(ExtBId, (c, bw) => bw.Write(c.V), br => new ExtB { V = br.ReadInt32() });
        return r;
    }

    // Knows ExtA but NOT ExtB - a consumer build missing one extension registration (a registry downgrade).
    private static ReplicationRegistry Reduced()
    {
        var r = new ReplicationRegistry();
        r.Register<ExtA>(ExtAId, (c, bw) => bw.Write(c.V), br => new ExtA { V = br.ReadInt32() });
        return r;
    }

    [Fact]
    public void TryApplyRetainingUnknown_AppliesKnown_RetainsUnknownExtension()
    {
        var src = new World();
        Entity e = src.Spawn();
        src.Set(e, new NetId(5));
        src.Set(e, new ExtA { V = 10 });
        src.Set(e, new ExtB { V = 20 });
        byte[] snap = SnapshotWriter.Write(src, Full());

        var dst = new World();
        var view = new ClientReplicationView(Reduced());
        bool ok = view.TryApplyRetainingUnknown(dst, snap, out IReadOnlyList<RetainedComponent> retained, out string? err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.True(view.TryGetEntity(5, out Entity de));
        Assert.True(dst.TryGet(de, out ExtA a));
        Assert.Equal(10, a.V);                        // known component applied
        Assert.Single(retained);                      // unknown extension retained
        Assert.Equal(5, retained[0].NetId);
        Assert.Equal(ExtBId, retained[0].TypeId);
        Assert.Equal(BitConverter.GetBytes(20), retained[0].Payload);
    }

    [Fact]
    public void TryApplyRetainingUnknown_UnknownBuiltin_FailsNonThrowing()
    {
        var full = new ReplicationRegistry();
        full.Register<Builtin>(BuiltinId, (c, bw) => bw.Write(c.V), br => new Builtin { V = br.ReadInt32() });
        var src = new World();
        Entity e = src.Spawn();
        src.Set(e, new NetId(5));
        src.Set(e, new Builtin { V = 99 });
        byte[] snap = SnapshotWriter.Write(src, full);

        var dst = new World();
        var view = new ClientReplicationView(Reduced());   // does not know built-in id 1
        bool ok = view.TryApplyRetainingUnknown(dst, snap, out IReadOnlyList<RetainedComponent> retained, out string? err);

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Empty(retained);
    }

    [Fact]
    public void WriteFiltered_ReEmitsRetainedExtensionFrames()
    {
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(5));
        world.Set(e, new ExtA { V = 10 });
        var retained = new RetainedComponent(5, ExtBId, BitConverter.GetBytes(20));

        byte[] snap = SnapshotWriter.WriteFiltered(world, Reduced(), new HashSet<int> { 5 },
            ReplicationChannels.Persist, ownerNetId: null,
            retainedExtensionFrames: id => id == 5 ? new[] { retained } : null);

        // Read back under the FULL registry: the retained ExtB reappears intact beside the re-encoded ExtA.
        var dst = new World();
        var view = new ClientReplicationView(Full());
        view.Apply(dst, snap);
        Assert.True(view.TryGetEntity(5, out Entity de));
        Assert.True(dst.TryGet(de, out ExtA a));
        Assert.Equal(10, a.V);
        Assert.True(dst.TryGet(de, out ExtB b));
        Assert.Equal(20, b.V);
    }
}
