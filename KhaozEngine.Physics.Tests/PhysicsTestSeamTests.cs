using System;
using System.Collections;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class PhysicsTestSeamTests
{
    [Theory]
    [InlineData(nameof(BepuPhysicsWorld.StaticHandlesForTests))]
    [InlineData(nameof(BepuPhysicsWorld.DynamicHandlesForTests))]
    [InlineData(nameof(BepuPhysicsWorld.ReverseStaticHandlesForTests))]
    [InlineData(nameof(BepuPhysicsWorld.InstalledSweepStaticsForTests))]
    [InlineData(nameof(BepuPhysicsWorld.InstalledSweepDynamicsForTests))]
    public void ClearingAnObservationCannotChangeWorldRegistrations(string seam)
    {
        // M7 Task 8 review regression: an IReadOnlyDictionary must not expose mutable registration state.
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var box = new BoxShape(new(0.5f));
        StaticHandle handle = world.AddStatic(box, Pose.At(new(2, 0, 0)));
        DynamicBodyHandle body = world.AddDynamic(box, Pose.At(new(6, 0, 0)), new DynamicBodyDescription(1));
        object observation = seam switch
        {
            nameof(BepuPhysicsWorld.StaticHandlesForTests) => world.StaticHandlesForTests,
            nameof(BepuPhysicsWorld.DynamicHandlesForTests) => world.DynamicHandlesForTests,
            nameof(BepuPhysicsWorld.ReverseStaticHandlesForTests) => world.ReverseStaticHandlesForTests,
            nameof(BepuPhysicsWorld.InstalledSweepStaticsForTests) => world.InstalledSweepStaticsForTests,
            nameof(BepuPhysicsWorld.InstalledSweepDynamicsForTests) => world.InstalledSweepDynamicsForTests,
            _ => throw new ArgumentOutOfRangeException(nameof(seam))
        };
        if (observation is IDictionary dictionary)
        {
            try { dictionary.Clear(); }
            catch (NotSupportedException) { }
        }

        Assert.Equal((1, 1, 0), world.RegistrationCountsForTests);
        Assert.Single(world.ReverseStaticHandlesForTests);
        Assert.Single(world.InstalledSweepStaticsForTests);
        Assert.Single(world.InstalledSweepDynamicsForTests);
        Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out RayHit hit));
        Assert.Equal(handle, hit.Body);
        world.RemoveStatic(handle);
        world.RemoveDynamic(body);
        Assert.Equal((0, 0, 0), world.RegistrationCountsForTests);
        Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out _));
    }
}
