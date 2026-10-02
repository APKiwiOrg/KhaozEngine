using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class JumpApexTests
{
    [Theory]
    [InlineData(1f, 25f, 1f / 15f)]
    [InlineData(1f, 25f, 1f / 30f)]
    [InlineData(1f, 25f, 1f / 60f)]
    [InlineData(1f, 25f, 1f / 120f)]
    [InlineData(3.75f, 9.81f, 1f / 60f)]
    [InlineData(0f, 25f, 1f / 30f)]
    public void CorrectedLaunch_ReachesRequestedApexWithinFixedStepSamplingBound(
        float apexMetres, float gravity, float stepSeconds)
    {
        var tuning = MoveTuning.Default with
        {
            Gravity = gravity,
            JumpSpeed = MoveTuning.JumpSpeedForApex(apexMetres, gravity, stepSeconds),
        };

        float peak = MeasureApex(tuning, stepSeconds);

        // Nearest tick is at most dt/2 from the parabola's peak, losing at most g * dt^2 / 8.
        double samplingLoss = (double)gravity * stepSeconds * stepSeconds / 8d;
        const double roundingAllowance = 0.00002d;
        Assert.InRange((double)peak, Math.Max(0d, apexMetres - samplingLoss - roundingAllowance),
            apexMetres + roundingAllowance);
    }

    [Fact]
    public void DefaultLaunch_RemainsUncorrectedAtThirtyHertz()
    {
        Assert.Equal(9.79796f, MoveTuning.Default.JumpSpeed);
        Assert.Equal(25f, MoveTuning.Default.Gravity);
        Assert.InRange(MeasureApex(MoveTuning.Default, 1f / 30f), 1.75923f, 1.75928f);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidApex_IsRejected(float apexMetres)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MoveTuning.JumpSpeedForApex(apexMetres, 25f, 1f / 30f));
        Assert.Equal("apexMetres", error.ParamName);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidGravity_IsRejected(float gravity)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MoveTuning.JumpSpeedForApex(1f, gravity, 1f / 30f));
        Assert.Equal("gravity", error.ParamName);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidStep_IsRejected(float stepSeconds)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MoveTuning.JumpSpeedForApex(1f, 25f, stepSeconds));
        Assert.Equal("stepSeconds", error.ParamName);
    }

    [Fact]
    public void LargeFiniteInputs_DoNotOverflowIntermediateProduct()
    {
        float speed = MoveTuning.JumpSpeedForApex(1e30f, 1e20f, 1e-10f);

        Assert.InRange(speed, 1.4142134e25f, 1.4142138e25f);
    }

    [Fact]
    public void TinyPositiveInputs_DoNotUnderflowIntermediateProduct()
    {
        float speed = MoveTuning.JumpSpeedForApex(float.Epsilon, float.Epsilon, float.Epsilon);

        Assert.Equal(float.Epsilon, speed);
    }

    [Theory]
    [InlineData(float.MaxValue, float.MaxValue, 1f)]
    [InlineData(1f, float.MaxValue, 4f)]
    public void UnrepresentableLaunchSpeed_IsRejected(float apexMetres, float gravity, float stepSeconds)
    {
        Assert.Throws<OverflowException>(() => MoveTuning.JumpSpeedForApex(apexMetres, gravity, stepSeconds));
    }

    private static float MeasureApex(MoveTuning tuning, float stepSeconds)
    {
        float launchHeight = tuning.CapsuleHalfHeight;
        var state = new MoveState { Position = new Vector3(0f, launchHeight, 0f), Grounded = true };
        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        state = CharacterMovement.Step(state, jump, stepSeconds, (_, _) => 0f, tuning);
        Assert.False(state.Grounded);
        Assert.Equal(tuning.JumpSpeed, state.VerticalVelocity);
        Assert.Equal(launchHeight, state.Position.Y);

        float peak = 0f;
        for (int tick = 0; tick < 256; tick++)
        {
            state = CharacterMovement.Step(state, MoveCommand.Idle, stepSeconds, (_, _) => 0f, tuning);
            peak = MathF.Max(peak, state.Position.Y - launchHeight);
            if (state.VerticalVelocity <= 0f) return peak;
        }
        throw new Xunit.Sdk.XunitException("Jump did not reach its apex within the bounded flight.");
    }
}
