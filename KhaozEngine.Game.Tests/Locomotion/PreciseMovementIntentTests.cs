using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class PreciseMovementIntentTests
{
    private const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (_, _) => 0f;
    private static readonly MoveTuning Tuning = new(2f, 5f, 0.75f, MathF.PI / 4f, CapsuleRadius: 0.3f);
    private static readonly MoveState Start = new() { Position = new Vector3(0f, 0.75f, 0f), Grounded = true };

    [Fact]
    public void TinyPreciseScalarIntentMatchesTheActualStep()
    {
        var cmd = new MoveCommand(new Vector2(0f, 0.0005f), true, 0f, false, false, true);
        MoveState stepped = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);
        var expected = new Vector2(0f, -0.00008333333f);

        Assert.Equal(0.0025f, stepped.CommandedSpeed, 7);
        AssertTarget(expected, new Vector2(stepped.Position.X, stepped.Position.Z), 1e-10f);
        AssertTarget(expected,
            CharacterMovement.IntendedHorizontalTargetAtSpeed(Start.Position, cmd, Dt, stepped.CommandedSpeed),
            1e-10f);
    }

    [Fact]
    public void TinyPreciseTuningIntentKeepsTheFraction()
    {
        var cmd = new MoveCommand(new Vector2(0f, 0.0005f), true, 0f, false, false, true);
        MoveState stepped = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);
        var expected = new Vector2(0f, -0.00008333333f);

        AssertTarget(expected, new Vector2(stepped.Position.X, stepped.Position.Z), 1e-10f);
        AssertTarget(expected, CharacterMovement.IntendedHorizontalTarget(Start.Position, cmd, Dt, Tuning), 1e-10f);
    }

    [Theory]
    [InlineData(0f, 0.125f, 0f, 0f, -0.020833334f)]
    [InlineData(0.3f, 0.4f, 0f, 0.05f, -0.06666667f)]
    [InlineData(0.3f, 0.4f, 1.5707964f, -0.06666667f, -0.05f)]
    public void TuningIntentMatchesFractionalGroundTravel(float x, float y, float yaw,
        float expectedX, float expectedZ)
    {
        var cmd = new MoveCommand(new Vector2(x, y), true, yaw, false, false, true);
        MoveState stepped = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);
        var expected = new Vector2(expectedX, expectedZ);

        AssertTarget(expected, new Vector2(stepped.Position.X, stepped.Position.Z));
        AssertTarget(expected, CharacterMovement.IntendedHorizontalTarget(Start.Position, cmd, Dt, Tuning));
    }

    [Fact]
    public void ExplicitActualSpeedIsNotScaledByTheFractionOrFacingTwice()
    {
        MoveTuning tuning = Tuning with { BackpedalSpeedScale = 0.5f, BackpedalAllowsRun = false };
        MoveState start = Start with { SpeedScale = 2f };
        var cmd = new MoveCommand(new Vector2(0f, -0.125f), true, 0f, false, true, true);
        MoveState stepped = CharacterMovement.Step(start, cmd, Dt, Flat, tuning);
        var expected = new Vector2(0f, 0.008333334f);

        Assert.Equal(0.25f, stepped.CommandedSpeed);
        AssertTarget(expected, new Vector2(stepped.Position.X, stepped.Position.Z));
        AssertTarget(expected,
            CharacterMovement.IntendedHorizontalTargetAtSpeed(start.Position, cmd, Dt, stepped.CommandedSpeed));
        AssertTarget(new Vector2(0f, 0.2f),
            CharacterMovement.IntendedHorizontalTargetAtSpeed(start.Position, cmd, Dt, 6f));
    }

    [Theory]
    [InlineData(0f, float.MaxValue, 0f, 0f, -0.16666667f)]
    [InlineData(float.MaxValue, float.MaxValue, 0f, 0.11785113f, -0.11785113f)]
    [InlineData(float.MaxValue, float.MaxValue, 0.7853982f, 0f, -0.16666667f)]
    public void HugeFinitePreciseIntentMatchesTheCappedStep(float x, float y, float yaw,
        float expectedX, float expectedZ)
    {
        var cmd = new MoveCommand(new Vector2(x, y), true, yaw, false, false, true);
        MoveState stepped = CharacterMovement.Step(Start, cmd, Dt, Flat, Tuning);
        var expected = new Vector2(expectedX, expectedZ);

        Assert.Equal(5f, stepped.CommandedSpeed, 5);
        AssertTarget(expected, new Vector2(stepped.Position.X, stepped.Position.Z));
        AssertTarget(expected,
            CharacterMovement.IntendedHorizontalTargetAtSpeed(Start.Position, cmd, Dt, stepped.CommandedSpeed));
        AssertTarget(expected, CharacterMovement.IntendedHorizontalTarget(Start.Position, cmd, Dt, Tuning));
    }

    [Theory]
    [InlineData(0f, 0.25f, true, true, false, 0f, -0.083333336f, 2.5f)]
    [InlineData(0.25f, 0f, true, true, false, 0.041666668f, 0f, 1.25f)]
    [InlineData(0f, -0.25f, true, true, false, 0f, 0.01f, 0.3f)]
    [InlineData(0f, -0.25f, true, true, true, 0f, 0.025f, 0.75f)]
    [InlineData(0.25f, 0f, true, false, false, 0.083333336f, 0f, 2.5f)]
    [InlineData(0.25f, 0f, false, true, false, 0.016666668f, 0f, 0.5f)]
    public void TuningIntentUsesDirectionalFractionAndEffectivePace(float x, float y, bool run,
        bool faceCamera, bool backpedalAllowsRun, float expectedX, float expectedZ, float expectedSpeed)
    {
        MoveTuning tuning = Tuning with
        {
            StrafeSpeedScale = 0.5f,
            BackpedalSpeedScale = 0.3f,
            BackpedalAllowsRun = backpedalAllowsRun,
        };
        MoveState start = Start with { SpeedScale = 2f };
        var cmd = new MoveCommand(new Vector2(x, y), run, 0f, false, faceCamera, true);
        MoveState stepped = CharacterMovement.Step(start, cmd, Dt, Flat, tuning);
        var expected = new Vector2(expectedX, expectedZ);

        Assert.Equal(expectedSpeed, stepped.CommandedSpeed, 6);
        AssertTarget(expected, new Vector2(stepped.Position.X, stepped.Position.Z));
        AssertTarget(expected, CharacterMovement.IntendedHorizontalTarget(start.Position, cmd, Dt, tuning, 2f));
        AssertTarget(expected,
            CharacterMovement.IntendedHorizontalTargetAtSpeed(start.Position, cmd, Dt, stepped.CommandedSpeed));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 1f, 0f)]
    [InlineData(1f, float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 1f, 0f)]
    [InlineData(1f, float.NegativeInfinity, 0f)]
    [InlineData(1f, 1f, float.NaN)]
    [InlineData(1f, 1f, float.PositiveInfinity)]
    public void IdleOrNonfinitePreciseIntentHoldsThePosition(float x, float y, float yaw)
    {
        MoveState start = Start with { Position = new Vector3(4f, 0.75f, 8f) };
        var cmd = new MoveCommand(new Vector2(x, y), true, yaw, false, false, true);
        MoveState stepped = CharacterMovement.Step(start, cmd, Dt, Flat, Tuning);
        var expected = new Vector2(4f, 8f);

        Assert.Equal(start.Position, stepped.Position);
        Assert.Equal(Vector2.Zero, stepped.CommandedVelocity);
        Assert.Equal(expected, CharacterMovement.IntendedHorizontalTarget(start.Position, cmd, Dt, Tuning));
        Assert.Equal(expected, CharacterMovement.IntendedHorizontalTargetAtSpeed(start.Position, cmd, Dt, 5f));
    }

    [Theory]
    [InlineData(0f, 0.125f, true, false, 4f, 6.75f, 4f, 7.25f)]
    [InlineData(3f, 4f, true, false, 4.75f, 7f, 4.45f, 7.4f)]
    [InlineData(1f, 0f, false, false, 4.5f, 8f, 4.75f, 8f)]
    [InlineData(0f, 0.0005f, true, false, 4f, 8f, 4f, 8f)]
    [InlineData(0f, 0f, true, false, 4f, 8f, 4f, 8f)]
    [InlineData(0f, -0.25f, true, true, 4f, 9.25f, 4f, 8.75f)]
    public void FalseOptInPreservesExactLegacyHelperTargets(float x, float y, bool run, bool faceCamera,
        float tuningX, float tuningZ, float scalarX, float scalarZ)
    {
        var position = new Vector3(4f, 0.75f, 8f);
        MoveTuning tuning = Tuning with { BackpedalSpeedScale = 0.3f, BackpedalAllowsRun = false };
        var retained = new MoveCommand(new Vector2(x, y), run, 0f, faceCamera: faceCamera);
        var unflagged = new MoveCommand(new Vector2(x, y), run, 0f, false, faceCamera, false);
        var tuningExpected = new Vector2(tuningX, tuningZ);
        var scalarExpected = new Vector2(scalarX, scalarZ);

        Assert.Equal(tuningExpected, CharacterMovement.IntendedHorizontalTarget(position, retained, 0.125f, tuning, 2f));
        Assert.Equal(tuningExpected, CharacterMovement.IntendedHorizontalTarget(position, unflagged, 0.125f, tuning, 2f));
        Assert.Equal(scalarExpected, CharacterMovement.IntendedHorizontalTargetAtSpeed(position, retained, 0.125f, 6f));
        Assert.Equal(scalarExpected, CharacterMovement.IntendedHorizontalTargetAtSpeed(position, unflagged, 0.125f, 6f));
    }

    private static void AssertTarget(Vector2 expected, Vector2 actual, float tolerance = 1e-7f)
    {
        Assert.True(float.IsFinite(actual.X));
        Assert.True(float.IsFinite(actual.Y));
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }
}
