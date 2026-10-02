using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class PlayerPathMovementTests
{
    private const float Dt = 0.125f;
    private static readonly Func<float, float, float> Flat = (_, _) => 0f;
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        WalkSpeed = 4f,
        RunSpeed = 8f,
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        StrafeSpeedScale = 0.2f,
        BackpedalSpeedScale = 0.3f,
        BackpedalAllowsRun = false,
    };
    private static readonly MoveState Start = new()
    {
        Position = new Vector3(0f, 0.75f, 0f),
        Grounded = true,
        FacingYaw = 1.25f,
    };

    [Theory]
    [InlineData(0f, 0f, -1f, 0f, 1f)]
    [InlineData(MathF.PI / 2f, 0f, -1f, 1f, 0f)]
    [InlineData(MathF.PI, 0f, -1f, 0f, -1f)]
    [InlineData(-MathF.PI / 2f, 0f, -1f, -1f, 0f)]
    [InlineData(0f, 1f, 0f, 1f, 0f)]
    [InlineData(MathF.PI / 2f, 1f, 0f, 0f, -1f)]
    [InlineData(MathF.PI, 1f, 0f, -1f, 0f)]
    [InlineData(-MathF.PI / 2f, 1f, 0f, 0f, 1f)]
    [InlineData(0f, 0.6f, -0.8f, 0.6f, 0.8f)]
    [InlineData(MathF.PI / 2f, 0.6f, -0.8f, 0.8f, -0.6f)]
    [InlineData(MathF.PI, 0.6f, -0.8f, -0.6f, -0.8f)]
    [InlineData(-MathF.PI / 2f, 0.6f, -0.8f, -0.8f, 0.6f)]
    public void CommandUsesTheEngineCameraBasisAtEveryQuadrant(float yaw, float worldX, float worldZ,
        float axisX, float axisY)
    {
        var steering = new RangeSteering(new Vector2(worldX, worldZ), RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, false, yaw);
        MoveState moved = CharacterMovement.Step(Start, command, Dt, Flat, Tuning);

        AssertCommandFlags(command, false);
        Assert.Equal(yaw, command.CameraYaw);
        AssertVector(new Vector2(axisX, axisY), command.Move);
        AssertVector(new Vector2(worldX, worldZ), CharacterMovement.CameraRelativeDir(command));
        Assert.Equal(worldX * 0.5f, moved.Position.X, 6);
        Assert.Equal(worldZ * 0.5f, moved.Position.Z, 6);
        Assert.Equal(4f, moved.CommandedSpeed, 6);
        Assert.Equal(0.75f, moved.Position.Y);
        Assert.True(moved.Grounded);
    }

    [Theory]
    [InlineData(0f, false, 0.15f, -0.2f, 2f)]
    [InlineData(MathF.PI / 2f, true, 0.3f, -0.4f, 4f)]
    [InlineData(MathF.PI, true, 0.3f, -0.4f, 4f)]
    [InlineData(-MathF.PI / 2f, false, 0.15f, -0.2f, 2f)]
    public void FractionalDirectionKeepsItsMagnitudeAndRequestedPace(float yaw, bool run,
        float travelX, float travelZ, float speed)
    {
        var steering = new RangeSteering(new Vector2(0.3f, -0.4f), RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, run, yaw);
        MoveState moved = CharacterMovement.Step(Start, command, Dt, Flat, Tuning);

        AssertCommandFlags(command, run);
        Assert.Equal(0.5f, command.Move.Length(), 6);
        AssertVector(new Vector2(0.6f, -0.8f), CharacterMovement.CameraRelativeDir(command));
        Assert.Equal(travelX, moved.Position.X, 6);
        Assert.Equal(travelZ, moved.Position.Z, 6);
        Assert.Equal(speed, moved.CommandedSpeed, 6);
        Assert.True(moved.Grounded);
    }

    [Theory]
    [InlineData(0f, 0.0001f)]
    [InlineData(MathF.PI / 2f, 0.0001f)]
    [InlineData(MathF.PI, 1e-30f)]
    [InlineData(-MathF.PI / 2f, 1e-30f)]
    public void TinyFinalFractionSurvivesTheRealMovementStep(float yaw, float fraction)
    {
        var steering = new RangeSteering(new Vector2(0f, -fraction), RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, true, yaw);
        MoveState moved = CharacterMovement.Step(Start, command, Dt, Flat, Tuning);

        AssertCommandFlags(command, true);
        Assert.InRange((command.Move / fraction).Length(), 0.999999f, 1.000001f);
        AssertVector(new Vector2(0f, -1f), CharacterMovement.CameraRelativeDir(command));
        Assert.True(moved.Position.Z < 0f);
        Assert.InRange(moved.Position.Z / -fraction, 0.999999f, 1.000001f);
        Assert.InRange(MathF.Abs(moved.Position.X / fraction), 0f, 0.000001f);
        Assert.InRange((moved.CommandedVelocity / (8f * fraction)).Length(), 0.999999f, 1.000001f);
        Assert.True(moved.Grounded);
    }

    [Theory]
    [InlineData(RangeMoveStatus.InRange)]
    [InlineData(RangeMoveStatus.WaitingForPath)]
    [InlineData(RangeMoveStatus.Unreachable)]
    [InlineData(RangeMoveStatus.UnsupportedTransition)]
    [InlineData(RangeMoveStatus.Suspended)]
    [InlineData((RangeMoveStatus)99)]
    public void EveryNonFollowingStatusRequestsNoMotion(RangeMoveStatus status)
    {
        var steering = new RangeSteering(Vector2.UnitX, status);

        MoveCommand command = PlayerPathMovement.Command(steering, true, 0.7f);

        AssertCommandFlags(command, true);
        AssertIdleThroughRealMovement(command);
    }

    [Fact]
    public void FollowingZeroDirectionRemainsIdle()
    {
        var steering = new RangeSteering(Vector2.Zero, RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, false, 0.7f);

        AssertCommandFlags(command, false);
        AssertIdleThroughRealMovement(command);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteYawProducesFiniteIdleCommand(float yaw)
    {
        var steering = new RangeSteering(Vector2.UnitX, RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, true, yaw);

        AssertCommandFlags(command, true);
        AssertIdleThroughRealMovement(command);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.NaN)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(1f, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 1f)]
    [InlineData(1f, float.NegativeInfinity)]
    public void NonFiniteWorldDirectionProducesFiniteIdleCommand(float x, float z)
    {
        var steering = new RangeSteering(new Vector2(x, z), RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, true, 0.7f);

        AssertCommandFlags(command, true);
        AssertIdleThroughRealMovement(command);
    }

    [Theory]
    [InlineData(3f, -4f, 0.6f, -0.8f)]
    [InlineData(float.MaxValue, -float.MaxValue, 0.70710677f, -0.70710677f)]
    public void OversizedFiniteDirectionStaysFiniteAndClampsToFullPace(float x, float z,
        float directionX, float directionZ)
    {
        var steering = new RangeSteering(new Vector2(x, z), RangeMoveStatus.Following);

        MoveCommand command = PlayerPathMovement.Command(steering, true, 0.7f);
        MoveState moved = CharacterMovement.Step(Start, command, Dt, Flat, Tuning);

        AssertCommandFlags(command, true);
        Assert.True(float.IsFinite(command.Move.X));
        Assert.True(float.IsFinite(command.Move.Y));
        Assert.InRange(command.Move.Length(), 0.999999f, 1.000001f);
        AssertVector(new Vector2(directionX, directionZ), CharacterMovement.CameraRelativeDir(command));
        Assert.Equal(directionX, moved.Position.X, 6);
        Assert.Equal(directionZ, moved.Position.Z, 6);
        Assert.Equal(8f, moved.CommandedSpeed, 5);
        Assert.True(moved.Grounded);
    }

    private static void AssertCommandFlags(in MoveCommand command, bool run)
    {
        Assert.Equal(run, command.Run);
        Assert.True(command.ScaleSpeedByAxis);
        Assert.False(command.Jump);
        Assert.False(command.FaceCamera);
    }

    private static void AssertIdleThroughRealMovement(in MoveCommand command)
    {
        Assert.True(float.IsFinite(command.CameraYaw));
        Assert.Equal(Vector2.Zero, command.Move);
        Assert.Equal(Vector2.Zero, CharacterMovement.CameraRelativeDir(command));
        MoveState held = CharacterMovement.Step(Start, command, Dt, Flat, Tuning);
        Assert.Equal(Start.Position, held.Position);
        Assert.Equal(Vector2.Zero, held.CommandedVelocity);
        Assert.Equal(Start.FacingYaw, held.FacingYaw);
        Assert.True(held.Grounded);
    }

    private static void AssertVector(Vector2 expected, Vector2 actual) =>
        Assert.InRange(Vector2.Distance(expected, actual), 0f, 0.000001f);
}
