using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void EqualLevelAdjacentBodiesDoNotCreateAnInvisibleMovementBoundary()
    {
        using var scene = new Scene(waterRegions:
        [new Water("left", "room", new(new(-8, -4, -8), new(0, 1, 8)), 1),
         new Water("right", "room", new(new(0, -4, -8), new(8, 1, 8)), 1)]);
        var state = Swimmer(scene);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, true, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.InRange(result.State.State.Position.X, 0.2999f, 0.3001f);
    }

    [Fact]
    public void SimultaneousDifferentLevelsRefuseWithoutInventingABuoyancyPlane()
    {
        using var scene = new Scene(waterRegions:
        [new Water("left", "room", new(new(-8, -4, -8), new(0, 1, 8)), 1),
         new Water("right", "room", new(new(0, -4, -8), new(8, 1.2f, 8)), 1.2f)]);
        var state = Swimmer(scene);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
    }

    [Fact]
    public void AFreeCentreCannotAuthorizeAClosedRegionUnderTheOuterCapsule()
    {
        using var scene = new Scene(waterRegions:
        [new Water("free", "room", new(new(-8, -4, -8), new(0, 1, 8)), 1),
         new Water("closed", "room", new(new(0, -4, -8), new(8, 0.95f, 8)), 1)]);
        var state = Swimmer(scene);
        MoveState move = state.State;
        move.Position.X = -0.3f;
        state = new(move, state.Frame, state.Selection);
        var point = scene.Lease.SampleCentreWater(new(move.Position, 0.25f, 0.75f, state.Selection!.Value.Space, null));
        Assert.True(point.Interval!.Value.UpperIsFreeSurface);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, -0.26f, -0.25f);
        Assert.True(result.State.State.Swimming);
    }

    [Fact]
    public void ColdDeepWaterPlacementDerivesSwimmingBeforeAnyInput()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2, cold: true);
        var state = scene.State(new(0, 0.7f, 0));
        var result = ExplicitCharacterMovement.SettlePlacement(state, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
        Assert.False(result.State.State.Grounded);
        Assert.Equal(state.State.Position, result.State.State.Position);
    }

    [Fact]
    public void ColdSwimBandPlacementDoesNotTurnBedContactIntoFooting()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: 0.1f, waterDepth: 0.9f, cold: true);
        var state = Swimmer(scene, 0.851f);
        var result = ExplicitCharacterMovement.SettlePlacement(state, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.False(result.State.State.Grounded);
        Assert.Null(result.State.Selection!.Value.Support);
    }
    [Fact]
    public void DescendingIntoASeparatedLowerPoolSelectsItsOwnSurface()
    {
        using var scene = new Scene(floor: true, floorY: -4, waterRegions:
        [new Water("first", "room", new(new(-8, -4, -8), new(-0.5f, 1, 8)), 1),
         new Water("second", "room", new(new(0.5f, -4, -8), new(8, 0.5f, 8)), 0.5f)]);
        var state = Swimmer(scene);
        MoveState moved = state.State;
        moved.Position.X = -1;
        state = new(moved, state.Frame, state.Selection);
        var water = new WaterTraversalPolicy(WaterTraversalMode.SurfaceSwimmer,
            MoveTuning.JumpSpeedForApex(1, 20, 1f / 30));
        for (int tick = 0; tick < 45; tick++)
        {
            var result = ExplicitCharacterMovement.Step(state, new(Vector2.UnitX, true, 0, jump: tick == 0),
                1f / 30, Tuning, water, scene.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            state = result.State;
        }
        Assert.True(state.State.Swimming);
        Assert.InRange(state.State.Position.Y, 0.30f, 0.40f);
        var point = scene.Lease.SampleCentreWater(new(state.State.Position, 0.25f, 0.75f, state.Selection!.Value.Space, null));
        Assert.Equal("second", point.Domain!.Value.LocalId);
        Assert.Equal(0.5f, point.Interval!.Value.NominalSurfaceY);
    }

    [Fact]
    public void ADescendingWaterArcDoesNotRemainAirborneAfterItsBedClipsDownwardVelocity()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2);
        var state = scene.State(new(0, -0.21f, 0));
        MoveState falling = state.State;
        falling.VerticalVelocity = -4;
        falling.WaterExcursion = WaterExcursionState.AirborneFromWater;
        state = new(falling, state.Frame, state.Selection);
        var result = ExplicitCharacterMovement.Step(state, MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
        Assert.False(result.State.State.Grounded);
        Assert.True(result.State.State.Position.Y > -0.249f);
    }

}
