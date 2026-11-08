using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class SupportedCapsulePrecisionTests
{
    [Fact]
    public void CoarseAnalyticSupportRefusesTheLiftAndHoldsCarriedState()
    {
        var input = Input(16777218f);
        var tuning = MoveTuning.Default with { Controller = MovementControllerKind.Contact, CapsuleHalfHeight = 0.75f };
        MoveState result = CharacterMovement.StepTowards(input, Vector2.Zero, false, 1f / 60,
            (_, _) => 16777218f, tuning, preserveSmallMagnitude: true);
        Assert.Equal(input.Position, result.Position);
        Assert.Equal(input.HorizontalVelocity, result.HorizontalVelocity);
        Assert.Equal(input.VerticalVelocity, result.VerticalVelocity);
        Assert.Equal(input.Grounded, result.Grounded);
        Assert.False(result.SupportGranted);
    }

    [Theory]
    [InlineData(0.749f, true)]
    [InlineData(0.7489999532699585f, false)]
    public void DryPublicationAdmitsTheLastHalfHeightWithinBudgetAndRefusesItsNeighbor(float half, bool admitted)
    {
        // At this ordinate the next centre is 32768.75. The two exact gaps straddle 0.001 m.
        var input = Input(32768.75f);
        var tuning = MoveTuning.Default with { Controller = MovementControllerKind.Contact, CapsuleHalfHeight = half };
        MoveState result = CharacterMovement.StepTowards(input, Vector2.Zero, false, 1f / 60,
            (_, _) => 32768f, tuning, preserveSmallMagnitude: true);
        Assert.Equal(admitted, result.SupportGranted);
        if (admitted)
        {
            Assert.Equal(32768.75f, result.Position.Y);
            double gap = (double)result.Position.Y - half - 32768;
            Assert.True(gap >= 0 && gap <= MovementQueryLease.CoverageSkinMetres);
        }
        else
        {
            Assert.Equal(input.Position, result.Position);
            Assert.Equal(input.HorizontalVelocity, result.HorizontalVelocity);
            Assert.Equal(input.VerticalVelocity, result.VerticalVelocity);
        }
    }

    [Theory]
    [InlineData(32768f, 0.749f, 0f, true, 32768.75f)]
    [InlineData(32768f, 0.7489999532699585f, 0f, false, 0f)]
    [InlineData(0f, 32768f, 0.001f, true, 32768f)]
    [InlineData(1e-30f, 32768f, 0.001f, false, 0f)]
    public void EveryCompositionChecksTheFinalDirectedResult(float feet, float half, float offset, bool admitted, float expected)
    {
        if (admitted) Assert.Equal(expected, SupportedCapsulePose.Centre(feet, half, offset));
        else Assert.Equal(MovementAvailability.Unresolved,
            Assert.Throws<ContactQueryFailure>(() => SupportedCapsulePose.Centre(feet, half, offset)).Availability);
    }

    static MoveState Input(float centre) => new()
    {
        Position = new(0, centre, 0), Grounded = true,
        HorizontalVelocity = new(0.125f, -0.25f),
    };
}
