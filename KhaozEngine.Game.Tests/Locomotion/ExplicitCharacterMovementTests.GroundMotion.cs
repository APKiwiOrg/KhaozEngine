using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Theory]
    [InlineData(0.04f, true)]
    [InlineData(0.1f, false)]
    public void DryCoyoteJumpUsesTheCarriedWindowWithoutInventingFooting(float sinceGround, bool launch)
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        var move = state.State;
        move.TimeSinceGrounded = sinceGround;
        var result = ExplicitCharacterMovement.Step(new(move, state.Frame, state.Selection),
            new(Vector2.Zero, false, 0, jump: true), 0.02f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(launch, result.State.State.VerticalVelocity > 0);
        Assert.False(result.State.State.Grounded);
        Assert.False(result.State.State.SupportGranted);
    }

    [Fact]
    public void DryBufferedPressLaunchesWhenProvedFootingIsReached()
    {
        using var scene = new Scene(floor: true);
        var first = ExplicitCharacterMovement.Step(scene.State(new(0, 0.9f, 0)),
            new(Vector2.Zero, false, 0, jump: true), 0.05f, Tuning, Policy, scene.Lease);
        Assert.True(first.State.State.JumpBufferRemaining > 0);
        var landed = ExplicitCharacterMovement.Step(first.State, MoveCommand.Idle, 0.05f, Tuning, Policy, scene.Lease);
        Assert.True(landed.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
        Assert.True(landed.State.State.VerticalVelocity > 0);
        Assert.False(landed.State.State.Grounded);
        Assert.Equal(0, landed.State.State.JumpBufferRemaining);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    public void OrdinaryAirControlScalesTheCommandWhenMomentumIsDisabled(float control)
    {
        using var scene = new Scene();
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 2, 0)), Vector2.UnitX, false,
            0.1f, Tuning with { AirControl = control }, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(0.4f * control, result.State.State.Position.X, 6);
        Assert.Equal(4 * control, result.State.State.CommandedVelocity.X, 6);
    }

    [Theory]
    [InlineData(false, 0f, 0f)]
    [InlineData(true, 0f, 3f)]
    [InlineData(true, 5f, 2.5f)]
    public void OrdinaryAirMomentumPreservesAndBrakesTheCarriedArc(bool momentum, float brake, float expectedSpeed)
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        var move = state.State;
        move.HorizontalVelocity = new(3, 0);
        var result = ExplicitCharacterMovement.Step(new(move, state.Frame, state.Selection), MoveCommand.Idle,
            0.1f, Tuning with { AirMomentum = momentum, AirBrakeAccel = brake }, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(expectedSpeed * 0.1f, result.State.State.Position.X, 6);
        Assert.Equal(expectedSpeed, result.State.State.HorizontalVelocity.X, 6);
        Assert.Equal(expectedSpeed, result.State.State.CommandedVelocity.X, 6);
    }

    [Fact]
    public void CarriedVelocityReflectsTheActualWallDenial()
    {
        using var scene = new Scene(wall: true);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 2, 0)), Vector2.UnitX, true,
            0.5f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.Equal(8, result.State.State.CommandedVelocity.X);
        Assert.InRange(result.State.State.HorizontalVelocity.X, 3.3f, 3.5f);
        Assert.Equal(result.State.State.Position.X / 0.5f, result.State.State.HorizontalVelocity.X, 5);
    }

    [Fact]
    public void AnUnsupportedSubmergedPressCannotBorrowDryCoyoteOrBuffer()
    {
        using var scene = new Scene(wet: true);
        var state = scene.State(new(0, 0.8f, 0));
        var move = state.State;
        move.TimeSinceGrounded = 0;
        var result = ExplicitCharacterMovement.Step(new(move, state.Frame, state.Selection),
            new(Vector2.Zero, false, 0, jump: true), 0.01f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.VerticalVelocity <= 0);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
    }
    [Theory]
    [InlineData("clock")]
    [InlineData("buffer")]
    [InlineData("momentum")]
    public void NonfiniteCarriedMotionIsRefusedBeforeMovementOrPlacement(string field)
    {
        using var scene = new Scene();
        var state = scene.State(new(0, 2, 0));
        var move = state.State;
        if (field == "clock") move.TimeSinceGrounded = float.NaN;
        if (field == "buffer") move.JumpBufferRemaining = float.PositiveInfinity;
        if (field == "momentum") move.HorizontalVelocity = new(float.NaN, 0);
        var input = new FramedMovementState(move, state.Frame, state.Selection);
        var step = ExplicitCharacterMovement.Step(input, MoveCommand.Idle, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, step.Outcome);
        Assert.Equal(state.State.Position, step.State.State.Position);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid,
            ExplicitCharacterMovement.ValidatePlacement(input, Tuning, Policy, scene.Lease).Outcome);
    }

}
