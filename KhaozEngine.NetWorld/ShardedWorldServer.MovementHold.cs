using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    // Held seats, keyed by slot and kept outside the ECS so the set needs nothing from a cell handoff. A held seat
    // still dequeues and acknowledges its commands, then steps on MoveCommand.Idle, so gravity and support keep
    // stepping while its input moves nothing. Edited only by the admin drain and cleared in OnLeave.
    private readonly HashSet<int> heldMovementSlots = new();

    // Leaves seen per slot, bumped by OnLeave on the host thread and read from any thread at enqueue. A slot-targeted
    // hold or release captures its slot's count and is dropped at apply once the seat has turned over, so a command
    // meant for one occupant never lands on the next. Sized to MaxPlayers, the slot range the allocator hands out.
    private readonly int[] seatTurnoverBySlot;

    /// <summary>Queues a hold on a joined player's movement commands, applied at the start of the next
    /// <see cref="Tick"/>. While held, each command the seat sends is still dequeued and acknowledged, so the client's
    /// pending commands drain, but the body steps on <see cref="Locomotion.MoveCommand.Idle"/>: walking, running and
    /// jumping do nothing while gravity and support keep stepping. The hold clears no momentum, jump timer or active
    /// movement commitment. A teleport keeps the hold. Leaving ends it. Safe from any thread. The twin of
    /// <see cref="WorldServer.HoldMovement"/>.
    /// <para>A slot target names the seat's occupant at the time of this call. When that occupant leaves before the
    /// hold applies, the hold is dropped, even if a newcomer has taken the slot. An account target is resolved when
    /// the hold applies and reaches that account on whatever slot it then holds. Either way the hold is a no-op for a
    /// player that is not joined when it applies.</para></summary>
    /// <param name="target">The player, by slot or account id.</param>
    public void HoldMovement(PlayerRef target) => EnqueueMovementHold(AdminCommandKind.HoldMovement, target);

    /// <summary>Queues the release of a movement hold, applied at the start of the next <see cref="Tick"/> in order
    /// with any queued <see cref="HoldMovement"/>. Commands that seat sends afterwards drive it again. A slot or
    /// account target resolves as it does for <see cref="HoldMovement"/>. No-op when the player is not held. Safe from
    /// any thread.</summary>
    /// <param name="target">The player, by slot or account id.</param>
    public void ReleaseMovement(PlayerRef target) => EnqueueMovementHold(AdminCommandKind.ReleaseMovement, target);

    /// <summary>True while the slot's movement is held, as of the last applied admin drain. False for an unknown or
    /// departed slot. Read it on the host thread, like <see cref="TryGetPlayerState"/>.</summary>
    public bool IsMovementHeld(int slot) => heldMovementSlots.Contains(slot);

    private void EnqueueMovementHold(AdminCommandKind kind, in PlayerRef target) =>
        admin.Enqueue(new AdminCommand { Kind = kind, Target = target, SeatTurnover = SeatTurnover(target) });

    private void ApplyMovementHold(in AdminCommand command)
    {
        if (command.Target.IsSlot && SeatTurnover(command.Target) != command.SeatTurnover) return;   // seat turned over
        int slot = ResolveSlot(command.Target);   // a joined slot, or -1
        if (slot < 0) return;
        if (command.Kind == AdminCommandKind.HoldMovement) heldMovementSlots.Add(slot);
        else heldMovementSlots.Remove(slot);
    }

    private int SeatTurnover(in PlayerRef target) =>
        target.IsSlot && (uint)target.SlotValue < (uint)seatTurnoverBySlot.Length
            ? Volatile.Read(ref seatTurnoverBySlot[target.SlotValue])
            : 0;

    private void CountSeatTurnover(int slot)
    {
        if ((uint)slot < (uint)seatTurnoverBySlot.Length) Interlocked.Increment(ref seatTurnoverBySlot[slot]);
    }
}
