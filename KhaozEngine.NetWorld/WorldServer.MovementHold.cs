using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServer
{
    // Held seats, keyed by slot and kept outside the ECS so the set needs nothing from a cell handoff. A held seat
    // still dequeues and acknowledges its commands, then steps on MoveCommand.Idle, so gravity and support keep
    // stepping while its input moves nothing. Edited only by the admin drain and cleared in OnLeave.
    private readonly HashSet<int> heldMovementSlots = new();

    /// <summary>Queues a hold on a joined player's movement commands, applied at the start of the next
    /// <see cref="Tick"/>. While held, each command the seat sends is still dequeued and acknowledged, so the client's
    /// pending commands drain, but the body steps on <see cref="Locomotion.MoveCommand.Idle"/>: walking, running and
    /// jumping do nothing while gravity and support keep stepping. The hold clears no momentum, jump timer or active
    /// movement commitment. A teleport keeps the hold. Leaving ends it. No-op for a player that is not joined when the
    /// hold applies. Safe from any thread. The twin of <see cref="ShardedWorldServer.HoldMovement"/>.</summary>
    /// <param name="target">The player, by slot or account id.</param>
    public void HoldMovement(PlayerRef target) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.HoldMovement, Target = target });

    /// <summary>Queues the release of a movement hold, applied at the start of the next <see cref="Tick"/> in order
    /// with any queued <see cref="HoldMovement"/>. Commands that seat sends afterwards drive it again. No-op when the
    /// player is not held. Safe from any thread.</summary>
    /// <param name="target">The player, by slot or account id.</param>
    public void ReleaseMovement(PlayerRef target) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.ReleaseMovement, Target = target });

    /// <summary>True while the slot's movement is held, as of the last applied admin drain. False for an unknown or
    /// departed slot. Read it on the host thread, like <see cref="TryGetPlayerState"/>.</summary>
    public bool IsMovementHeld(int slot) => heldMovementSlots.Contains(slot);

    private void ApplyMovementHold(in AdminCommand command)
    {
        int slot = ResolveSlot(command.Target);   // a joined slot, or -1
        if (slot < 0) return;
        if (command.Kind == AdminCommandKind.HoldMovement) heldMovementSlots.Add(slot);
        else heldMovementSlots.Remove(slot);
    }
}
