using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

[Collection("AllocSensitive")]
public sealed class RenderSnapshotReuseTests
{
    [Fact]
    public void CallerListPreservesSnapshotOrderContentsAndOwnership()
    {
        (LoopbackTransport a, LoopbackTransport b) = LoopbackTransport.CreatePair();
        using var source = a;
        using var receiver = b;
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        using var client = new WorldClient(receiver, static (_, _) => 0f, MoveTuning.Default, registry: registry);
        var world = new World();
        Entity first = AddRemote(world, 70, 10f);
        AddRemote(world, 20, 30f);
        AddRemote(world, 50, 20f);
        client.WorldForTest.AdvanceTick();
        client.ViewForTest.Apply(client.WorldForTest, SnapshotWriter.Write(world, registry));

        IReadOnlyList<EntityRenderState> owned = client.Snapshot();
        long[] order = [70, 20, 50];
        Assert.Equal(order, owned.Select(e => e.Id.Value));
        var reusable = new List<EntityRenderState>(capacity: 3) { default };
        client.CopySnapshotTo(reusable);
        Assert.Equal(owned, reusable);

        AllocationMeasurement.NoPerCallAllocation("Caller-owned render snapshot", () =>
        {
            for (int i = 0; i < 32; i++) client.CopySnapshotTo(reusable);
        });

        world.Set(first, ReplicatedPosition.InFrame(WorldFrame.Origin, new Vector3(90f, 0f, 0f)));
        client.WorldForTest.AdvanceTick();
        client.ViewForTest.Apply(client.WorldForTest, SnapshotWriter.Write(world, registry));
        client.CopySnapshotTo(reusable);
        Assert.Equal(order, reusable.Select(e => e.Id.Value));
        Assert.Equal(90f, reusable[0].Position.X);
        Assert.Equal(10f, owned[0].Position.X);
    }

    private static Entity AddRemote(World world, long id, float x)
    {
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(id));
        world.Set(entity, ReplicatedPosition.InFrame(WorldFrame.Origin, new Vector3(x, 0f, 0f)));
        return entity;
    }
}
