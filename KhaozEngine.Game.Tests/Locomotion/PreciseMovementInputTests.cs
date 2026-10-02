using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class PreciseMovementInputTests
{
    private const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (_, _) => 0f;
    private static readonly MoveTuning Tuning = new(2f, 5f, 0.75f, MathF.PI / 4f, CapsuleRadius: 0.3f);
    private static readonly MoveState Start = new() { Position = new Vector3(0f, 0.75f, 0f), Grounded = true };

    [Fact]
    public void LegacyFractionalAxesStillMoveAtFullSpeed()
    {
        var cmd = new MoveCommand(new Vector2(0f, 0.125f), run: true, cameraYaw: 0f);
        MoveState legacy = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);

        Assert.Equal(new Vector3(0f, 0.75f, -5f / 30f), legacy.Position);
        Assert.Equal(new Vector2(0f, -5f), legacy.CommandedVelocity);
        Assert.True(legacy.Grounded);
    }

    [Theory]
    [InlineData(0.0001f)]
    [InlineData(1e-30f)]
    public void TinyPreciseAxesStillMove(float fraction)
    {
        var cmd = new MoveCommand(new Vector2(0f, fraction), true, 0f, false, false, true);
        MoveState tiny = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);

        Assert.True(tiny.Position.Z < 0f);
        Assert.InRange(tiny.Position.Z / (-5f / 30f * fraction), 0.999999f, 1.000001f);
        Assert.Equal(0f, tiny.Position.X);
        Assert.Equal(0.75f, tiny.Position.Y);
        Assert.True(tiny.Grounded);
    }

    [Theory]
    [InlineData(0.0001f)]
    [InlineData(1e-30f)]
    public void TinyPreciseNpcDirectionStillMoves(float fraction)
    {
        var direction = new Vector2(0f, -fraction);
        MoveState legacy = CharacterMovement.StepTowards(Start, direction, true, Dt, Flat, Tuning);
        MoveState tiny = CharacterMovement.StepTowards(Start, direction, true, Dt, Flat, Tuning,
            preserveSmallMagnitude: true);

        Assert.Equal(Start.Position, legacy.Position);
        Assert.True(tiny.Position.Z < 0f);
        Assert.InRange(tiny.Position.Z / (-5f / 30f * fraction), 0.999999f, 1.000001f);
        Assert.Equal(0f, tiny.Position.X);
        Assert.Equal(0.75f, tiny.Position.Y);
        Assert.True(tiny.Grounded);
    }

    [Theory]
    [InlineData(0f, float.MaxValue, 0f, 0f, -0.16666667f)]
    [InlineData(float.MaxValue, float.MaxValue, 0f, 0.11785113f, -0.11785113f)]
    [InlineData(float.MaxValue, float.MaxValue, 0.7853982f, 0f, -0.16666667f)]
    public void ExtremeFiniteAxesStayFiniteAndAtMostFullSpeed(float x, float y, float yaw,
        float expectedX, float expectedZ)
    {
        var cmd = new MoveCommand(new Vector2(x, y), true, yaw, false, false, true);
        MoveState extreme = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);

        Assert.True(float.IsFinite(extreme.Position.X));
        Assert.True(float.IsFinite(extreme.Position.Z));
        Assert.InRange(new Vector2(extreme.Position.X, extreme.Position.Z).Length(), 0f, 5f / 30f + 0.000001f);
        Assert.Equal(expectedX, extreme.Position.X, 6);
        Assert.Equal(expectedZ, extreme.Position.Z, 6);
        Assert.Equal(5f, extreme.CommandedSpeed, 5);
        Assert.True(extreme.Grounded);

        MoveState npc = CharacterMovement.StepTowards(Start, new Vector2(x, -y), true, Dt, Flat, Tuning,
            preserveSmallMagnitude: true);
        Assert.True(float.IsFinite(npc.Position.X));
        Assert.True(float.IsFinite(npc.Position.Z));
        Assert.InRange(new Vector2(npc.Position.X, npc.Position.Z).Length(), 0f, 5f / 30f + 0.000001f);
        Assert.Equal(5f, npc.CommandedSpeed, 5);
    }

    [Fact]
    public void DefaultCommandIsStillIdle()
    {
        MoveState idle = CharacterMovement.Step(Start, default, Dt, Flat, Tuning);
        MoveState precise = CharacterMovement.Step(Start,
            new MoveCommand(Vector2.Zero, true, 0f, false, false, true), Dt, Flat, Tuning);
        MoveState npc = CharacterMovement.StepTowards(Start, Vector2.Zero, true, Dt, Flat, Tuning,
            preserveSmallMagnitude: true);

        Assert.Equal(Start.Position, idle.Position);
        Assert.Equal(Vector2.Zero, idle.CommandedVelocity);
        Assert.Equal(idle, precise);
        Assert.Equal(idle, npc);
    }

    [Fact]
    public void PreciseNpcAndPlayerShareTheGroundCore()
    {
        var cmd = new MoveCommand(new Vector2(0f, 0.125f), true, 0f, false, false, true);
        MoveState precise = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);
        MoveState npc = CharacterMovement.StepTowards(Start, new Vector2(0f, -0.125f), true, Dt, Flat, Tuning,
            preserveSmallMagnitude: true);

        Assert.Equal(-5f / 30f * 0.125f, precise.Position.Z, 6);
        Assert.Equal(new Vector2(0f, -0.625f), precise.CommandedVelocity);
        Assert.True(precise.Grounded);
        Assert.Equal(precise, npc);
    }

    [Theory]
    [InlineData(0.125f, 0f, true, 0.010416667f, 0f, 0.3125f)]
    [InlineData(0f, -0.125f, true, 0f, 0.004166667f, 0.125f)]
    [InlineData(0.125f, 0f, false, 0.020833334f, 0f, 0.625f)]
    public void DirectionalScaleIsAppliedOnce(float x, float y, bool faceCamera,
        float expectedX, float expectedZ, float expectedSpeed)
    {
        MoveTuning tuning = Tuning with { StrafeSpeedScale = 0.5f, BackpedalSpeedScale = 0.5f, BackpedalAllowsRun = false };
        var cmd = new MoveCommand(new Vector2(x, y), true, 0f, false, faceCamera, true);
        MoveState precise = CharacterMovement.Step(Start, cmd, Dt, Flat, tuning);

        Assert.Equal(expectedX, precise.Position.X, 6);
        Assert.Equal(expectedZ, precise.Position.Z, 6);
        Assert.Equal(expectedSpeed, precise.CommandedSpeed, 6);
        Assert.Equal(faceCamera ? 0f : -MathF.PI / 2f, precise.FacingYaw);
    }

    [Theory]
    [InlineData(0.25f, 0.5f, false)]
    [InlineData(0f, 0f, false)]
    [InlineData(0.25f, 0.5f, true)]
    [InlineData(1f, 0f, true)]
    [InlineData(0f, -0.5f, true)]
    [InlineData(0f, 0.0001f, false)]
    public void FalseOptInMatchesTheRetainedConstructor(float x, float y, bool faceCamera)
    {
        MoveTuning tuning = Tuning with { StrafeSpeedScale = 0.4f, BackpedalSpeedScale = 0.3f, BackpedalAllowsRun = false };
        var legacy = new MoveCommand(new Vector2(x, y), true, 0.7f, faceCamera: faceCamera);
        var unscaled = new MoveCommand(new Vector2(x, y), true, 0.7f, false, faceCamera, false);

        Assert.Equal(CharacterMovement.Step(Start, legacy, Dt, Flat, tuning),
            CharacterMovement.Step(Start, unscaled, Dt, Flat, tuning));
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.NaN)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(1f, float.NegativeInfinity)]
    public void NonFinitePreciseInputRemainsIdle(float x, float y)
    {
        var cmd = new MoveCommand(new Vector2(x, y), true, 0f, false, false, true);
        MoveState player = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);
        MoveState npc = CharacterMovement.StepTowards(Start, new Vector2(x, y), true, Dt, Flat, Tuning,
            preserveSmallMagnitude: true);

        Assert.Equal(Start.Position, player.Position);
        Assert.Equal(Vector2.Zero, player.CommandedVelocity);
        Assert.Equal(player, npc);
    }
}
