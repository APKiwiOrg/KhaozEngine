using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class ContactMoveToRangeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MismatchedBindingThrowsBeforePlanning(bool contactGraph, bool loaded)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        MoveTuning legacy = Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        var profile = new NavBakeProfile("player", contactGraph ? Tuning : legacy, default);
        var bake = Bake(context, Options, profile);
        var graph = (loaded ? Load(Write(bake), new(Options, Sources(), [profile])) : bake).GetProfile("player");
        var move = new MoveToRange(graph);
        MoveTuning wrong = contactGraph ? legacy : Tuning;
        // Invalid body and dt would fail later. A kind refusal must win before any planning or prediction.
        var body = new MoveState { Position = new(float.NaN), Grounded = true };
        Assert.Contains("Kind", Assert.Throws<ArgumentException>(() =>
            move.Tick(body, wrong, default, 0, false, float.NaN, context)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReusedContextAcceptsSignedZeroSurfaceJumpSpeed(bool reverse, bool loaded)
    {
        float negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        MoveTuning priming = Tuning with { Water = new(Tuning.Water.Mode, reverse ? negativeZero : 0f) };
        MoveTuning runtime = Tuning with { Water = new(Tuning.Water.Mode, reverse ? 0f : negativeZero) };
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bakeContext = Context(world);
        GroundNavigation Graph(MoveTuning tuning)
        {
            var profile = new NavBakeProfile("player", tuning, default);
            var bake = Bake(bakeContext, FlatOptions, profile);
            return (loaded ? Load(Write(bake), new(FlatOptions, Sources(), [profile])) : bake).GetProfile("player");
        }
        GroundNavigation primingGraph = Graph(priming), graph = Graph(runtime);
        using var context = Context(world);
        var body = new MoveState { Position = new(0, runtime.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(body.Position);
        Assert.Equal(RangeMoveStatus.InRange,
            new MoveToRange(primingGraph).Tick(body, priming, target, 0, false, 1f / 30f, context).Status);
        graph.Validate(runtime);
        Assert.Equal(RangeMoveStatus.InRange,
            new MoveToRange(graph).Tick(body, runtime, target, 0, false, 1f / 30f, context).Status);
        graph.Validate(context.ResolvedBinding);
        graph.Validate(priming);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedContextRefusesDifferentSurfaceJumpSpeed(bool loaded)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        var profile = new NavBakeProfile("player", Tuning, default);
        var bake = Bake(context, FlatOptions, profile);
        var graph = (loaded ? Load(Write(bake), new(FlatOptions, Sources(), [profile])) : bake).GetProfile("player");
        var move = new MoveToRange(graph);
        var body = new MoveState { Position = new(0, Tuning.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(body.Position);
        Assert.Equal(RangeMoveStatus.InRange, move.Tick(body, Tuning, target, 0, false, 1f / 30f, context).Status);
        MoveTuning wrong = Tuning with { Water = new(Tuning.Water.Mode, 1f) };
        Assert.Contains("Water.SurfaceJumpSpeed", Assert.Throws<ArgumentException>(() =>
            move.Tick(body, wrong, target, 0, false, 1f / 30f, context)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CrossesLowLipBankBothWays(bool reverse, bool loaded)
    {
        using var world = LowLipTraversalTests.LipWorld();
        using var context = Context(world);
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f, WalkSpeed = 2, RunSpeed = 3 };
        var profile = new NavBakeProfile("player", tuning, default);
        var bake = Bake(context, Options, profile);
        var graph = (loaded ? Load(Write(bake), new(Options, Sources(), [profile])) : bake).GetProfile("player");
        var move = new MoveToRange(graph);
        Vector3 start = reverse ? new(0.925f, 0.025f, 0.125f) : new(-0.825f, 0, 0.125f);
        Vector3 end = reverse ? new(-0.825f, 0, 0.125f) : new(0.925f, 0.025f, 0.125f);
        var target = ReachTarget.Point(end + Vector3.UnitY * tuning.CapsuleHalfHeight);
        var body = new MoveState { Position = start + Vector3.UnitY * tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 };
        RangeSteering steering = default;
        bool crossedBank = false;
        for (int tick = 0; tick < 120; tick++)
        {
            steering = move.Tick(body, tuning, target, 0.01f, false, 1f / 30f, context);
            Assert.Contains(steering.Status, new[] { RangeMoveStatus.Following, RangeMoveStatus.InRange });
            body = NpcGroundMovement.Step(body, steering, false, 1f / 30f, tuning, context);
            Assert.True(body.Grounded, $"Lost footing at {body.Position} on tick {tick}.");
            crossedBank |= reverse ? body.Position.X < -0.325f : body.Position.X > 0.175f;
            if (steering.Status == RangeMoveStatus.InRange) break;
        }
        Assert.True(crossedBank, $"Did not cross the low-lip waypoints, stopped at {body.Position}.");
        Assert.Equal(RangeMoveStatus.InRange, steering.Status);
        Assert.True(ReachGeometry.Within(new MovementBody(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, 0.01f));
        MoveState held = NpcGroundMovement.Hold(body, 1f / 30f, tuning, context);
        Assert.InRange(Vector3.Distance(body.Position, held.Position), 0, 0.001f);
        Assert.Equal(RangeMoveStatus.InRange, move.Tick(held, tuning, target, 0.01f, false, 1f / 30f, context).Status);
    }

    [Fact]
    public void ResolvePicksOwnLayerAtBandEdge()
    {
        MoveTuning tuning = Tuning with { CapsuleHalfHeight = 0.7f };
        byte[] file = FlatFile(1, 1, tuning, tuning.StepHeight, out var expected);
        GroundNavigation graph = Load(file, expected).GetProfile("player");
        var body = new MoveState { Position = new(0.5f, tuning.StepHeight + tuning.CapsuleHalfHeight, 0.5f), Grounded = true };
        Vector3 feet = body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;
        // The physical centre rounds feet just above the raw band, still within 1 mm of its stored anchor.
        Assert.True(feet.Y > tuning.StepHeight);
        Assert.True(graph.AllowsSegment(feet, feet));
        Assert.Equal(KhaozEngine.Navigation.NavPathStatus.Complete,
            graph.Planner.FindPath(feet, feet, graph.AgentRadius, KhaozEngine.Navigation.PathQueryBudget.Default).Status);
    }

    [Fact]
    public void RevalidatedLegacyContextUsesCurrentGeometry()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        MoveTuning legacy = Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        var graph = Bake(context, Options, new NavBakeProfile("player", legacy, default)).GetProfile("player");
        var move = new MoveToRange(graph);
        var body = new MoveState { Position = new(-0.075f, legacy.CapsuleHalfHeight, 0.125f), Grounded = true };
        var target = ReachTarget.Point(body.Position);
        Assert.Equal(RangeMoveStatus.InRange, move.Tick(body, legacy, target, 0, false, 1f / 30f, context).Status);
        Assert.Throws<ArgumentException>(() => move.Tick(body, legacy with { CapsuleRadius = 0.3f }, target, 0, false, 1f / 30f, context));
    }
}
