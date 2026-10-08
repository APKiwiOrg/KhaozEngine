using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void AKnownFullyFloodedColdPoseIsAPlacementRefusal()
    {
        using var scene = new Scene(wet: true, closedWet: true, floor: true, floorY: -7,
            waterDepth: 8, ceiling: 0.5f, cold: true);
        var input = scene.State(new(0, -0.3f, 0));
        var result = ExplicitCharacterMovement.SettlePlacement(input, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.PlacementRefused, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
    }

    [Fact]
    public void SurfaceOnlyMotionStopsAtKnownClosedWaterEvenBelowItsCeiling()
    {
        using var scene = new Scene(floor: true, floorY: -7, ceiling: 0.5f, waterRegions:
            [new Water("closed", "room", new(new(1, -7, -8), new(8, 0.5f, 8)), 1)]);
        var input = scene.State(new(0, -0.3f, 0));
        var result = ExplicitCharacterMovement.StepTowards(input, Vector2.UnitX, false, 0.5f,
            Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 0.73f, 0.75f);
        Assert.False(result.State.State.Swimming);
        Assert.True(result.State.State.Position.Y < input.State.Position.Y);
    }

    [Fact]
    public void SequentialSeparatedLevelsAreNotSimultaneousLevelAmbiguity()
    {
        using var scene = new Scene(waterRegions:
            [new Water("first", "room", new(new(-8, -4, -8), new(-0.75f, 1, 8)), 1),
             new Water("second", "room", new(new(0.75f, -4, -8), new(8, 0.5f, 8)), 0.5f)]);
        var input = Swimmer(scene);
        var move = input.State;
        move.Position.X = -1;
        var crossed = ExplicitCharacterMovement.StepTowards(new(move, input.Frame, input.Selection),
            Vector2.UnitX, false, 1, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, crossed.Outcome);
        Assert.Equal(2, crossed.State.State.Position.X);
        Assert.Equal(WaterExcursionState.AirborneFromWater, crossed.State.State.WaterExcursion);
        var descended = ExplicitCharacterMovement.Step(crossed.State, MoveCommand.Idle, 0.2f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, descended.Outcome);
        Assert.True(descended.State.State.Swimming);
        var point = scene.Lease.SampleCentreWater(new(descended.State.State.Position, 0.25f, 0.75f,
            descended.State.Selection!.Value.Space, null));
        Assert.Equal("second", point.Domain!.Value.LocalId);
    }
}
