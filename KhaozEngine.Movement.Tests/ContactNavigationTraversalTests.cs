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
    public void DeepCellIsNotAdmitted()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, ContactNavigationWaterTests.PoolHeight, MovementWaterField.Level("world", 0, -4));
        using var capture = PhysicsNavBake.Capture(context, ContactNavigationWaterTests.PoolOptions, _ => 0);
        GroundNavigation nav = capture.BuildProfile(Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default);
        Assert.False(nav.AllowsSegment(new(-1.75f, 0, 0.25f), new(-1.25f, -2, 0.25f)));
        Assert.Null(Resting(nav, new(-1.25f, -2, 0.25f)));
    }

    [Fact]
    public void EdgeIntoDeepCellStopsAtTheWall()
    {
        MoveTuning tuning = Tuning with
        { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.5f, Water = new(WaterTraversalMode.WadeOnly) };
        var water = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(tuning, (x, _) => x < 0 ? -0.625f : -0.75f,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -0.75f, -0.625f),
            water: water, install: (world, builder) =>
            {
                Box(world, builder, "shallow", new(-8, -1, -8), new(0, -0.625f, 8));
                Box(world, builder, "deep", new(0, -1, -8), new(8, -0.75f, 8));
            });
        using var wetContext = Context(scene, water);
        using var dryContext = Context(scene);
        var options = Options with { MinX = -1, MaxX = 1, MinZ = -0.5f, MaxZ = 0.5f, CellSize = 0.5f };
        using var wetCapture = PhysicsNavBake.Capture(wetContext, options, _ => 0);
        using var dryCapture = PhysicsNavBake.Capture(dryContext, options, _ => 0);
        Vector3 from = new(-0.25f, -0.625f, 0.25f), to = new(0.25f, -0.75f, 0.25f);
        GroundNavigation wet = wetCapture.BuildProfile(tuning, default);
        GroundNavigation dry = dryCapture.BuildProfile(tuning, default);
        Assert.Equal(from.Y, Resting(wet, from));
        Assert.Null(Resting(wet, to));
        Assert.False(wet.AllowsSegment(from, to));
        Assert.True(dry.AllowsSegment(from, to));

        using var wetSource = wetContext.CreateNavigationBinding(tuning);
        using var drySource = dryContext.CreateNavigationBinding(tuning);
        using var wetProof = new ContactNavigationProof(wetCapture, wetContext, wetSource,
            new(wetCapture.Columns, options, tuning, default));
        using var dryProof = new ContactNavigationProof(dryCapture, dryContext, drySource,
            new(dryCapture.Columns, options, tuning, default));
        Assert.False(wetProof.TryEdge(from, to));
        Assert.True(dryProof.TryEdge(from, to));

        using var binding = wetSource.CreateProofBinding();
        MovementFrameDescriptor frame = wetContext.NavigationFrame;
        MovementQueryScope scope = binding.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState
        { Position = from + Vector3.UnitY * tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 }, frame, null);
        MovementStepResult result = default;
        for (int slice = 0; slice < options.MaxEdgeProbeSteps; slice++)
        {
            Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var lease));
            using (lease)
                result = binding.StepTowards(new(state.State, frame, null), slice == 0 ? Vector2.Zero : Vector2.UnitX,
                    false, options.EdgeProbeSeconds, lease!);
            Assert.Contains(result.Outcome, new[] { MovementStepOutcome.Advanced, MovementStepOutcome.Blocked });
            if (slice == 0) Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            Assert.True(result.State.State.Grounded);
            Assert.False(result.State.State.Swimming);
            state = result.State;
            if (result.Outcome != MovementStepOutcome.Advanced) break;
        }
        Assert.True(result.Outcome != MovementStepOutcome.Advanced || result.State.State.Position.X < 0.05f,
            $"The WadeOnly proof crossed the deep line at {result.State.State.Position} with {result.Outcome}.");
        Assert.True(result.State.State.Position.X > from.X);
        Assert.Equal(from.Y, result.State.State.Position.Y - tuning.CapsuleHalfHeight);
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
