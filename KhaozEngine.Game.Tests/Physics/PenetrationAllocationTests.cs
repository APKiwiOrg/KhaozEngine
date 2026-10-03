using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Warmed penetration queries reuse their overlap scratch, through the world and through a query view.</summary>
[Collection("AllocSensitive")]
public class PenetrationAllocationTests
{
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);
    static readonly BoxShape Floor = new(new Vector3(4f, 0.25f, 4f));
    static readonly BoxShape Wall = new(new Vector3(0.25f, 2f, 4f));
    static readonly Pose FloorPose = Pose.At(new Vector3(0f, 0.25f, 0f));
    static readonly Pose WallPose = Pose.At(new Vector3(0.4f, 1f, 0f));
    static readonly Pose CapsulePose = Pose.At(new Vector3(0f, 0.75f, 0f));

    [Fact]
    public void WarmedPenetrationAllocatesNothingPerCall()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Floor, FloorPose);
        world.AddStatic(Wall, WallPose);
        for (int i = 0; i < 3; i++)
            Assert.True(world.ComputePenetration(Capsule, CapsulePose, out _));

        AllocAssert.NoPerCallAllocation("ComputePenetration", () =>
        {
            for (int i = 0; i < 100; i++) world.ComputePenetration(Capsule, CapsulePose, out _);
        });
    }

    [Fact]
    public void WarmedViewPenetrationAllocatesNothingPerCall()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = world.AddStatic(Floor, FloorPose);
        world.AddStatic(Wall, WallPose);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(new[] { floor });
        for (int i = 0; i < 3; i++)
            Assert.True(view.ComputePenetration(Capsule, CapsulePose, out _));

        AllocAssert.NoPerCallAllocation("view ComputePenetration", () =>
        {
            for (int i = 0; i < 100; i++) view.ComputePenetration(Capsule, CapsulePose, out _);
        });
    }

    [Fact]
    public void ConsecutiveQueriesDoNotLeakCandidates()
    {
        var second = new BoxShape(new Vector3(0.5f, 1f, 0.5f));
        Pose secondPose = Pose.At(new Vector3(10.4f, 1f, 0f));
        Pose nearSecond = Pose.At(new Vector3(10f, 1f, 0f));
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Wall, WallPose);
        world.AddStatic(second, secondPose);
        using var secondOnly = new BepuPhysicsWorld(Vector3.Zero);
        secondOnly.AddStatic(second, secondPose);

        Assert.True(world.ComputePenetration(Capsule, CapsulePose, out Vector3 intoWall));
        Assert.True(intoWall.X < 0f);
        Assert.False(world.ComputePenetration(Capsule, Pose.At(new Vector3(5f, 5f, 0f)), out Vector3 openAir));
        Assert.Equal(default, openAir);
        Assert.True(world.ComputePenetration(Capsule, nearSecond, out Vector3 intoSecond));
        Assert.True(secondOnly.ComputePenetration(Capsule, nearSecond, out Vector3 reference));
        Assert.Equal(reference, intoSecond);
    }
}
