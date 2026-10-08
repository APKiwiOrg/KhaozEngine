using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    static FramedMovementState Swimmer(Scene scene, float y = 0.85f)
    {
        var state = scene.State(new(0, y, 0));
        MoveState move = state.State;
        move.Swimming = true;
        move.WaterExcursion = WaterExcursionState.Surface;
        return new(move, state.Frame, state.Selection);
    }

    [Fact]
    public void DeepWaterStartsActualSwimmingAndBuoyancyThroughTheCombinedResolver()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2);
        var state = scene.State(new(0, 0.7f, 0));
        var result = ExplicitCharacterMovement.Step(state, MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.False(result.State.State.Grounded);
        Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
        Assert.True(result.State.State.Position.Y > 0.7f);
    }

    [Theory]
    [InlineData(0f, 1f, false, 3f)]
    [InlineData(0f, 1f, true, 3f)]
    [InlineData(1f, 0f, true, 3f)]
    [InlineData(0f, -1f, false, 1.95f)]
    [InlineData(0f, -1f, true, 1.95f)]
    public void SurfaceMovementUsesSwimPaceRatherThanWalkOrRun(float x, float z, bool run, float speed)
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2);
        var result = ExplicitCharacterMovement.Step(Swimmer(scene), new(new(x, z), run, 0, faceCamera: true),
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.InRange(result.State.State.CommandedVelocity.Length(), speed - 0.00001f, speed + 0.00001f);
        Assert.InRange(result.State.State.Position.Y, 0.8499f, 0.8501f);
    }

    [Theory]
    [InlineData(2f, 1f, 30)]
    [InlineData(100f, 1f, 30)]
    [InlineData(100f, 0.5f, 30)]
    [InlineData(100f, 1f, 60)]
    public void SurfaceJumpReachesItsIndependentApexAndReturnsToWater(float depth, float apex, int hz)
    {
        using var scene = new Scene(wet: true, floor: true, floorY: 1 - depth, waterDepth: depth);
        float dt = 1f / hz;
        var water = new WaterTraversalPolicy(WaterTraversalMode.SurfaceSwimmer, MoveTuning.JumpSpeedForApex(apex, 20, dt));
        var state = Swimmer(scene);
        Assert.Equal(1 - depth, scene.Lease.SampleCentreWater(new(state.State.Position, 0.25f, 0.75f,
            state.Selection!.Value.Space, null)).Interval!.Value.LowerY);
        var first = ExplicitCharacterMovement.Step(state, new(Vector2.Zero, false, 0, jump: true), dt, Tuning, water, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, first.Outcome);
        Assert.True(first.State.State.Position.Y > state.State.Position.Y);
        Assert.False(first.State.State.Swimming);
        Assert.Equal(WaterExcursionState.AirborneFromWater, first.State.State.WaterExcursion);
        float highest = first.State.State.Position.Y;
        state = first.State;
        for (int tick = 1; tick < hz; tick++)
        {
            var result = ExplicitCharacterMovement.Step(state, new(Vector2.Zero, false, 0, jump: tick == 2),
                dt, Tuning, water, scene.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            state = result.State;
            highest = Math.Max(highest, state.State.Position.Y);
        }
        Assert.InRange(highest - 0.85f, apex - 0.004f, apex + 0.0002f);
        Assert.True(state.State.Swimming);
        Assert.Equal(WaterExcursionState.Surface, state.State.WaterExcursion);
    }

    [Fact]
    public void ASubmergedPressCannotBecomeALaterBufferedLaunch()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2);
        var pressed = ExplicitCharacterMovement.Step(Swimmer(scene, 0.6f), new(Vector2.Zero, false, 0, jump: true),
            1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, pressed.Outcome);
        Assert.Equal(WaterExcursionState.Surface, pressed.State.State.WaterExcursion);
        Assert.Equal(0, pressed.State.State.JumpBufferRemaining);
        var state = pressed.State;
        for (int tick = 0; tick < 30; tick++)
        {
            var next = ExplicitCharacterMovement.Step(state, MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, next.Outcome);
            Assert.True(next.State.State.Swimming);
            Assert.Equal(WaterExcursionState.Surface, next.State.State.WaterExcursion);
            state = next.State;
        }
    }

    [Fact]
    public void ARealCeilingClipsTheWaterJumpWithoutClearingItsExcursion()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2, ceiling: 1.7f);
        var result = ExplicitCharacterMovement.Step(Swimmer(scene), new(Vector2.Zero, false, 0, jump: true),
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.Y, 0.85f, 0.95f);
        Assert.Equal(0, result.State.State.VerticalVelocity);
        Assert.Equal(WaterExcursionState.AirborneFromWater, result.State.State.WaterExcursion);
        Assert.False(result.State.State.Grounded);
    }

    [Fact]
    public void PhysicalBedContactInTheSwimBandDoesNotGrantFooting()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: 0.1f, waterDepth: 0.9f);
        var state = Swimmer(scene, 0.851f);
        for (int tick = 0; tick < 12; tick++)
        {
            var result = ExplicitCharacterMovement.Step(state, MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
            Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
            Assert.True(result.State.State.Swimming);
            Assert.False(result.State.State.Grounded);
            Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
            Assert.True(result.State.State.Position.Y >= 0.85f);
            state = result.State;
        }
    }

    [Fact]
    public void SupportedShallowWaterEndsTheExcursion()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: 0.7f, waterDepth: 0.3f);
        var result = ExplicitCharacterMovement.Step(Swimmer(scene, 1.451f), MoveCommand.Idle,
            1f / 30, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
    }
    [Fact]
    public void SwimmingUsesTheLiveWallAddedAfterTheEarlierRead()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2);
        var initial = Swimmer(scene);
        var clear = ExplicitCharacterMovement.StepTowards(initial, Vector2.UnitX, false, 1, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, clear.Outcome);
        Assert.Equal(3f, clear.State.State.Position.X);
        scene.Lease.Dispose();
        scene.Environment.Physics.AddStatic(new KhaozEngine.Physics.BoxShape(new(0.03125f, 8, 8)),
            KhaozEngine.Physics.Pose.At(new(2, 0, 0)));
        var acquired = scene.Environment.Acquisition.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var read = Assert.IsType<MovementQueryLease>(acquired.Lease);
        var blocked = ExplicitCharacterMovement.StepTowards(initial, Vector2.UnitX, false, 1, Tuning, Policy, read);
        Assert.Equal(MovementStepOutcome.Blocked, blocked.Outcome);
        Assert.InRange(blocked.State.State.Position.X, 1.70f, 1.71875f);
        Assert.True(blocked.State.State.Swimming);
    }

    [Fact]
    public void SurfaceSwimmingCanStepOntoAProvedWadingBank()
    {
        using var scene = new Scene(wet: true, floor: true, floorY: -1, waterDepth: 2, bankHeight: 0.25f);
        var result = ExplicitCharacterMovement.StepTowards(Swimmer(scene), Vector2.UnitX, true, 1, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.InRange(result.State.State.Position.X, 2.9999f, 3.0001f);
        Assert.InRange(result.State.State.Position.Y, 1.0009f, 1.0011f);
        Assert.Equal(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
    }

    [Fact]
    public void RebasedFarWorldKeepsTheDeepWaterArcIdentical()
    {
        using var origin = new Scene(wet: true, floor: true, floorY: -99, waterDepth: 100);
        using var far = new Scene(wet: true, floor: true, floorY: -99, waterDepth: 100,
            physicsOrigin: new Vector3(65536, 0, -65536));
        Assert.Equal(new Vector3(65536, 0, -65536), far.Lease.Frame.PhysicsOrigin);
        var a = Swimmer(origin);
        var b = Swimmer(far);
        var water = new WaterTraversalPolicy(WaterTraversalMode.SurfaceSwimmer,
            MoveTuning.JumpSpeedForApex(1, 20, 1f / 30));
        for (int tick = 0; tick < 30; tick++)
        {
            var command = new MoveCommand(Vector2.UnitX, true, 0, jump: tick == 0);
            var first = ExplicitCharacterMovement.Step(a, command, 1f / 30, Tuning, water, origin.Lease);
            var second = ExplicitCharacterMovement.Step(b, command, 1f / 30, Tuning, water, far.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, first.Outcome);
            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.State.State, second.State.State);
            a = first.State;
            b = second.State;
        }
    }

}
