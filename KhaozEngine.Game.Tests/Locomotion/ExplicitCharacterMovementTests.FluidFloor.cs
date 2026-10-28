using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void BuoyantBankContactUnderALowCeilingHoldsInsteadOfPublishingLegLag()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2,
            bankHeight: 0.15f, ceiling: 1.63f);
        var swimmer = Swimmer(scene);
        var input = new FramedMovementState(swimmer.State with { Position = new(0.95f, 0.85f, 0) },
            swimmer.Frame, swimmer.Selection);

        MovementStepResult result = ExplicitCharacterMovement.StepTowards(input, Vector2.UnitX, false,
            1f / 30, Tuning, Policy, scene.Lease);

        MoveState body = result.State.State;
        float bed = body.Position.X >= 1 ? 0.15f : -1;
        float penetration = MathF.Max(0, bed - (body.Position.Y - Tuning.CapsuleHalfHeight));
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.True(body.Position == input.State.Position,
            $"Expected held pose {input.State.Position}, got {body.Position}, penetration {penetration:R} m.");
        Assert.True(body.Position.Y - Tuning.CapsuleHalfHeight >= bed, $"penetration {penetration:R} m");
        Assert.True(body.Swimming);
        Assert.False(body.Grounded);
        Assert.False(body.SupportGranted);
        Assert.Equal(WaterExcursionState.Surface, body.WaterExcursion);
    }

    [Theory]
    [InlineData(0.6f, 0.121f, MovementStepOutcome.Advanced)]
    [InlineData(3f, 0.129f, MovementStepOutcome.Blocked)]
    public void DryBankContactRetainsPacedLegLagUnderTheSameLowCeiling(float climbSpeed, float expectedFeet,
        MovementStepOutcome outcome)
    {
        using var scene = new Scene(floor: true, floorY: 0.1f, bankHeight: 0.15f, ceiling: 1.63f);
        var input = scene.State(new(0.95f, 0.851f, 0), grounded: true);
        MoveTuning tuning = Tuning with { MaxStepClimbSpeed = climbSpeed };

        MovementStepResult result = ExplicitCharacterMovement.StepTowards(input, Vector2.UnitX, false,
            1f / 30, tuning, new(WaterTraversalMode.DryOnly), scene.Lease);

        MoveState body = result.State.State;
        float feet = body.Position.Y - tuning.CapsuleHalfHeight;
        Assert.Equal(outcome, result.Outcome);
        Assert.True(body.Grounded);
        Assert.False(body.Swimming);
        Assert.Equal(WaterExcursionState.None, body.WaterExcursion);
        Assert.Equal(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
        Assert.True(body.Position.X > 1, $"The dry body must advance onto the bank: {body.Position}.");
        Assert.True(feet < 0.15f, $"The dry placement must retain its intentional leg lag: {feet:R}.");
        Assert.InRange(feet, expectedFeet - 1e-6f, expectedFeet + 1e-6f);
        Assert.True(feet - (input.State.Position.Y - tuning.CapsuleHalfHeight) <= climbSpeed / 30);
    }
}
