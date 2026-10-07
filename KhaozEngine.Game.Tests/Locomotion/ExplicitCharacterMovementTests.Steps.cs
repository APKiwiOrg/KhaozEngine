using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1.75f)]
    public void AReachableBankUsesItsActualRiseAndACompleteSupportedApproach(float? ceiling)
    {
        using var scene = new Scene(floor: true, bankHeight: 0.2f, ceiling: ceiling);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.4f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.InRange(result.State.State.Position.X, 1.5999f, 1.6001f);
        Assert.InRange(result.State.State.Position.Y, 0.9509f, 0.9511f);
        Assert.InRange(result.State.State.StepDeltaY, 0.1999f, 0.2001f);
        Assert.Equal(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
    }

    [Fact]
    public void ATooHighBankCannotBecomeAnImplicitTeleport()
    {
        using var scene = new Scene(floor: true, bankHeight: 0.6f);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 0.752f, 0), grounded: true),
            Vector2.UnitX, false, 0.4f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.True(result.State.State.Position.X < 1);
        Assert.Equal(0, result.State.State.StepDeltaY);
    }

    [Fact]
    public void ABlockedRiseCannotGrantTheFarSideSupport()
    {
        using var scene = new Scene(floor: true, bankHeight: 0.2f, ceiling: 1.6f);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 0.752f, 0), grounded: true),
            Vector2.UnitX, false, 0.4f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.True(result.State.State.Position.X < 1);
        Assert.NotEqual(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
    }

    [Fact]
    public void MissingMediumDuringTheStepRiseDiscardsThePreviouslyProvedWallPrefix()
    {
        using var scene = new Scene(floor: true, bankHeight: 0.2f);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var trace = scene.Environment.Acquisition.OnCoverage!;
        bool observedRise = false;
        scene.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            if (query.Body.Centre.X == 0 && query.Delta.Y > 0.1f)
            {
                observedRise = true;
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Identity);
            }
            return trace(query, spans, contacts);
        };
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.4f, Tuning, Policy, scene.Lease);
        Assert.True(observedRise);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(0, result.State.State.StepDeltaY);
    }
}
