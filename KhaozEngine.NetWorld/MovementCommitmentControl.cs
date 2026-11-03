using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>Shared queued ground controls and admission for the two authoritative float heads.</summary>
internal static class MovementCommitmentControl
{
    public static AdminCommand Steer(PlayerRef target, uint sequence, Vector2 direction, float distanceRemaining)
    {
        // Validate through the same policy used at apply, even when no actor currently matches the sequence.
        _ = GroundTravelCommitment.Steer(default, sequence, direction, distanceRemaining);
        return new()
        {
            Kind = AdminCommandKind.SteerMovementCommitment,
            Target = target,
            Sequence = sequence,
            Direction = direction,
            DistanceRemaining = distanceRemaining,
        };
    }

    public static AdminCommand End(PlayerRef target, uint sequence, MovementCommitmentStopReason reason)
    {
        _ = GroundTravelCommitment.End(default, sequence, EndReason(reason));
        return new()
        {
            Kind = AdminCommandKind.EndMovementCommitment,
            Target = target,
            Sequence = sequence,
            StopReason = reason,
        };
    }

    public static bool Begin(in AdminCommand command, int slot, ref PlayerMoveState state,
        List<MovementCommitmentResult> ends)
    {
        MovementCommitment next = command.MovementCommitment.Start(command.Sequence);
        if (command.MovementCommitment.IsGroundTravel && (!state.Move.Grounded || state.Move.Swimming
            || state.Move.WaterExcursion == WaterExcursionState.AirborneFromWater))
        {
            // Refuse this request's sequence before inspecting or replacing an existing commitment.
            ends.Add(new(slot, next.Sequence, MovementCommitmentEndReason.UnsupportedState,
                state.Absolute.Position, next.Direction));
            return false;
        }
        if (state.Move.Commitment.IsActive)
            ends.Add(Result(slot, state, MovementCommitmentEndReason.Superseded));
        state.Move.Commitment = next;
        return true;
    }

    public static bool Apply(in AdminCommand command, int slot, ref PlayerMoveState state,
        List<MovementCommitmentResult> arrivals, List<MovementCommitmentResult> ends)
    {
        MovementCommitment current = state.Move.Commitment;
        MovementCommitment next = command.Kind == AdminCommandKind.SteerMovementCommitment
            ? GroundTravelCommitment.Steer(current, command.Sequence, command.Direction, command.DistanceRemaining)
            : GroundTravelCommitment.End(current, command.Sequence, EndReason(command.StopReason));
        if (next == current) return false;
        state.Move.Commitment = next;
        if (command.Kind == AdminCommandKind.EndMovementCommitment)
        {
            if (next.EndReason == MovementCommitmentEndReason.Arrived)
                arrivals.Add(Result(slot, state, next.EndReason));
            // A terminal control precedes the step's before-state capture. Publish here, since the step may
            // clear it without a phase transition the observer can see. Recovery still ends through that observer.
            if (!next.IsActive) ends.Add(Result(slot, state, next.EndReason));
        }
        return true;
    }

    private static MovementCommitmentEndReason EndReason(MovementCommitmentStopReason reason) => reason switch
    {
        MovementCommitmentStopReason.Arrived => MovementCommitmentEndReason.Arrived,
        MovementCommitmentStopReason.TargetLost => MovementCommitmentEndReason.TargetLost,
        MovementCommitmentStopReason.NoPath => MovementCommitmentEndReason.NoPath,
        MovementCommitmentStopReason.TooLong => MovementCommitmentEndReason.TooLong,
        MovementCommitmentStopReason.UnsupportedState => MovementCommitmentEndReason.UnsupportedState,
        MovementCommitmentStopReason.Cancelled => MovementCommitmentEndReason.Aborted,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static MovementCommitmentResult Result(int slot, in PlayerMoveState state, MovementCommitmentEndReason reason) =>
        new(slot, state.Move.Commitment.Sequence, reason, state.Absolute.Position, state.Move.Commitment.Direction);
}
