using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServer
{
    private int nextMovementCommitmentSequence;
    private readonly List<MovementCommitmentResult> movementCommitmentLandings = new();
    private readonly List<MovementCommitmentResult> movementCommitmentEnds = new();

    /// <summary>Raised once on physical landing, after movement settles and before that tick's snapshot pass.</summary>
    public event Action<MovementCommitmentResult>? MovementCommitmentLanded;

    /// <summary>Raised once after recovery, or when a commitment aborts for any reason.</summary>
    public event Action<MovementCommitmentResult>? MovementCommitmentEnded;

    /// <summary>Queues a committed move and returns its correlation sequence.</summary>
    public uint BeginMovementCommitment(PlayerRef target, in MovementCommitmentRequest request)
    {
        request.Validate();
        uint sequence = NextMovementCommitmentSequence();
        admin.Enqueue(new AdminCommand
        {
            Kind = AdminCommandKind.BeginMovementCommitment,
            Target = target,
            Sequence = sequence,
            MovementCommitment = request,
        });
        return sequence;
    }

    /// <summary>Queues a server-authorized abort of the target's active commitment.</summary>
    public void AbortMovementCommitment(PlayerRef target, uint expectedSequence) =>
        admin.Enqueue(new AdminCommand
        {
            Kind = AdminCommandKind.AbortMovementCommitment,
            Target = target,
            Sequence = expectedSequence,
        });

    private uint NextMovementCommitmentSequence()
    {
        uint sequence;
        do sequence = unchecked((uint)Interlocked.Increment(ref nextMovementCommitmentSequence));
        while (sequence == 0);
        return sequence;
    }

    private void ApplyBeginMovementCommitment(in AdminCommand command)
    {
        int slot = ResolveSlot(command.Target);
        if (slot < 0 || !stateBySlot.TryGetValue(slot, out PlayerMoveState state)) return;
        if (state.Move.Commitment.IsActive)
            QueueMovementCommitmentEnd(slot, state, MovementCommitmentEndReason.Superseded);
        state.Move.Commitment = command.MovementCommitment.Start(command.Sequence);
        SetPlayerState(slot, state);
    }

    private void ApplyAbortMovementCommitment(PlayerRef target, uint expectedSequence,
        MovementCommitmentEndReason reason)
    {
        int slot = ResolveSlot(target);
        if (slot < 0 || !stateBySlot.TryGetValue(slot, out PlayerMoveState state)
            || !state.Move.Commitment.IsActive || state.Move.Commitment.Sequence != expectedSequence) return;
        QueueMovementCommitmentEnd(slot, state, reason);
        state.Move.Commitment = default;
        SetPlayerState(slot, state);
    }

    private void InspectMovementCommitmentTransition(int slot, in PlayerMoveState before, in PlayerMoveState after)
    {
        MovementCommitment previous = before.Move.Commitment;
        MovementCommitment current = after.Move.Commitment;
        if (previous.Phase == current.Phase) return;
        if (previous.Phase == MovementCommitmentPhase.Airborne
            && current.Phase is MovementCommitmentPhase.Recovering or MovementCommitmentPhase.Completed)
            movementCommitmentLandings.Add(Result(slot, after, MovementCommitmentEndReason.Landed));
        if (current.Phase is MovementCommitmentPhase.Completed or MovementCommitmentPhase.Aborted)
            movementCommitmentEnds.Add(Result(slot, after, current.EndReason));
    }

    private void QueueMovementCommitmentEnd(int slot, in PlayerMoveState state, MovementCommitmentEndReason reason) =>
        movementCommitmentEnds.Add(Result(slot, state, reason));

    private static MovementCommitmentResult Result(int slot, in PlayerMoveState state,
        MovementCommitmentEndReason reason)
    {
        PlayerMoveState absolute = state.Absolute;
        MovementCommitment movement = state.Move.Commitment;
        return new MovementCommitmentResult(slot, movement.Sequence, reason, absolute.Position, movement.Direction);
    }

    private void PublishMovementCommitmentEvents()
    {
        foreach (MovementCommitmentResult result in movementCommitmentLandings)
            MovementCommitmentLanded?.Invoke(result);
        foreach (MovementCommitmentResult result in movementCommitmentEnds)
            MovementCommitmentEnded?.Invoke(result);
        movementCommitmentLandings.Clear();
        movementCommitmentEnds.Clear();
    }
}
