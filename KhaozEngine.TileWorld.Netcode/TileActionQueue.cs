using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>What a pending action does on arrival. R1 ships one kind and the seam for the rest, so the kind is
/// already on the wire-free server state before a second one exists and adding one is not a shape change.</summary>
public enum TileActionKind : byte
{
    /// <summary>Raise <c>TileWorldServer.OnInteract</c> once the player stands on a reach tile of the target.</summary>
    Interact = 0,
}

/// <summary>One player's pending action, the thing an arrival is checked against.</summary>
/// <param name="Target">The object id the action runs against. An id rather than a tile, because the thing can move
/// between the click and the arrival and the walk is chasing the OBJECT.</param>
/// <param name="Kind">What to do on arrival.</param>
/// <param name="IssuedTick">The server tick the command arrived on. Kept so the resolution can order two actions
/// that come ready on the same tick and so a walk that never arrives can be aged out rather than pending
/// forever.</param>
public readonly record struct TilePendingAction(long Target, TileActionKind Kind, long IssuedTick);

/// <summary>
/// At most ONE pending action per player, OSRS style: reissuing (another click) REPLACES the pending action rather
/// than queueing behind it, so a player is never committed to an action they have visibly abandoned. A queue that
/// actually queued would make a double click run the first action after the player had already walked away from it,
/// which is the behaviour tile worlds are recognisably NOT.
/// <para>Server-side only. The client predicts MOVEMENT, never actions, because an action's outcome depends on
/// state the client does not hold (what is in the bank, whether the door is locked, whether another player took the
/// item first), so mispredicting one would show a result that never happened. The walk toward the target is
/// predicted, and this is what runs at the end of it.</para>
/// <para>Slots rather than entities, because a slot is what survives an entity being handed between shards mid
/// walk. Nothing here enumerates the dictionary, so its hash order never reaches a decision and two runtimes with
/// different hash layouts resolve the same actions in the same order.</para>
/// </summary>
public sealed class TileActionQueue
{
    readonly Dictionary<int, TilePendingAction> bySlot = new();

    /// <summary>How many players have an action pending, the cheap check a tick makes before doing any work.</summary>
    public int PendingCount => bySlot.Count;

    /// <summary>Sets, or REPLACES, the pending action for a slot. There is no failure mode and no capacity: one
    /// click per player per tick is all the command queue can deliver, so the last one on any tick is the one the
    /// player is asking for.</summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="target">The object id clicked.</param>
    /// <param name="issuedTick">The server tick the command was applied on.</param>
    public void Issue(int slot, long target, long issuedTick) =>
        bySlot[slot] = new TilePendingAction(target, TileActionKind.Interact, issuedTick);

    /// <summary>Reads the pending action WITHOUT clearing it, because the common answer is "still walking" and an
    /// action is only spent once the player actually arrives.</summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="action">The pending action, default when the slot has none.</param>
    public bool TryPeek(int slot, out TilePendingAction action) => bySlot.TryGetValue(slot, out action);

    /// <summary>Drops the pending action after it was raised or refused.</summary>
    /// <param name="slot">The player's connection slot.</param>
    public void Clear(int slot) => bySlot.Remove(slot);

    /// <summary>Drops all state for a slot, on disconnect. Identical to <see cref="Clear"/> today, named separately
    /// because a slot is a seat the next connection recycles: a lifecycle call site has to say which of the two it
    /// means, or the day this class grows per-slot state beyond one action the recycled seat inherits it.</summary>
    /// <param name="slot">The connection slot being released.</param>
    public void Forget(int slot) => bySlot.Remove(slot);
}
