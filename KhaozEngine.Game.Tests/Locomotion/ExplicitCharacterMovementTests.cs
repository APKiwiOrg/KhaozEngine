using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class ExplicitCharacterMovementTests
{
    static readonly WaterTraversalPolicy Policy = new(WaterTraversalMode.SurfaceSwimmer, 7);
    static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        RunSpeed = 8,
        Gravity = 20,
        JumpSpeed = 7,
        GroundedEpsilon = 0.05f,
        StepHeight = 0.4f,
        BackpedalSpeedScale = 0.65f,
        SwimSpeed = 3
    };

    [Fact]
    public void CameraAndWorldCommandsUseTheSameCertifiedMovementStep()
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        var camera = ExplicitCharacterMovement.Step(state, new(new(0, 1), false, 0), 0.1f, Tuning, Policy, scene.Lease);
        var world = ExplicitCharacterMovement.StepTowards(state, new(0, -1), false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, camera.Outcome);
        Assert.Equal(camera.State.State.Position, world.State.State.Position);
        Assert.InRange(camera.State.State.Position.Z, -0.400001f, -0.399999f);
        Assert.InRange(camera.State.State.Position.Y, 1.799999f, 1.800001f);
        Assert.Equal(new Vector2(0, -4), camera.State.State.CommandedVelocity);
        Assert.Throws<InvalidOperationException>(() => scene.Environment.Physics.Step(0.1f));
    }

    [Theory]
    [InlineData(0f, 1f, false, 3f)]
    [InlineData(0f, 1f, true, 3f)]
    [InlineData(1f, 0f, true, 3f)]
    [InlineData(0f, -1f, false, 1.95f)]
    [InlineData(0f, -1f, true, 1.95f)]
    public void WaterOriginFlightKeepsSwimPaceAndDirectionalSlowdown(float x, float z, bool run, float speed)
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        MoveState move = state.State;
        move.WaterExcursion = WaterExcursionState.AirborneFromWater;
        move.VerticalVelocity = 3;
        state = new(move, state.Frame, state.Selection);
        var result = ExplicitCharacterMovement.Step(state, new(new(x, z), run, 0, faceCamera: true),
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.InRange(result.State.State.CommandedVelocity.Length(), speed - 0.00001f, speed + 0.00001f);
        Assert.Equal(WaterExcursionState.AirborneFromWater, result.State.State.WaterExcursion);
        Assert.False(result.State.State.Grounded);
        Assert.InRange(result.State.State.Position.Y, 2.09999f, 2.10001f);
    }

    [Fact]
    public void ARealWallBlocksThePublishedMoveWithoutUsingTheLegacySolver()
    {
        using var scene = new Scene(wall: true);
        var state = scene.State(new(0, 2, 0));
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, true, 0.5f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 1.70f, 1.71875f);
    }

    [Fact]
    public void GroundedMovementKeepsProvedFootingAndClearsWaterExcursion()
    {
        using var scene = new Scene(floor: true);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        MoveState carried = state.State;
        carried.WaterExcursion = WaterExcursionState.AirborneFromWater;
        state = new(carried, state.Frame, state.Selection);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Equal(new MovementSupportKey("world", "floor"), result.State.Selection!.Value.Support);
        Assert.InRange(result.State.State.Position.Y, 0.7509f, 0.7511f);
        Assert.InRange(result.State.State.Position.X, 0.3999f, 0.4001f);
    }

    [Fact]
    public void SupportedLandJumpUsesTheLandLaunchAndClearsSupport()
    {
        using var scene = new Scene(floor: true);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var result = ExplicitCharacterMovement.Step(state, new(Vector2.Zero, false, 0, jump: true),
            0.1f, Tuning, new(WaterTraversalMode.SurfaceSwimmer, 30), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.False(result.State.State.Grounded);
        Assert.Null(result.State.Selection!.Value.Support);
        Assert.InRange(result.State.State.VerticalVelocity, 4.9999f, 5.0001f);
        Assert.InRange(result.State.State.Position.Y, 1.2509f, 1.2521f);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Stale, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.CapacityExceeded, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Invalid, MovementStepOutcome.EnvironmentInvalid)]
    public void QueryRefusalHoldsPoseAndConsumesThePressWithoutRepeatingEvents(MovementAvailability availability,
        MovementStepOutcome outcome)
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        MoveState carried = state.State;
        carried.JumpBufferRemaining = 0.5f;
        carried.LandingImpactSpeed = 10;
        carried.StepDeltaY = 0.2f;
        carried.SupportGranted = true;
        carried.CommandedVelocity = Vector2.One;
        state = new(carried, state.Frame, state.Selection);
        scene.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> _,
            Span<MovementDomainContact> _) => new(availability, 0, 0, 0, 0, 0, Identity);
        var result = ExplicitCharacterMovement.Step(state, new(Vector2.UnitX, true, 0, jump: true),
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(state.Frame, result.State.Frame);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(0, result.State.State.LandingImpactSpeed);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.False(result.State.State.SupportGranted);
        Assert.Equal(Vector2.Zero, result.State.State.CommandedVelocity);
        Assert.Null(result.State.Selection);
    }

    [Fact]
    public void RefusedPressCannotLaunchWhenDataReturns()
    {
        using var scene = new Scene(floor: true);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var trace = scene.Environment.Acquisition.OnCoverage;
        scene.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> _,
            Span<MovementDomainContact> _) => new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Identity);
        var refused = ExplicitCharacterMovement.Step(state, new(Vector2.Zero, false, 0, jump: true),
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, refused.Outcome);
        scene.Environment.Acquisition.OnCoverage = trace;
        // A selected lease cannot silently become a cold reconstruction witness. Reacquire below.
        scene.Lease.Dispose();
        using var next = new Scene(floor: true, cold: true);
        var restored = new FramedMovementState(refused.State.State, next.Lease.Frame, null);
        var result = ExplicitCharacterMovement.Step(restored, MoveCommand.Idle, 0.1f, Tuning, Policy, next.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(0, result.State.State.VerticalVelocity);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
    }

    [Fact]
    public void FrameMismatchNeverRelabelsOrMovesTheState()
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        var wrongFrame = new MovementFrameDescriptor(state.Frame.Frame, state.Frame.PhysicsOrigin, state.Frame.Epoch + 1);
        state = new(state.State, wrongFrame, state.Selection);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.FrameMismatch, result.Outcome);
        Assert.Equal(wrongFrame, result.State.Frame);
        Assert.Equal(state.State.Position, result.State.State.Position);
    }

    [Fact]
    public void ColdStateReconstructsMembershipBeforeAnyMovementQueries()
    {
        using var scene = new Scene(cold: true);
        var state = scene.State(new(0, 2, 0));
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(1, scene.Environment.Acquisition.RebuildCalls);
        Assert.Equal(Space("room"), result.State.Selection!.Value.Space);
    }

    [Fact]
    public void UncertifiedWetPathCannotPublishTheCandidateSurfaceJump()
    {
        using var scene = new Scene(wet: true);
        var state = scene.State(new(0, 0.85f, 0));
        MoveState swim = state.State;
        swim.Swimming = true;
        swim.WaterExcursion = WaterExcursionState.Surface;
        state = new(swim, state.Frame, state.Selection);
        var result = ExplicitCharacterMovement.Step(state, new(Vector2.Zero, false, 0, jump: true),
            1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
    }

    sealed class Scene : IDisposable
    {
        public AnalyticMovementEnvironment Environment { get; }
        public MovementQueryLease Lease { get; }
        readonly bool cold;
        public Scene(bool floor = false, bool wall = false, bool cold = false, bool wet = false)
        {
            this.cold = cold;
            Environment = new([new Room("room", new(new(-16), new(16)))], wet
                ? [new Water("lake", "room", new(new(-8, -8, -8), new(8, 1, 8)), 1)] : []);
            if (floor) Environment.Physics.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));
            if (wall) Environment.Physics.AddStatic(new BoxShape(new(0.03125f, 8, 8)), Pose.At(new(2, 0, 0)));
            Lease = Environment.Acquire(cold ? null : "room", 1, 4);
            Environment.Acquisition.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            {
                if (floor) candidates[0] = new(new("world", "floor"), Space("room"),
                    new(request.Body.Centre.X, 0, request.Body.Centre.Z), Vector3.UnitY, null);
                return new(MovementAvailability.Known, floor ? 1 : 0, floor ? 1 : 0, Identity);
            };
        }
        public FramedMovementState State(Vector3 position, bool grounded = false) => new(
            new MoveState { Position = position, Grounded = grounded, TimeSinceGrounded = grounded ? 0 : 1 }, Lease.Frame,
            cold ? null : new MovementSelection(Space("room"), grounded ? new MovementSupportKey("world", "floor") : null, Identity));
        public void Dispose() { Lease.Dispose(); Environment.Dispose(); }
    }
}
