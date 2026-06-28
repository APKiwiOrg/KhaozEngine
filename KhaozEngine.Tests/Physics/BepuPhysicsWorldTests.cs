using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class BepuPhysicsWorldTests
{
    [Fact]
    public void Raycast_HitsAStaticBox()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit h);
        Assert.True(hit);
        Assert.Equal(4f, h.Distance, 2);                 // 5 - half-depth 1
        Assert.True(Vector3.Dot(h.Normal, -Vector3.UnitZ) > 0.9f);
    }

    [Fact]
    public void SweepCapsule_StopsBeforeAWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 2f, 0.5f)), Pose.At(new Vector3(0f, 1f, 5f)));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        bool hit = world.SweepCapsule(cap, Pose.At(new Vector3(0f, 1f, 0f)), Vector3.UnitZ, 100f, out SweepHit h);
        Assert.True(hit);
        Assert.True(h.Distance > 0f && h.Distance < 5f);  // contacts before the wall plane at z=4.5
    }

    [Fact]
    public void ComputePenetration_PushesOutOfAnOverlap()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        // capsule centre inside the box -> must report a separating translation
        bool overlap = world.ComputePenetration(cap, Pose.At(new Vector3(0.5f, 0f, 0f)), out Vector3 mtv);
        Assert.True(overlap);
        Assert.True(mtv.Length() > 0f);
    }

    [Fact]
    public void RemoveStatic_StopsHits()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        StaticHandle h = world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.RemoveStatic(h);
        world.Step(1f / 60f);
        Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out _));
    }
}
