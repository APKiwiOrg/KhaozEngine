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
        var crossed = ExplicitCharacterMovement.Step(new(move, input.Frame, input.Selection),
            new(Vector2.UnitX, false, 0, jump: true), 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, crossed.Outcome);
        Assert.Equal(WaterExcursionState.AirborneFromWater, crossed.State.State.WaterExcursion);
        // Five short flight ticks cross the 1.5 m gap. The launch follows vy 5, 3, 1, -1, -3, -5.
        for (int tick = 0; tick < 5; tick++)
        {
            crossed = ExplicitCharacterMovement.StepTowards(crossed.State, Vector2.UnitX,
                false, 0.1f, Tuning, Policy, scene.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, crossed.Outcome);
            Assert.Equal(WaterExcursionState.AirborneFromWater, crossed.State.State.WaterExcursion);
            Assert.False(crossed.State.State.Swimming);
        }
        // Six separately rounded 3 * 0.1 s advances from X -1 give this binary32 endpoint.
        Assert.Equal(0.8000001f, crossed.State.State.Position.X);
        var descended = ExplicitCharacterMovement.StepTowards(crossed.State, Vector2.UnitX,
            false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, descended.Outcome);
        Assert.True(descended.State.State.Swimming);
        var point = scene.Lease.SampleCentreWater(new(descended.State.State.Position, 0.25f, 0.75f,
            descended.State.Selection!.Value.Space, null));
        Assert.Equal("second", point.Domain!.Value.LocalId);
    }
}
