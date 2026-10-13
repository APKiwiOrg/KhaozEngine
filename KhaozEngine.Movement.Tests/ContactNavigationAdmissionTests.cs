using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationAdmissionTests
{
    [Fact]
    public void LowLipBankAdmitsAnchoredNodes()
    {
        using var world = LowLipTraversalTests.LipWorld();
        using var context = Context(world);
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f };
        Vector3 bank = new(-0.075f, 0, 0.125f);
        using var legacy = new GroundMoveContext((_, _) => 0, physics: world);
        Assert.False(GroundTraversalProbeTests.Probe(legacy,
            tuning with { Controller = MovementControllerKind.Legacy, Water = default }, bank, bank));
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation nav = capture.BuildProfile(tuning, default);
        Assert.Equal(0.025f, Resting(nav, bank));
        Assert.All(nav.Space.Layers, grid => Assert.True(grid.HasRestingHeights));
        Assert.True(nav.AllowsSegment(new(-0.325f, 0.025f, 0.125f), new(0.175f, 0.025f, 0.125f)));
        Assert.Equal(NavPathStatus.Complete, nav.Planner.FindPath(new Vector3(-0.825f, 0, 0.125f),
            new Vector3(0.925f, 0.025f, 0.125f), tuning.CapsuleRadius, PathQueryBudget.Default).Status);
    }

    [Fact]
    public void RebasedCapturePublishesTheSameAbsoluteAnchorsAndGraph()
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f };
        using var zero = Scene(Vector3.Zero);
        using var rebased = Scene(new(8, 2, -4));
        using var zeroContext = Context(zero);
        using var rebasedContext = Context(rebased);
        var options = Options with { MinX = -1.5f, MaxX = 1.5f };
        using var zeroCapture = PhysicsNavBake.Capture(zeroContext, options, _ => 0);
        using var rebasedCapture = PhysicsNavBake.Capture(rebasedContext, options, _ => 0);
        GroundNavigation expected = zeroCapture.BuildProfile(tuning, default);
        GroundNavigation actual = rebasedCapture.BuildProfile(tuning, default);
        Vector3 bank = new(-0.125f, 0, 0.125f);
        Assert.Equal(0.03125f, Resting(expected, bank));
        Assert.Equal(0.03125f, Resting(actual, bank));
        SameGraph(expected, actual);
        for (int layer = 0; layer < expected.Space.Layers.Count; layer++)
        {
            NavGrid grid = expected.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                    Assert.Equal(grid.RestingHeightAt(x, z), actual.Space.Layers[layer].RestingHeightAt(x, z));
        }
        Assert.True(actual.AllowsSegment(new(-0.125f, 0.03125f, 0.125f), new(0.125f, 0.03125f, 0.125f)));

        OrdinaryContactScene Scene(Vector3 origin) => new(tuning, origin: origin, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.25f, -8), new(8, 0, 8));
            Box(world, builder, "lip", new(0, 0, -2), new(2, 0.03125f, 2));
        });
    }

    [Fact]
    public void DeckAndGroundKeepTheirOwnAnchors()
    {
        using var scene = new OrdinaryContactScene(Tuning, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "deck", new(-2, 2, -2), new(2, 2.1f, 2));
        });
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation nav = capture.BuildProfile(Tuning, default);
        Assert.Equal(0f, Resting(nav, new(-0.075f, 0, 0.125f)));
        Assert.Equal(2.1f, Resting(nav, new(-0.075f, 2.1f, 0.125f)));
        Assert.Equal(2, nav.Space.Layers.Count);
    }

    [Fact]
    public void RaisedBodyMustFitCapturedHeadroom()
    {
        using var scene = LipAndRoof(axisRoof: true);
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        var column = capture.Columns.GetColumn(6, 4);
        Assert.Equal(0f, column[0].Height);
        Assert.InRange(column[0].Headroom, 1.51f, 1.53f);
        Assert.Null(Resting(capture.BuildProfile(scene.Tuning, default), new(-0.075f, 0, 0.125f)));
    }

    [Fact]
    public void ShellCatchesOffAxisLowRoof()
    {
        using var scene = LipAndRoof(axisRoof: false);
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.True(float.IsPositiveInfinity(capture.Columns.GetColumn(6, 4)[0].Headroom));
        Assert.Null(Resting(capture.BuildProfile(scene.Tuning, default), new(-0.075f, 0, 0.125f)));
    }

    [Fact]
    public void StackedLiftRefusesRatherThanIterating()
    {
        using var scene = new OrdinaryContactScene(Tuning with { CapsuleRadius = 0.3f }, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "lip", new(0, 0, -2), new(2, 0.025f, 2));
            Box(world, builder, "second", new(0.05f, 0, -2), new(2, 0.42f, 2));
        });
        using var context = Context(scene);
        // The second tread occludes the first proposal outside its initial support search band.
        using (var queries = scene.Acquire())
        {
            Assert.Equal(MovementAvailability.Known, scene.Binding.TryFindRestingAnchor(
                scene.State(new(-0.075f, 0.75f, 0.125f)), queries, out var anchor));
            Assert.NotEqual(SupportStatus.Walkable, anchor.Support.Status);
        }
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.Null(Resting(capture.BuildProfile(scene.Tuning, default), new(-0.075f, 0, 0.125f)));
    }

    [Fact]
    public void SidewaysSeatRefuses()
    {
        using var scene = new OrdinaryContactScene(Tuning, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "wall", new(0.05f, 0, -2), new(0.1f, 2, 2));
        });
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.Null(Resting(capture.BuildProfile(Tuning, default), new(-0.075f, 0, 0.125f)));
    }

    [Fact]
    public void EnvelopeOutsideServedScopeRefuses()
    {
        int proofs = 0;
        using var scene = new OrdinaryContactScene(Tuning, (_, _) => { proofs++; return 0; },
            extent: new(new(-1, -1, -1), new(1, 1, 1), 0, 0));
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options with { MinX = 100, MaxX = 103 }, _ => 0);
        Assert.Throws<InvalidOperationException>(() => capture.BuildProfile(Tuning, default));
        Assert.Equal(0, proofs);
    }

    [Fact]
    public void LateSliceRefusalPublishesNoEdge()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var field = new ContactNavigationFaultField();
        using var context = Context(world, water: field);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation nav = capture.BuildProfile(Tuning, default);
        Assert.True(field.Refused);
        Assert.NotNull(Resting(nav, new(-0.075f, 0, 0.125f)));
        Assert.NotNull(Resting(nav, new(0.175f, 0, 0.125f)));
        Assert.False(nav.AllowsSegment(new(-0.075f, 0, 0.125f), new(0.175f, 0, 0.125f)));

        var holdField = new ContactNavigationFaultField(hold: true);
        using var holdContext = Context(world, water: holdField);
        using var holdCapture = PhysicsNavBake.Capture(holdContext, Options, _ => 0);
        GroundNavigation held = holdCapture.BuildProfile(Tuning, default);
        Assert.True(holdField.Refused);
        Assert.Null(Resting(held, new(-0.075f, 0, 0.125f)));
    }

    [Fact]
    public void HoldRefusalFollowsSuccessfulPlacement()
    {
        var field = new ContactNavigationFaultField(hold: true);
        using var scene = new OrdinaryContactScene(Tuning, water: field);
        FramedMovementState state = scene.State(new(-0.075f, 0.75f, 0.125f));
        MovementStepResult placed;
        using (var queries = scene.Acquire()) placed = scene.Binding.SettlePlacement(state, queries);
        Assert.Equal(MovementStepOutcome.Advanced, placed.Outcome);
        Assert.True(placed.State.State.Grounded);
        using var holdQueries = scene.Acquire();
        var hold = scene.Binding.StepTowards(new(placed.State.State, placed.State.Frame, null),
            Vector2.Zero, false, Options.EdgeProbeSeconds, holdQueries);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, hold.Outcome);
        Assert.Equal(placed.State.State.Position, hold.State.State.Position);
    }

    static OrdinaryContactScene LipAndRoof(bool axisRoof) => new(Tuning with { CapsuleRadius = 0.3f },
        install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "lip", new(0, 0, -2), new(2, 0.04f, 2));
            Box(world, builder, "roof", new(axisRoof ? -1 : -0.01f, 1.52f, -2), new(1, 1.62f, 2));
        });
}
