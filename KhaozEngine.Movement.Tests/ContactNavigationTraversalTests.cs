using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationTraversalTests
{
    [Fact]
    public void EdgesStepTheProofBinding()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation unit = capture.BuildProfile(Tuning with { WalkSpeed = 1, RunSpeed = 1, AirMomentum = false }, default);
        MoveTuning zeroTuning = Tuning with { WalkSpeed = 0, RunSpeed = 0, AirMomentum = true };
        GroundNavigation zero = capture.BuildProfile(zeroTuning, default);
        SameGraph(unit, zero);
        Assert.True(zero.AllowsSegment(new(-0.075f, 0, 0.125f), new(0.175f, 0, 0.125f)));
        Assert.Equal(zeroTuning, zero.Identity.Tuning);
        Assert.Equal(EffectiveMovementBindingKind.OrdinaryContact, zero.Identity.Kind);
        Assert.Equal(0f, zero.Identity.PlacementOffset);
    }

    [Fact]
    public void EdgeIntoDeepCellStopsAtTheWall()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, ContactNavigationWaterTests.PoolHeight, MovementWaterField.Level("world", 0, -4));
        using var capture = PhysicsNavBake.Capture(context, ContactNavigationWaterTests.PoolOptions, _ => 0);
        GroundNavigation nav = capture.BuildProfile(Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default);
        Assert.False(nav.AllowsSegment(new(-1.75f, 0, 0.25f), new(-1.25f, -2, 0.25f)));
        Assert.Null(Resting(nav, new(-1.25f, -2, 0.25f)));
    }

    [Fact]
    public void StairLinksProvedBetweenAnchors()
    {
        using var scene = new OrdinaryContactScene(Tuning, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "left", new(-4, 0, -2), new(0, 0.1875f, 2));
            Box(world, builder, "right", new(0, 0.34375f, -2), new(4, 0.375f, 2));
        });
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options with
        { MinX = -1, MaxX = 1, MinZ = -0.5f, MaxZ = 0.5f, CellSize = 1 }, _ => 0);
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.15625f, StepHeight = 0.21f, MaxStepClimbSpeed = 0 };
        GroundNavigation contact = capture.BuildProfile(tuning, default);
        GroundNavigation legacy = capture.BuildProfile(tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default);
        Assert.NotEmpty(legacy.Graph.Links);
        Assert.Equal(legacy.Space.Links, contact.Space.Links);
        Assert.Equal(legacy.Graph.Links, contact.Graph.Links);
        Assert.Equal(0.1875f, Resting(contact, new(-0.5f, 0.1875f, 0)));
        Assert.Equal(0.375f, Resting(contact, new(0.5f, 0.375f, 0)));
    }
}
