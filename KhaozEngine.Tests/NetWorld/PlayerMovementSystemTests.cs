using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerMovementSystemTests
{
    private static float Flat(float x, float z) => 0f;

    private static Entity SpawnPlayer(World w, int netId, Vector3 pos, MoveCommand cmd)
    {
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, new ReplicatedPosition { Value = pos });
        w.Set(e, new PendingMove { Command = cmd });
        return e;
    }

    [Fact]
    public void Step_AdvancesOwnedPlayer_AlongCommand()
    {
        var w = new World();
        var sys = new PlayerMovementSystem(Flat, MoveTuning.Default);
        // Move +X (camera-relative right at yaw 0), run speed 6 m/s.
        Entity e = SpawnPlayer(w, 1, new Vector3(0f, 0f, 0f), new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f));

        sys.Update(w, 0.1f);

        Vector3 after = w.Get<ReplicatedPosition>(e).Value;
        Assert.True(after.X > 0.05f, $"expected +X motion, got {after.X}");
        Assert.Equal(MoveTuning.Default.CapsuleHalfHeight, after.Y, 3); // clamped onto flat ground + half-height
    }

    [Fact]
    public void Step_SkipsGhostsAndMigrating()
    {
        var w = new World();
        var sys = new PlayerMovementSystem(Flat, MoveTuning.Default);
        var cmd = new MoveCommand(new Vector2(1f, 0f), true, 0f);

        Entity ghost = SpawnPlayer(w, 2, new Vector3(5f, 0f, 0f), cmd);
        w.Set(ghost, new Ghost { Source = new CellCoord(0, 0) });
        Entity migrating = SpawnPlayer(w, 3, new Vector3(7f, 0f, 0f), cmd);
        w.Set(migrating, new Migrating { Destination = new CellCoord(1, 0) });

        sys.Update(w, 0.1f);

        Assert.Equal(5f, w.Get<ReplicatedPosition>(ghost).Value.X, 3);     // unchanged
        Assert.Equal(7f, w.Get<ReplicatedPosition>(migrating).Value.X, 3); // unchanged
    }

    [Fact]
    public void NullGroundHeight_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlayerMovementSystem(null!, MoveTuning.Default));
    }
}
