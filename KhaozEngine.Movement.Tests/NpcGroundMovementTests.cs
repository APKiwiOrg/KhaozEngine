using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class NpcGroundMovementTests
{
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        WalkSpeed = 2f,
        RunSpeed = 5f,
        CapsuleRadius = 0.2f,
        CapsuleHalfHeight = 0.75f,
    };

    [Theory]
    [InlineData(false, 0.25f)]
    [InlineData(true, 0.625f)]
    public void FollowingPreservesFractionPaceAndCarriedSpeedScale(bool run, float travel)
    {
        var context = new GroundMoveContext((_, _) => 0f);
        var body = Standing(0f, 0f);
        body.SpeedScale = 2f;
        var steering = new RangeSteering(new Vector2(0.5f, 0f), RangeMoveStatus.Following);

        MoveState result = NpcGroundMovement.Step(body, steering, run, 0.125f, Tuning, context);

        Assert.Equal(travel, result.Position.X);
        Assert.Equal(0f, result.Position.Z);
        Assert.Equal(2f, result.SpeedScale);
        Assert.Equal(-MathF.PI / 2f, result.FacingYaw, 6);
        Assert.Equal(0f, body.Position.X);
    }

    [Fact]
    public void FollowingRetainsACommandBelowTheLegacyDeadZone()
    {
        var context = new GroundMoveContext((_, _) => 0f);
        var steering = new RangeSteering(new Vector2(0.0001f, 0f), RangeMoveStatus.Following);

        MoveState result = NpcGroundMovement.Step(Standing(0f, 0f), steering, false, 0.125f, Tuning, context);

        Assert.Equal(0.000025f, result.Position.X, 8);
        Assert.True(result.Position.X > 0f);
    }

    [Fact]
    public void FollowingBoundsAnOversizedDirectionThroughTheCore()
    {
        var context = new GroundMoveContext((_, _) => 0f);
        var steering = new RangeSteering(new Vector2(3f, 4f), RangeMoveStatus.Following);

        MoveState result = NpcGroundMovement.Step(Standing(0f, 0f), steering, false, 0.125f, Tuning, context);

        Assert.Equal(0.15f, result.Position.X, 6);
        Assert.Equal(0.2f, result.Position.Z, 6);
    }

    [Theory]
    [InlineData(RangeMoveStatus.InRange)]
    [InlineData(RangeMoveStatus.WaitingForPath)]
    [InlineData(RangeMoveStatus.Unreachable)]
    [InlineData(RangeMoveStatus.UnsupportedTransition)]
    [InlineData(RangeMoveStatus.Suspended)]
    public void EveryNonFollowingStatusIgnoresItsDirectionAndResolvesSupport(RangeMoveStatus status)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var body = Standing(1f, -1f);
        body.Grounded = false;
        body.FacingYaw = 1.25f;
        var steering = new RangeSteering(Vector2.UnitX, status);

        MoveState result = NpcGroundMovement.Step(body, steering, true, 1f / 30f, Tuning, context);

        Assert.Equal(1f, result.Position.X);
        Assert.Equal(-1f, result.Position.Z);
        Assert.Equal(1.25f, result.FacingYaw);
        Assert.True(result.Grounded);
        Assert.Equal(Vector2.Zero, result.CommandedVelocity);
    }

    [Fact]
    public void HoldSettlesGravityOnRealSupportWithoutChangingHorizontalPosition()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var body = new MoveState { Position = new Vector3(1f, 2f, -1f), FacingYaw = 1.25f };

        for (int tick = 0; tick < 60 && !body.Grounded; tick++)
            body = NpcGroundMovement.Hold(body, 1f / 30f, Tuning, context);

        Assert.True(body.Grounded);
        Assert.InRange(body.Position.Y, 0.749f, 0.751f);
        Assert.Equal(1f, body.Position.X);
        Assert.Equal(-1f, body.Position.Z);
        Assert.Equal(1.25f, body.FacingYaw);
        Assert.Equal(0f, body.VerticalVelocity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WadingUsesAbsoluteMediumCoordinatesAfterRebase(bool rebase)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        if (rebase) world.Rebase(new Vector3(100f, 20f, -80f));
        var dry = new GroundMoveContext((_, _) => 0f, physics: world);
        Vector3 sampled = default;
        var wet = new GroundMoveContext((_, _) => 0f, physics: world, medium: (x, z, feet) =>
        {
            sampled = new Vector3(x, feet, z);
            return new MovementMedium(0.5f, true, 0.5f);
        });
        var body = Standing(1f, -1f);
        var steering = new RangeSteering(Vector2.UnitX, RangeMoveStatus.Following);

        MoveState dryResult = NpcGroundMovement.Step(body, steering, false, 0.125f, Tuning, dry);
        MoveState wetResult = NpcGroundMovement.Step(body, steering, false, 0.125f, Tuning, wet);

        float dryTravel = dryResult.Position.X - body.Position.X;
        float wetTravel = wetResult.Position.X - body.Position.X;
        Assert.Equal(new Vector3(1f, 0f, -1f), sampled);
        Assert.Equal(new Vector2(2f, 0f), dryResult.CommandedVelocity);
        Assert.True(dryTravel > 0f);
        Assert.InRange(wetTravel, 0.05f, 0.12f);
        Assert.True(wetTravel < dryTravel);
        Assert.False(wetResult.Swimming);
        Assert.True(wetResult.Grounded);
    }

    [Fact]
    public void FollowingHonoursWorldBounds()
    {
        var context = new GroundMoveContext((_, _) => 0f,
            clampXz: (x, z) => new Vector2(Math.Clamp(x, -1f, 0.1f), Math.Clamp(z, -1f, 0.2f)));
        var steering = new RangeSteering(Vector2.One, RangeMoveStatus.Following);

        MoveState result = NpcGroundMovement.Step(Standing(0f, 0f), steering, true, 0.125f, Tuning, context);

        Assert.Equal(0.1f, result.Position.X);
        Assert.Equal(0.2f, result.Position.Z);
        Assert.True(result.Grounded);
    }

    [Fact]
    public void FollowingCannotPressUpAnUnwalkableSlope()
    {
        var context = new GroundMoveContext((x, _) => x > 0.25f ? 3f : 0f,
            (x, _) => x > 0.25f ? Vector3.UnitX : Vector3.UnitY);
        var steering = new RangeSteering(Vector2.UnitX, RangeMoveStatus.Following);

        MoveState result = NpcGroundMovement.Step(Standing(0f, 0f), steering, true, 0.125f, Tuning, context);

        Assert.Equal(0f, result.Position.X);
        Assert.Equal(0.75f, result.Position.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoldAndSuspensionLeaveAirMomentumToTheCore(bool hold)
    {
        var context = new GroundMoveContext((_, _) => 0f);
        MoveTuning tuning = Tuning with { AirMomentum = true, AirControl = 0f, AirBrakeAccel = 0f };
        var body = new MoveState
        {
            Position = new Vector3(0f, 5f, 0f),
            HorizontalVelocity = new Vector2(2f, -1f),
            VerticalVelocity = -2f,
            TimeSinceGrounded = 1f,
            FacingYaw = 1.25f,
            SpeedScale = 0.5f,
        };
        var steering = new RangeSteering(Vector2.UnitY, RangeMoveStatus.Suspended);

        MoveState result = hold ? NpcGroundMovement.Hold(body, 0.125f, tuning, context)
            : NpcGroundMovement.Step(body, steering, true, 0.125f, tuning, context);

        Assert.Equal(0.25f, result.Position.X);
        Assert.Equal(-0.125f, result.Position.Z);
        Assert.Equal(body.HorizontalVelocity, result.HorizontalVelocity);
        Assert.True(result.VerticalVelocity < -2f);
        Assert.Equal(0.5f, result.SpeedScale);
        Assert.Equal(1.25f, result.FacingYaw);
        Assert.False(result.Grounded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoldAndSuspensionAdvanceAnActiveCommitment(bool hold)
    {
        var context = new GroundMoveContext((_, _) => 0f);
        var body = new MoveState
        {
            Position = new Vector3(0f, 5f, 0f),
            Commitment = new MovementCommitment(7, Vector2.UnitX, 2f, 4f, 0f, 2f)
            {
                Phase = MovementCommitmentPhase.Airborne,
            },
            HorizontalVelocity = new Vector2(2f, 0f),
            VerticalVelocity = 4f,
        };
        var steering = new RangeSteering(-Vector2.UnitX, RangeMoveStatus.Suspended);

        MoveState result = hold ? NpcGroundMovement.Hold(body, 0.125f, Tuning, context)
            : NpcGroundMovement.Step(body, steering, true, 0.125f, Tuning, context);

        Assert.Equal(0.25f, result.Position.X);
        Assert.Equal(7u, result.Commitment.Sequence);
        Assert.Equal(MovementCommitmentPhase.Airborne, result.Commitment.Phase);
        Assert.Equal(1.875f, result.Commitment.TimeoutRemaining);
        Assert.Equal(Vector2.UnitX, result.Commitment.Direction);
        Assert.True(result.Position.Y > body.Position.Y);
    }

    private static MoveState Standing(float x, float z)
        => new() { Position = new Vector3(x, Tuning.CapsuleHalfHeight, z), Grounded = true };
}
