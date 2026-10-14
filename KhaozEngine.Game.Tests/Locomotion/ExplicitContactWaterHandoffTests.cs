using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public sealed class ExplicitContactWaterHandoffTests
{
    static readonly WaterTraversalPolicy Policy = new(WaterTraversalMode.SurfaceSwimmer, 7);
    static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        RunSpeed = 8,
        SwimSpeed = 3,
        Gravity = 20,
        JumpSpeed = 7,
        StepHeight = 0.4f,
        GroundedEpsilon = 0.05f,
    };

    [Fact]
    public void H1LandEntrySpendsOnlyTheRemainderSwimmingAndErasesLandBuffer()
    {
        using var scene = new Crossing([Wet(0.5f, 8)], floor: true);
        var state = scene.State(new(0, 0.751f, 0), grounded: true) with { JumpBufferRemaining = 0.2f };
        var result = scene.Step(state, 0.5f);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        // Four of sixteen ground substeps reach X 0.5 in 0.125 s. The remaining 0.375 s swim at 3 m/s.
        Assert.InRange(result.State.State.Position.X, 1.6249f, 1.6251f);
        Assert.True(result.State.State.Swimming);
        Assert.False(result.State.State.Grounded);
        Assert.Null(result.State.Selection!.Value.Support);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(new Vector2(4, 0), result.State.State.CommandedVelocity);
        Assert.Equal(new Vector2(3, 0), result.State.State.HorizontalVelocity);
    }

    [Fact]
    public void H2WaterOriginFlightKeepsItsOwnerAndPaceAcrossDrySpace()
    {
        using var scene = new Crossing([Wet(-8, 0.2f)]);
        var state = scene.State(new(0, 0.85f, 0)) with
        { Swimming = true, WaterExcursion = WaterExcursionState.Surface };
        var first = scene.Step(state, 0.1f, jump: true);
        Assert.Equal(MovementStepOutcome.Advanced, first.Outcome);
        Assert.InRange(first.State.State.Position.X, 0.2999f, 0.3001f);
        Assert.InRange(first.State.State.Position.Y, 1.3499f, 1.3501f);
        Assert.Equal(WaterExcursionState.AirborneFromWater, first.State.State.WaterExcursion);
        Assert.False(first.State.State.Swimming);
        var second = scene.Step(first.State.State, 0.1f, jump: true, direction: -Vector2.UnitX);
        Assert.Equal(MovementStepOutcome.Advanced, second.Outcome);
        Assert.InRange(second.State.State.Position.Y, 1.6499f, 1.6501f);
        Assert.Equal(WaterExcursionState.AirborneFromWater, second.State.State.WaterExcursion);
        Assert.False(second.State.State.Swimming);
        Assert.Equal(3, second.State.State.CommandedVelocity.Length());
        Assert.Equal(0, second.State.State.JumpBufferRemaining);
    }

    [Fact]
    public void H3WaterOriginLandingOnQuarterMetreBankWalksTheRemainingTime()
    {
        using var scene = new Crossing([Wet(-8, 8)], bank: true);
        var state = scene.State(new(0.8f, 1.2f, 0)) with
        { VerticalVelocity = -1, WaterExcursion = WaterExcursionState.AirborneFromWater };
        var result = scene.Step(state, 0.5f);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 2.2995f, 2.3005f);
        Assert.InRange(result.State.State.Position.Y, 1.0009f, 1.0011f);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Equal(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
        Assert.True(result.State.State.LandingImpactSpeed > 0);
    }

    [Fact]
    public void H4GroundWaterGroundUsesOneFacingTurnAndConsumesThePress()
    {
        using var scene = new Crossing([Wet(0.5f, 1)], floor: true);
        var result = scene.Step(scene.State(new(0, 0.751f, 0), grounded: true), 1, jump: true,
            tuning: Tuning with { FacingTurnSpeed = 0.4f }, facing: 1.5f);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 3.1245f, 3.1255f);
        Assert.InRange(result.State.State.FacingYaw, 0.3999f, 0.4001f);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(0, result.State.State.VerticalVelocity);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.Equal(0, result.State.State.ClimbRate);
    }

    [Fact]
    public void MixedModeCommitmentKeepsItsHeadingAndAdvancesItsLifecycleOnce()
    {
        using var scene = new Crossing([Wet(0.5f, 1)], floor: true);
        var state = scene.State(new(0, 0.751f, 0)) with
        {
            VerticalVelocity = -1,
            HorizontalVelocity = new(3, 0),
            FacingYaw = 0.7f,
            Commitment = new MovementCommitment(1, Vector2.UnitX, 3, 4, 20, 0, 0.1f, 3)
            { Phase = MovementCommitmentPhase.Airborne },
        };
        var result = scene.Step(state, 1, jump: true, direction: Vector2.UnitY, facing: 2);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(3, result.State.State.Position.X);
        Assert.Equal(0, result.State.State.Position.Z);
        Assert.True(result.State.State.Grounded);
        Assert.True(result.State.State.SupportGranted);
        Assert.Equal(new MovementSupportKey("world", "floor"), result.State.Selection!.Value.Support);
        Assert.Equal(0.7f, result.State.State.FacingYaw);
        Assert.Equal(MovementCommitmentPhase.Aborted, result.State.State.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.EnteredWater, result.State.State.Commitment.EndReason);
        Assert.Equal(2, result.State.State.Commitment.TimeoutRemaining);
        Assert.Equal(0.1f, result.State.State.Commitment.RecoveryRemaining);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.Equal(0, result.State.State.ClimbRate);
    }

    [Theory]
    [InlineData(100f, 1.652174f)]
    [InlineData(6f, 1.7407407f)]
    public void WaterOriginLandingImpactUsesOnlyTheConsumedDuration(float cap, float impact)
    {
        using var scene = new Crossing([Wet(-8, 8)], bank: true);
        var state = scene.State(new(0.8f, 1.2f, 0)) with
        { VerticalVelocity = -1, WaterExcursion = WaterExcursionState.AirborneFromWater };
        var result = scene.Step(state, 0.5f, tuning: Tuning with { MaxFallSpeed = cap });
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.SupportGranted);
        // The uncapped segment lands after 3 of 46 substeps. After 2 of 27 the capped segment's axis is 0.0889
        // short of the bank edge, inside the 0.125 disc, and the bank top 0.25 lies in the travelled band from
        // 0.2268. The rounded foot probe would meet the edge only at 0.2129, below its sweep end, but the
        // complete footprint finds the top there. Impact is 1 + 20 * (0.5 * n/N).
        Assert.InRange(result.State.State.LandingImpactSpeed, impact - 0.0005f, impact + 0.0005f);
    }

    [Fact]
    public void UnobstructedSegmentsKeepTheWholeTicksRoundedHorizontalEndpoint()
    {
        using var scene = new Crossing([Wet(-8, -0.75f)]);
        var state = scene.State(new(-1, 0.85f, 0)) with
        { Swimming = true, WaterExcursion = WaterExcursionState.Surface };
        var result = scene.Step(state, 1);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(2, result.State.State.Position.X);
        Assert.Equal(new Vector2(3, 0), result.State.State.HorizontalVelocity);
        Assert.Equal(WaterExcursionState.AirborneFromWater, result.State.State.WaterExcursion);
    }

    [Theory]
    [InlineData(0.5f, 2.375f)]
    [InlineData(0.7f, 3.304412f)]
    [InlineData(1f, 4.625f)]
    [InlineData(1.3f, 6.004762f)]
    public void MixedModeContinuationBindsTheRoundedRampEndpointAndPreservesEntityScale(float dt, float x)
    {
        using var scene = new Crossing([Wet(0.5f, 1)], ramp: true);
        var state = scene.State(new(0, 0.751f, 0), grounded: true) with { SpeedScale = 1.5f };
        var result = scene.Step(state, dt);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        // Ground entries are 4/24, 5/34, 4/48 and 5/63 at 6 m/s. The remainders move at 4.5 m/s.
        Assert.InRange(result.State.State.Position.X, x - 0.0005f, x + 0.0005f);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(new MovementSupportKey("world", "ramp"), result.State.Selection!.Value.Support);
        Assert.Equal(1.5f, result.State.State.SpeedScale);
        Assert.Equal(new Vector2(6, 0), result.State.State.CommandedVelocity);
        Assert.Equal(new Vector2(4.5f, 0), result.State.State.HorizontalVelocity);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
    }

    [Theory]
    [InlineData(false, 0.58693f, -2.54250f)]
    [InlineData(true, 0.55129f, -3.12499f)]
    public void AirEntryHandsOnOnlyConsumedGravityIncludingATerminalCap(bool capped, float y, float velocity)
    {
        using var scene = new Crossing([Wet(0.5f, 8)]);
        var state = scene.State(new(0, capped ? 1.7f : 2, 0)) with { VerticalVelocity = capped ? -5 : -4 };
        var result = scene.Step(state, 0.2f, tuning: Tuning with { MaxFallSpeed = capped ? 6 : 100 });
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        // Uncapped entry reaches the 0.03 m surface band at substep 11/15,
        // t 11/75, Y 0.8266667, vy -6.933333. Capped entry is
        // substep 10/12 at t 1/6, Y 0.7, vy -6. The buoyancy closed form spends the remainder once.
        Assert.InRange(result.State.State.Position.Y, y - 0.0005f, y + 0.0005f);
        Assert.InRange(result.State.State.VerticalVelocity, velocity - 0.0005f, velocity + 0.0005f);
        Assert.True(result.State.State.Swimming);
    }

    [Fact]
    public void WaterLandingRetainsTheWholeTicksClimbBudget()
    {
        using var scene = new Crossing([Wet(-8, 8)], bank: true);
        var state = scene.State(new(0.8f, 0.85f, 0)) with
        { Swimming = true, WaterExcursion = WaterExcursionState.Surface };
        var result = scene.Step(state, 0.5f, tuning: Tuning with { MaxStepClimbSpeed = 0.1f });
        Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
        // The surface feet start at 0.099. Landing plus the walking remainder can pay at most 0.05 m.
        Assert.InRange(result.State.State.Position.Y, 0.8995f, 0.9005f);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
    }

    [Fact]
    public void EightOwnershipSegmentsCannotPublishAnUnspentPrefix()
    {
        using var scene = new Crossing([Wet(0.25f, 0.5f), Wet(0.75f, 1), Wet(1.25f, 1.5f), Wet(1.75f, 2)], floor: true);
        var state = scene.State(new(0, 0.751f, 0), grounded: true);
        var result = scene.Step(state, 1);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(state.Position, result.State.State.Position);
        Assert.False(result.State.State.SupportGranted);
        Assert.Equal(0, result.State.State.LandingImpactSpeed);
    }

    [Fact]
    public void SupportedShallowLandingPrecedesABufferedLandJump()
    {
        using var scene = new Crossing([new("shallows", "room", new(new(-8), new(8, 0.5f, 8)), 0.5f)], bank: true);
        var state = scene.State(new(0.8f, 1.2f, 0)) with
        { VerticalVelocity = -1, JumpBufferRemaining = 0.2f };
        var result = scene.Step(state, 0.1f);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.SupportGranted);
        Assert.True(result.State.State.LandingImpactSpeed > 0);
        Assert.False(result.State.State.Grounded);
        Assert.Equal(7, result.State.State.VerticalVelocity);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Null(result.State.Selection!.Value.Support);
    }

    static Water Wet(float start, float end) => new("lake/" + start, "room", new(new(start, -8, -8), new(end, 1, 8)), 1);

    sealed class Crossing : IDisposable
    {
        readonly AnalyticMovementEnvironment environment;
        readonly MovementQueryLease lease;
        public Crossing(Water[] water, bool floor = false, bool bank = false, bool ramp = false)
        {
            environment = new([new Room("room", new(new(-16), new(16)))], water);
            if (floor) Add(new(8, 0.125f, 8), new(0, -0.125f, 0), "floor");
            if (bank) Add(new(2, 0.125f, 4), new(3, 0.125f, 0), "bank");
            lease = environment.Acquire("room", 4, 4);
            if (ramp)
            {
                environment.Acquisition.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
                {
                    Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
                    bool onRamp = axis.X >= 1;
                    ground = new(axis, onRamp ? 0.1f * (axis.X - 1) : 0,
                        onRamp ? Vector3.Normalize(new(-0.1f, 1, 0)) : Vector3.UnitY,
                        new("world", onRamp ? "ramp" : "floor"), Space("room"), null, Identity);
                    return MovementAvailability.Known;
                };
                environment.Acquisition.OnContactSupport = (in MovementContactSupportRequest request,
                    out MovementContactSupportBinding binding) =>
                {
                    bool onRamp = request.Axis.X >= 1;
                    float height = onRamp ? 0.1f * (request.Axis.X - 1) : 0;
                    Vector3 normal = onRamp ? Vector3.Normalize(new(-0.1f, 1, 0)) : Vector3.UnitY;
                    binding = new(new("world", onRamp ? "ramp" : "floor"), Space("room"), null, Identity);
                    return request.Static is null && request.Normal == normal &&
                        MathF.Abs(request.Height - height) <= request.HeightError
                        ? MovementAvailability.Known : MovementAvailability.Invalid;
                };
            }
            environment.Acquisition.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            {
                int count = 0;
                if (floor && request.Body.Feet.Y - request.MaxDrop <= 0 && request.Body.Feet.Y + request.MaxRise >= 0)
                    candidates[count++] = new(new("world", "floor"), Space("room"),
                        new(request.Body.Centre.X, 0, request.Body.Centre.Z), Vector3.UnitY, null);
                if (bank && request.Body.Centre.X >= 1 && request.Body.Centre.X <= 5 &&
                    request.Body.Feet.Y - request.MaxDrop <= 0.25f && request.Body.Feet.Y + request.MaxRise >= 0.25f)
                    candidates[count++] = new(new("world", "bank"), Space("room"),
                        new(request.Body.Centre.X, 0.25f, request.Body.Centre.Z), Vector3.UnitY, null);
                return new(MovementAvailability.Known, count, count, Identity);
            };
        }
        void Add(Vector3 half, Vector3 position, string owner)
        {
            var handle = environment.Physics.AddStatic(new BoxShape(half), Pose.At(position));
            environment.RegisterBoxSupport(handle, owner, "room");
        }
        public MoveState State(Vector3 position, bool grounded = false) => new()
        { Position = position, Grounded = grounded, TimeSinceGrounded = grounded ? 0 : 1 };
        public MovementStepResult Step(MoveState state, float dt, bool jump = false, Vector2? direction = null,
            MoveTuning? tuning = null, float? facing = null) => ExplicitCharacterMovement.Step(
                new(state, lease.Frame, new(Space("room"), state.Grounded ? new("world", "floor") : null, lease.Identity)),
                new(CameraInput(direction ?? Vector2.UnitX, facing ?? 0), false, facing ?? 0, jump: jump, faceCamera: facing.HasValue),
                dt, tuning ?? Tuning, Policy, lease);
        static Vector2 CameraInput(Vector2 direction, float yaw) => new(
            MathF.Cos(yaw) * direction.X - MathF.Sin(yaw) * direction.Y,
            -MathF.Sin(yaw) * direction.X - MathF.Cos(yaw) * direction.Y);
        public void Dispose() { lease.Dispose(); environment.Dispose(); }
    }
}
