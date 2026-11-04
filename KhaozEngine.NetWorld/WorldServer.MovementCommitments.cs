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
    private readonly List<MovementCommitmentResult> movementCommitmentArrivals = new();
    private readonly List<MovementCommitmentResult> movementCommitmentEnds = new();

    /// <summary>Raised once on physical landing, after movement settles and before that tick's snapshot pass.</summary>
    public event Action<MovementCommitmentResult>? MovementCommitmentLanded;

    /// <summary>Raised once immediately after the admin drain, before movement advances ground recovery and
    /// before snapshots, including frames without a physical step. Commands queued by handlers apply next frame.</summary>
    public event Action<MovementCommitmentResult>? MovementCommitmentArrived;

    /// <summary>Raised once after recovery, or when a commitment releases for any reason. Accepted ground arrival
    /// remains the reported reason even when recovery is interrupted.</summary>
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

    /// <inheritdoc cref="IAdminControllable.SteerMovementCommitment"/>
    public void SteerMovementCommitment(PlayerRef target, uint expectedSequence, Vector2 direction, float distanceRemaining) =>
        admin.Enqueue(MovementCommitmentControl.Steer(target, expectedSequence, direction, distanceRemaining));

    /// <inheritdoc cref="IAdminControllable.EndMovementCommitment"/>
    public void EndMovementCommitment(PlayerRef target, uint expectedSequence, MovementCommitmentStopReason reason) =>
        admin.Enqueue(MovementCommitmentControl.End(target, expectedSequence, reason));

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
        if (MovementCommitmentControl.Begin(command, slot, ref state, movementCommitmentEnds))
            StoreMovementCommitment(slot, state, command.MovementCommitment.IsGroundTravel);
    }

    private void ApplyGroundMovementCommitmentControl(in AdminCommand command)
    {
        int slot = ResolveSlot(command.Target);
        if (slot < 0 || !stateBySlot.TryGetValue(slot, out PlayerMoveState state)) return;
        if (MovementCommitmentControl.Apply(command, slot, ref state, movementCommitmentArrivals, movementCommitmentEnds))
            StoreMovementCommitment(slot, state, ground: true);
    }

    private void ApplyAbortMovementCommitment(PlayerRef target, uint expectedSequence,
        MovementCommitmentEndReason reason)
    {
        int slot = ResolveSlot(target);
        if (slot < 0 || !stateBySlot.TryGetValue(slot, out PlayerMoveState state)
            || !state.Move.Commitment.IsActive || state.Move.Commitment.Sequence != expectedSequence) return;
        QueueMovementCommitmentEnd(slot, state, reason);
        bool ground = state.Move.Commitment.Kind == MovementCommitmentKind.Ground;
        state.Move.Commitment = default;
        StoreMovementCommitment(slot, state, ground);
    }

    private void StoreMovementCommitment(int slot, in PlayerMoveState state, bool ground)
    {
        if (!ground) { SetPlayerState(slot, state); return; }
        // Ground controls edit carried state, not placement. They must persist even while an explicit
        // environment holds movement, otherwise a queued terminal event could outlive its refused state edit.
        stateBySlot[slot] = state;
        MovementComponents.Set(world, entityBySlot[slot], state);
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
        MovementCommitmentEndReason reason) => MovementCommitmentResult.From(slot, state, reason);

    private void PublishMovementCommitmentArrivals()
    {
        foreach (MovementCommitmentResult result in movementCommitmentArrivals)
            MovementCommitmentArrived?.Invoke(result);
        movementCommitmentArrivals.Clear();
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
