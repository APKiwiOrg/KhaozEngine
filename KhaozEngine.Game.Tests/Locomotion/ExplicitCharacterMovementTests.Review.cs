using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialFootingSettlementRetainsImpactEvenWhenTheCommandLaunches(bool jump)
    {
        using var scene = new Scene(floor: true);
        var input = scene.State(new(0, 0.78f, 0));
        var move = input.State;
        move.VerticalVelocity = -4;
        var result = ExplicitCharacterMovement.Step(new(move, input.Frame, input.Selection),
            new(Vector2.Zero, false, 0, jump: jump), 1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(4, result.State.State.LandingImpactSpeed);
        Assert.Equal(!jump, result.State.State.Grounded);
        var next = ExplicitCharacterMovement.Step(result.State, MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(0, next.State.State.LandingImpactSpeed);
    }

    [Fact]
    public void SubmergedBedContactDoesNotEmitAFootingLandingImpact()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: 0.1f, waterDepth: 0.9f);
        var state = Swimmer(scene, 0.88f);
        var move = state.State;
        move.VerticalVelocity = -4;
        var result = ExplicitCharacterMovement.Step(new(move, state.Frame, state.Selection),
            MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
        Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
        Assert.False(result.State.State.Grounded);
        Assert.Equal(0, result.State.State.LandingImpactSpeed);
    }

    [Theory]
    [InlineData("wade-start")]
    [InlineData("wade-end")]
    [InlineData("wade-scale")]
    [InlineData("swim-exit")]
    [InlineData("swim-rest")]
    [InlineData("buoyancy")]
    [InlineData("air-control")]
    public void InvalidWaterTuningCannotPublishAnAcceptedStep(string field)
    {
        using var scene = new Scene(floor: true, wet: true, floorY: 0.5f, waterDepth: 0.5f);
        var tuning = field switch
        {
            "wade-start" => Tuning with { WadeStartDepthFraction = float.NaN },
            "wade-end" => Tuning with { WadeEndDepthFraction = 0.1f },
            "wade-scale" => Tuning with { WadeMinSpeedScale = -0.5f, WadeEndDepthFraction = 0.3f },
            "swim-exit" => Tuning with { SwimExitDepthFraction = 0.8f },
            "swim-rest" => Tuning with { SwimSurfaceSubmersionFraction = 1.1f },
            "buoyancy" => Tuning with { SwimBuoyancyStiffness = -1 },
            _ => Tuning with { AirControl = 1.1f }
        };
        var state = scene.State(new(0, 1.252f, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
    }
}
