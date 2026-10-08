using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    static FramedMovementState Committed(Scene scene, float preparation = 0, float timeout = 3)
    {
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var move = state.State;
        move.Commitment = new(1, Vector2.UnitX, 3, 4, 20, preparation, 0.1f, timeout);
        move.FacingYaw = 0.7f;
        move.JumpBufferRemaining = 0.1f;
        return new(move, state.Frame, state.Selection);
    }

    [Fact]
    public void CommittedPreparationAndLaunchIgnorePlayerSteeringAndBufferedJump()
    {
        using var scene = new Scene(floor: true);
        var command = new MoveCommand(new(0, -1), true, 2, jump: true, faceCamera: true);
        var preparing = ExplicitCharacterMovement.Step(Committed(scene, preparation: 0.2f), command,
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, preparing.Outcome);
        Assert.Equal(MovementCommitmentPhase.Preparing, preparing.State.State.Commitment.Phase);
        Assert.Equal(0, preparing.State.State.Position.X);
        Assert.Equal(0, preparing.State.State.JumpBufferRemaining);
        var launch = ExplicitCharacterMovement.Step(preparing.State, command, 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, launch.Outcome);
        Assert.Equal(MovementCommitmentPhase.Airborne, launch.State.State.Commitment.Phase);
        Assert.Equal(0.3f, launch.State.State.Position.X, 5);
        Assert.Equal(0.3f, launch.State.State.Position.Y - preparing.State.State.Position.Y, 5);
        Assert.Equal(0.7f, launch.State.State.FacingYaw);
        Assert.False(launch.State.State.Grounded);
    }

    [Fact]
    public void CommittedArcLandsAndCompletesRecoveryUnderTheSameLease()
    {
        using var scene = new Scene(floor: true);
        var state = Committed(scene);
        bool airborne = false, recovering = false, completed = false;
        for (int tick = 0; tick < 24; tick++)
        {
            var result = ExplicitCharacterMovement.Step(state, MoveCommand.Idle, 1f / 30, Tuning, Policy, scene.Lease);
            Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
            state = result.State;
            airborne |= state.State.Commitment.Phase == MovementCommitmentPhase.Airborne;
            recovering |= state.State.Commitment.Phase == MovementCommitmentPhase.Recovering;
            if (state.State.Commitment.Phase == MovementCommitmentPhase.Completed)
            {
                Assert.Equal(MovementCommitmentEndReason.Landed, state.State.Commitment.EndReason);
                completed = true;
                break;
            }
        }
        Assert.True(airborne && recovering && completed);
        Assert.True(state.State.Grounded);
        Assert.InRange(state.State.Position.X, 1, 1.4f);
    }

    [Fact]
    public void CommittedLaunchUsesLiveWallProofAndAbortsACompletelyDeniedLaunch()
    {
        using var scene = new Scene(floor: true, wall: true);
        var basis = Committed(scene);
        var move = basis.State;
        move.Position.X = 1.715f;
        var result = ExplicitCharacterMovement.Step(new(move, basis.Frame, basis.Selection), MoveCommand.Idle,
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.Equal(MovementCommitmentPhase.Aborted, result.State.State.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.Blocked, result.State.State.Commitment.EndReason);
        Assert.True(result.State.State.Position.X < 1.719f);
    }

    [Fact]
    public void CommittedTimeoutConsumesTheCommandWithoutLaunching()
    {
        using var scene = new Scene(floor: true);
        var result = ExplicitCharacterMovement.Step(Committed(scene, timeout: 0.05f),
            new(Vector2.UnitX, true, 2, jump: true), 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(MovementCommitmentEndReason.TimedOut, result.State.State.Commitment.EndReason);
        Assert.Equal(0, result.State.State.Position.X);
        Assert.True(result.State.State.Grounded);
    }

    [Fact]
    public void EnteringSurfaceWaterAbortsTheCommitmentWithoutAPlayerLaunch()
    {
        using var scene = new Scene(wet: true);
        var state = Swimmer(scene);
        var move = state.State;
        move.Commitment = new(1, Vector2.UnitX, 3, 4, 20, 0, 0, 3);
        var result = ExplicitCharacterMovement.Step(new(move, state.Frame, state.Selection),
            new(Vector2.UnitX, true, 0, jump: true), 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(MovementCommitmentEndReason.EnteredWater, result.State.State.Commitment.EndReason);
        Assert.True(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
        Assert.Equal(0, result.State.State.Position.X);
    }
    [Fact]
    public void RefusedCommittedAdvancePreservesTheOriginalLifecycleAndPose()
    {
        using var scene = new Scene(floor: true);
        var state = Committed(scene);
        var trace = scene.Environment.Acquisition.OnCoverage!;
        bool refusedAdvance = false;
        scene.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery query,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        {
            if (query.Delta.X > 0)
            {
                refusedAdvance = true;
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, scene.Lease.Identity);
            }
            return trace(query, spans, contacts);
        };
        var result = ExplicitCharacterMovement.Step(state, MoveCommand.Idle, 0.1f, Tuning, Policy, scene.Lease);
        Assert.True(refusedAdvance);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(state.State.Commitment, result.State.State.Commitment);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("direction")]
    [InlineData("timeout")]
    public void MalformedCommitmentCannotPublishAnAdvance(string field)
    {
        using var scene = new Scene(floor: true);
        var state = Committed(scene);
        var move = state.State;
        move.Commitment = field switch
        {
            "gravity" => move.Commitment with { Gravity = float.NaN },
            "direction" => move.Commitment with { Direction = Vector2.Zero },
            _ => move.Commitment with { TimeoutRemaining = -1 }
        };
        var result = ExplicitCharacterMovement.Step(new(move, state.Frame, state.Selection), MoveCommand.Idle,
            0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
    }

    [Fact]
    public void CommittedDescentIntoWaterEndsAtTheSurfaceWithoutASecondLaunch()
    {
        using var scene = new Scene(wet: true);
        var basis = scene.State(new(0, 1.2f, 0));
        var move = basis.State;
        move.VerticalVelocity = -4;
        move.HorizontalVelocity = new(3, 0);
        move.Commitment = new MovementCommitment(1, Vector2.UnitX, 3, 4, 20, 0, 0, 3)
            with
        { Phase = MovementCommitmentPhase.Airborne };
        var result = ExplicitCharacterMovement.Step(new(move, basis.Frame, basis.Selection),
            new(Vector2.UnitY, true, 1, jump: true), 0.1f, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(MovementCommitmentEndReason.EnteredWater, result.State.State.Commitment.EndReason);
        Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
        Assert.True(result.State.State.Swimming);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
    }

}
