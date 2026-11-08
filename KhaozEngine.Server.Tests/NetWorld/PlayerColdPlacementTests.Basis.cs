using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests
{
    public static IEnumerable<object[]> ChangedBases => Enumerable.Range(0, 22).Select(i => new object[] { i });

    // #1730. Every raw carry difference and correction must take the uncertified Step path.
    [Theory]
    [MemberData(nameof(ChangedBases))]
    public void AChangedFirstBasisConsumesTheCertificateAndRevalidates(int change)
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator();
        var basis = scene.Initial();
        var changed = basis;
        switch (change)
        {
            case 0: changed.Move.Position.X = MathF.BitIncrement(basis.Move.Position.X + 0.125f); break;
            case 1: changed.Move.Position.Y = MathF.BitIncrement(basis.Move.Position.Y); break;
            case 2: changed.Move.Position.Z = 0.125f; break;
            case 3: changed.Move.VerticalVelocity = 0.125f; break;
            case 4: changed.Move.Grounded = false; break;
            case 5: changed.Move.TimeSinceGrounded = 0.125f; break;
            case 6: changed.Move.JumpBufferRemaining = 0.125f; break;
            case 7: changed.Move.Swimming = true; break;
            case 8: changed.Move.WaterExcursion = WaterExcursionState.Surface; break;
            case 9: changed.Move.SpeedScale = 0.5f; break;
            case 10: changed.Move.HorizontalVelocity = Vector2.UnitX; break;
            case 11: changed.Move.CommandedVelocity = Vector2.UnitY; break;
            case 12: changed.Move.ClimbRate = 0.125f; break;
            case 13: changed.Move.ClimbRateEwma = 0.125f; break;
            case 14: changed.Move.StepDeltaY = 0.125f; break;
            case 15: changed.Move.LandingImpactSpeed = 0.125f; break;
            case 16: changed.Move.SupportGranted = true; break;
            case 17: changed.Move.FacingYaw = 0.125f; break;
            case 18: changed.TeleportEpoch = 1; break;
            case 19: changed.Move.Position.X = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)); break;
            case 20: changed.Move.Commitment = new(1, Vector2.UnitX, 2, 3, 20, 0.125f, 0, 1); break;
            case 21: changed.FrameAnchor = new(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 0); break;
        }
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        Assert.True(read!.BasisValid);
        var actual = simulator.Step(changed, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
        var outcome = simulator.LastExplicitOutcome;
        Assert.Equal(2, work.Validations);
        Assert.Equal(0, work.Reuses);
        var expected = ReferenceStep(simulator, read, changed, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
        Assert.Equal(expected.Outcome, outcome);
        AssertExact(expected.State, actual);
        simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
        Assert.Equal(0, work.Reuses);
    }

    // The public binding overload is the pre-optimization algorithm, with no certificate input.
    static (PlayerMoveState State, MovementStepOutcome Outcome) ReferenceStep(PlayerMoveSimulator simulator,
        PlayerMoveReadScope read, PlayerMoveState basis, MoveCommand command, float dt)
    {
        MovementStepOutcome outcome = read.Outcome;
        var queries = read.Queries;
        if (read.BasisValid && queries is not null)
        {
            if (basis.FrameAnchor != new Vector2(simulator.Frame.Anchor.X, simulator.Frame.Anchor.Z) ||
                queries.Frame.Frame != simulator.Frame) outcome = MovementStepOutcome.FrameMismatch;
            else
            {
                var framed = new FramedMovementState(OrdinaryPlayerMovement.ToPhysics(basis.Move, queries.Frame), queries.Frame, null);
                var result = simulator.Binding.StepPlayer(framed, command, dt, queries, read.Boundary);
                return (new PlayerMoveState
                {
                    Move = OrdinaryPlayerMovement.FromPhysics(result.State.State, queries.Frame),
                    FrameAnchor = basis.FrameAnchor,
                    TeleportEpoch = basis.TeleportEpoch
                }, result.Outcome);
            }
        }
        basis.Move = PlayerMovementPublication.Canonicalize(MovementStepResult.HoldState(basis.Move));
        return (basis, outcome);
    }

    static void AssertKernelExact(PlayerMoveSimulator simulator, PlayerMoveReadScope read,
        PlayerMoveState basis, MoveCommand command, float dt)
    {
        if (!read.BasisValid) return;
        var queries = read.Queries!;
        var framed = new FramedMovementState(OrdinaryPlayerMovement.ToPhysics(basis.Move, queries.Frame), queries.Frame, null);
        Assert.Equal(MovementStepOutcome.Advanced,
            simulator.Binding.CertifyPlayerPlacement(framed, queries, out var certificate, read.Boundary).Outcome);
        var actual = simulator.Binding.StepPlayerWithPlacement(framed, command, dt, queries, certificate, read.Boundary);
        var expected = simulator.Binding.StepPlayer(framed, command, dt, queries, read.Boundary);
        KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.AssertEqual(expected, actual,
            KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.Policy.IncludeContinuityHandle);
    }

    static void AssertExact(PlayerMoveState expected, PlayerMoveState actual)
    {
        Assert.Equal(expected, actual);
        KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.AssertEqual(expected, actual,
            KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.Policy.IncludeContinuityHandle);
    }
}
