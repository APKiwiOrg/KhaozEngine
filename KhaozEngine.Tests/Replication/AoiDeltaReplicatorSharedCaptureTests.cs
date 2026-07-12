using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The shared per-tick capture: <see cref="AoiDeltaReplicator"/> scans and captures each world ONCE per tick and
/// projects it per client, instead of a full world scan + capture per client. These tests assert the scan is
/// actually shared (via the <c>WorldScanCount</c> seam), that distinct worlds within one tick each get their own
/// scan (the sharded multi-cell serve pattern), and that owner-scoped filtering stays correct across the sharing.
/// </summary>
public class AoiDeltaReplicatorSharedCaptureTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private struct Secret : IComponent { public int V; }

    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId;

    private static ReplicationRegistry PlainRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1, (p, bw) => { bw.Write(p.X); bw.Write(p.Y); }, br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static ReplicationRegistry OwnerScopedRegistry()
    {
        ReplicationRegistry r = PlainRegistry();
        r.Register<Secret>(SecretId, (s, bw) => bw.Write(s.V), br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        return r;
    }

    private static Entity Spawn(World w, long netId, float x, float y, int? secret = null)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new Pos { X = x, Y = y });
        if (secret is int v) w.Set(e, new Secret { V = v });
        return e;
    }

    private static HashSet<long> Aoi(params long[] ids) => new(ids);

    [Fact]
    public void OneWorld_ManyClients_ScansTheWorldOncePerTick()
    {
        var world = new World();
        for (long i = 1; i <= 20; i++) Spawn(world, i, i, i);
        var repl = new AoiDeltaReplicator(PlainRegistry());

        repl.BeginTick();
        for (int slot = 0; slot < 8; slot++) repl.WriteFor(slot, world, Aoi(1, 2, 3, 4, 5));
        Assert.Equal(1, repl.WorldScanCount);   // 8 clients, one scan

        repl.BeginTick();
        for (int slot = 0; slot < 8; slot++) repl.WriteFor(slot, world, Aoi(1, 2, 3, 4, 5));
        Assert.Equal(2, repl.WorldScanCount);   // next tick, one more scan
    }

    [Fact]
    public void DistinctWorlds_InOneTick_ScanOncePerWorld()
    {
        // The sharded server serves different clients from different home-cell worlds within one tick, so the capture
        // is keyed by world: one scan per distinct world, still shared by every client homed in that world.
        var worldA = new World();
        Spawn(worldA, 1, 0, 0);
        var worldB = new World();
        Spawn(worldB, 2, 0, 0);
        var repl = new AoiDeltaReplicator(PlainRegistry());

        repl.BeginTick();
        repl.WriteFor(0, worldA, Aoi(1));
        repl.WriteFor(1, worldA, Aoi(1));   // same world, reuses the scan
        repl.WriteFor(2, worldB, Aoi(2));   // different world, one more scan
        Assert.Equal(2, repl.WorldScanCount);
    }

    [Fact]
    public void SharedCapture_KeepsOwnerScopingCorrect_AcrossClients()
    {
        // Two owners served from ONE shared capture (one scan): each must still see only its own owner-only component.
        var world = new World();
        Spawn(world, 1, 0, 0, secret: 111);
        Spawn(world, 2, 10, 0, secret: 222);
        var repl = new AoiDeltaReplicator(OwnerScopedRegistry());
        ReplicationRegistry clientReg = OwnerScopedRegistry();

        repl.BeginTick();
        byte[] toA = repl.WriteFor(0, world, Aoi(1, 2), ownerNetId: 1);
        byte[] toB = repl.WriteFor(1, world, Aoi(1, 2), ownerNetId: 2);
        Assert.Equal(1, repl.WorldScanCount);   // one scan feeds both owners

        var wA = new World(); var vA = new ClientReplicationView(clientReg); vA.ApplyDelta(wA, toA);
        var wB = new World(); var vB = new ClientReplicationView(clientReg); vB.ApplyDelta(wB, toB);

        Assert.True(wA.Has<Secret>(vA.Entities[1]));    // A owns 1
        Assert.False(wA.Has<Secret>(vA.Entities[2]));   // A never sees B's owner-only bytes
        Assert.True(wB.Has<Secret>(vB.Entities[2]));
        Assert.False(wB.Has<Secret>(vB.Entities[1]));
    }

    [Fact]
    public void SharingDoesNotPerturbPerClientBytes()
    {
        // A client's frame must be identical whether or not another client was served first from the shared capture.
        // Compare a replicator that serves only slot B against one that serves slot A then slot B: slot B's bytes match.
        var world = new World();
        Spawn(world, 1, 1, 1, secret: 111);
        Spawn(world, 2, 2, 2, secret: 222);
        Spawn(world, 3, 3, 3);

        var only = new AoiDeltaReplicator(OwnerScopedRegistry());
        only.BeginTick();
        byte[] onlyB = only.WriteFor(1, world, Aoi(1, 2, 3), ownerNetId: 2);

        var shared = new AoiDeltaReplicator(OwnerScopedRegistry());
        shared.BeginTick();
        shared.WriteFor(0, world, Aoi(1, 2, 3), ownerNetId: 1);   // serve A first, populating the shared capture
        byte[] sharedB = shared.WriteFor(1, world, Aoi(1, 2, 3), ownerNetId: 2);

        Assert.Equal(onlyB, sharedB);
    }
}
