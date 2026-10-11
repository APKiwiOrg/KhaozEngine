using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    const float PlacementSkin = 0.001f;

    [Theory]
    [InlineData(0.4f, 30)]
    [InlineData(0.4f, 60)]
    [InlineData(0.75f, 30)]
    [InlineData(0.75f, 60)]
    public void DescendingBankStopsAtTheBodyDepthLineAndKeepsAlongShoreProgress(float halfHeight, int hz)
    {
        var tuning = DeepWaterTuning(halfHeight);
        float deepFeet = 1 - 2 * halfHeight * tuning.SwimEnterDepthFraction;
        float highFloor = deepFeet - PlacementSkin + 0.1f;
        using var environment = new AnalyticMovementEnvironment(
            [new Room("room", new(new(-16), new(16)))],
            [new Water("lake", "room", new(new(-8, -4, -8), new(8, 1, 8)), 1)]);
        using var lease = environment.Acquire("room", 4, 4);
        Vector3 normal = Vector3.Normalize(new Vector3(0.1f, 1, 0));
        environment.Acquisition.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            var axis = new Vector2(request.Body.Centre.X, request.Body.Centre.Z);
            ground = new(axis, highFloor - 0.1f * axis.X, normal,
                new("world", "bank"), Space("room"), null, Identity);
            return MovementAvailability.Known;
        };
        environment.Acquisition.OnContactSupport = (in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "bank"), Space("room"), null, Identity);
            return MovementAvailability.Known;
        };
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0, highFloor + halfHeight + PlacementSkin, 0),
            Grounded = true,
        }, lease.Frame, new(Space("room"), new MovementSupportKey("world", "bank"), Identity));
        lease.Dispose();
        bool blocked = false;
        for (int tick = 0; tick < hz; tick++)
        {
            var acquired = environment.Acquisition.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            using var tickLease = Assert.IsType<MovementQueryLease>(acquired.Lease);
            var result = ExplicitCharacterMovement.StepTowards(state, Vector2.One, false, 1f / hz,
                tuning, new(WaterTraversalMode.WadeOnly), tickLease);
            Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked,
                $"tick {tick}, pose {state.State.Position}, outcome {result.Outcome}");
            blocked |= result.Outcome == MovementStepOutcome.Blocked;
            state = result.State;
            Assert.True(state.State.Grounded);
            Assert.False(state.State.Swimming);
            Assert.True(state.State.Position.Y - halfHeight > deepFeet);
        }
        Assert.True(blocked);
        // h = deepFeet - s at X = 1. A tick already within the 0.002 m retreat keeps its safe start.
        Assert.InRange(state.State.Position.X, 0.97f, 1f);
        Assert.True(state.State.Position.Z > 1.27f);
        Assert.InRange(state.State.Position.Y - halfHeight,
            deepFeet, deepFeet + 0.003f);
        Assert.Equal(new MovementSupportKey("world", "bank"), state.Selection!.Value.Support);
    }

    [Theory]
    [InlineData(0.4f, 30)]
    [InlineData(0.4f, 60)]
    [InlineData(0.75f, 30)]
    [InlineData(0.75f, 60)]
    public void IntegratedWadingKeepsTheExistingDepthSpeedFormula(float halfHeight, int hz)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        var tuning = DeepWaterTuning(halfHeight);
        float depth = (0.3f - PlacementSkin) / (2 * halfHeight);
        float scale = 1 - (depth - tuning.WadeStartDepthFraction) /
            (tuning.WadeEndDepthFraction - tuning.WadeStartDepthFraction) * (1 - tuning.WadeMinSpeedScale);
        var result = ExplicitCharacterMovement.StepTowards(
            scene.State(new(0, halfHeight + PlacementSkin, 0), grounded: true),
            Vector2.UnitX, false, 1f / hz, tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.InRange(result.State.State.Position.X,
            tuning.WalkSpeed * scale / hz - 0.0005f, tuning.WalkSpeed * scale / hz + 0.0005f);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.InRange(result.State.State.Position.Y, halfHeight + 0.0005f, halfHeight + 0.0015f);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void IntegratedWalkerCannotSkipAThinDeepBandBetweenDryEndpoints(float halfHeight)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("channel", "room", new(new(1, -1, -8), new(1.05f, 1, 8)), 1)]);
        var result = ExplicitCharacterMovement.StepTowards(
            scene.State(new(0, halfHeight + PlacementSkin, 0), grounded: true),
            Vector2.UnitX, false, 1, DeepWaterTuning(halfHeight), new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        // Water starts at 1, the full radius is 0.25, coverage skin is 0.001 and retreat is 0.002.
        Assert.InRange(result.State.State.Position.X, 0.7465f, 0.7475f);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void IntegratedDeepEntryThresholdIsAnInclusivePlacementRefusal(float halfHeight)
    {
        var tuning = DeepWaterTuning(halfHeight);
        float floor = 1 - 2 * halfHeight * tuning.SwimEnterDepthFraction - PlacementSkin;
        using var scene = new Scene(floor: true, floorY: floor, wet: true);
        var state = scene.State(new(0, floor + halfHeight + PlacementSkin, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.One, false, 0.125f,
            tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.PlacementRefused, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(Vector2.Zero, result.State.State.CommandedVelocity);
        Assert.Null(result.State.Selection);
    }

    [Theory]
    [InlineData(0.4f, -1f, 0f, MovementStepOutcome.Advanced)]
    [InlineData(0.4f, 0f, 1f, MovementStepOutcome.Advanced)]
    [InlineData(0.4f, 1f, 0f, MovementStepOutcome.Blocked)]
    [InlineData(0.75f, -1f, 0f, MovementStepOutcome.Advanced)]
    [InlineData(0.75f, 0f, 1f, MovementStepOutcome.Advanced)]
    [InlineData(0.75f, 1f, 0f, MovementStepOutcome.Blocked)]
    public void IntegratedCertifiedShoreTangentAdmitsOnlyOutwardAndParallelTravel(float halfHeight,
        float x, float z, MovementStepOutcome expected)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("lake", "room", new(new(1, -4, -8), new(8, 1, 8)), 1)]);
        scene.Environment.CertifyAxisSeparation = true;
        var result = ExplicitCharacterMovement.StepTowards(
            scene.State(new(0.75f, halfHeight + PlacementSkin, 0), grounded: true), new(x, z), false,
            0.125f, DeepWaterTuning(halfHeight), new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(x < 0 ? 0.25f : 0.75f, result.State.State.Position.X);
        Assert.Equal(z * 0.5f, result.State.State.Position.Z);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void IntegratedShoreCornerIsIndependentOfContactInsertionOrder(float halfHeight)
    {
        Water east = new("east", "room", new(new(1, -1, -8), new(8, 1, 8)), 1);
        Water north = new("north", "room", new(new(-8, -1, 1), new(1, 1, 8)), 1);
        using var a = new Scene(floor: true, waterRegions: [east, north]);
        using var b = new Scene(floor: true, waterRegions: [north, east]);
        var trace = b.Environment.Acquisition.OnCoverage!;
        b.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            var coverage = trace(query, spans, contacts);
            foreach (var span in spans[..coverage.SpansWritten])
                contacts.Slice(span.ContactStart, span.ContactCount).Reverse();
            return coverage;
        };
        var tuning = DeepWaterTuning(halfHeight);
        var ar = ExplicitCharacterMovement.StepTowards(a.State(new(0, halfHeight + PlacementSkin, 0), grounded: true),
            Vector2.One, false, 1, tuning, new(WaterTraversalMode.WadeOnly), a.Lease);
        var br = ExplicitCharacterMovement.StepTowards(b.State(new(0, halfHeight + PlacementSkin, 0), grounded: true),
            Vector2.One, false, 1, tuning, new(WaterTraversalMode.WadeOnly), b.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, ar.Outcome);
        Assert.Equal(ar, br);
        Assert.InRange(ar.State.State.Position.X, 0.7465f, 0.7475f);
        Assert.InRange(ar.State.State.Position.Z, 0.7465f, 0.7475f);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void IntegratedDryOnlyBlocksEvenTheSupportedShallows(float halfHeight)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("shallows", "room", new(new(1, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        var result = ExplicitCharacterMovement.StepTowards(
            scene.State(new(0, halfHeight + PlacementSkin, 0), grounded: true), Vector2.UnitX, false,
            1, DeepWaterTuning(halfHeight), new(WaterTraversalMode.DryOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 0.7465f, 0.7475f);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void IntegratedWadeOnlyIgnoresStaleSwimExitHistory(float halfHeight)
    {
        var tuning = DeepWaterTuning(halfHeight);
        float floor = 1 - 2 * halfHeight * 0.6f - PlacementSkin;
        using var scene = new Scene(floor: true, floorY: floor, wet: true);
        var fresh = scene.State(new(0, floor + halfHeight + PlacementSkin, 0), grounded: true);
        var stale = new FramedMovementState(fresh.State with
        {
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
        }, fresh.Frame, fresh.Selection);
        var expected = ExplicitCharacterMovement.StepTowards(fresh, Vector2.UnitX, false, 0.125f,
            tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        var result = ExplicitCharacterMovement.StepTowards(stale, Vector2.UnitX, false, 0.125f,
            tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(expected.State, result.State);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
    }

    [Theory]
    [InlineData(0.4f, false)]
    [InlineData(0.4f, true)]
    [InlineData(0.75f, false)]
    [InlineData(0.75f, true)]
    public void IntegratedFullBodyProofKeepsTheDryCaveAndBridgeTraversable(float halfHeight, bool bridge)
    {
        using var environment = new AnalyticMovementEnvironment(
            [new Room("cave", new(new(-8, -4, -8), new(8, 0, 8))),
             new Room("outside", new(new(-8, 0, -8), new(8, 8, 8)))],
            [new Water("lake", "outside", new(new(-8, 0, -8), new(8, 1, 8)), 1)]);
        float floor = bridge ? 1.25f : -2;
        string room = bridge ? "outside" : "cave";
        var slab = environment.Physics.AddStatic(new BoxShape(new(8, 0.125f, 8)),
            Pose.At(new(0, floor - 0.125f, 0)));
        environment.RegisterBoxSupport(slab, "floor", room);
        using var lease = environment.Acquire(room, 4, 4);
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0, floor + halfHeight + PlacementSkin, 0),
            Grounded = true,
        }, lease.Frame, new(Space(room), new MovementSupportKey("world", "floor"), Identity));
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.125f,
            DeepWaterTuning(halfHeight), new(WaterTraversalMode.WadeOnly), lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(0.5f, result.State.State.Position.X);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(Space(room), result.State.Selection!.Value.Space);
        Assert.InRange(result.State.State.Position.Y,
            floor + halfHeight + 0.0005f, floor + halfHeight + 0.0015f);
    }

    [Theory]
    [InlineData(0.4f, WaterTraversalMode.DryOnly)]
    [InlineData(0.4f, WaterTraversalMode.WadeOnly)]
    [InlineData(0.4f, WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(0.75f, WaterTraversalMode.DryOnly)]
    [InlineData(0.75f, WaterTraversalMode.WadeOnly)]
    [InlineData(0.75f, WaterTraversalMode.SurfaceSwimmer)]
    public void IntegratedClosedFloodedPlacementRefusesEveryExplicitPolicy(float halfHeight, WaterTraversalMode mode)
    {
        using var scene = new Scene(floor: true, wet: true, closedWet: true);
        var state = scene.State(new(0, halfHeight + PlacementSkin, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.125f,
            DeepWaterTuning(halfHeight), new(mode), scene.Lease);
        Assert.Equal(MovementStepOutcome.PlacementRefused, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(Vector2.Zero, result.State.State.CommandedVelocity);
        Assert.Null(result.State.Selection);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void IntegratedPolicyContrastLeavesSurfaceSwimmerFreeWaterDynamicsAvailable(float halfHeight)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("lake", "room", new(new(1, -1, -8), new(8, 1, 8)), 1)]);
        var tuning = DeepWaterTuning(halfHeight);
        var state = scene.State(new(0, halfHeight + PlacementSkin, 0), grounded: true);
        var walker = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 1,
            tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        var swimmer = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 1,
            tuning, new(WaterTraversalMode.SurfaceSwimmer), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, walker.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, swimmer.Outcome);
        Assert.True(swimmer.State.State.Position.X > 1.25f);
        Assert.True(swimmer.State.State.Swimming);
        Assert.False(swimmer.State.State.Grounded);
        Assert.Null(swimmer.State.Selection!.Value.Support);
        Assert.Equal(WaterExcursionState.Surface, swimmer.State.State.WaterExcursion);
    }

    static MoveTuning DeepWaterTuning(float halfHeight) => Tuning with
    {
        CapsuleHalfHeight = halfHeight,
        StepHeight = halfHeight == 0.4f ? 0.25f : Tuning.StepHeight,
    };
}
