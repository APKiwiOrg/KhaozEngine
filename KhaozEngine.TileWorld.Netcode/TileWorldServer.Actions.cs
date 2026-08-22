using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The action half of <see cref="TileWorldServer"/>: what happens to a pending click at the end of the walk it
/// started. Ordered, refused or raised, once per tick, after movement and handoff have both run. See the other
/// partials for construction (<c>TileWorldServer.cs</c>), the tick order (<c>TileWorldServer.Tick.cs</c>) and the
/// session lifecycle (<c>TileWorldServer.Sessions.cs</c>).
/// </summary>
public sealed partial class TileWorldServer
{
    // Slots with a pending action this tick, paired with the tick it was issued on, so the resolution order is
    // (IssuedTick, slot) rather than the player index's hash order. Reused, so the sort costs no allocation.
    readonly List<(long issuedTick, int slot)> actionOrder = new();
    static readonly Comparison<(long issuedTick, int slot)> OldestFirst = (a, b) =>
        a.issuedTick != b.issuedTick ? a.issuedTick.CompareTo(b.issuedTick) : a.slot.CompareTo(b.slot);

    // Resolves every pending action whose walk has ENDED. The arrival test is the state's own, not a second reach
    // computation: TileMoveSimulator routes an interact to a reach tile and keeps InteractTarget for exactly as long
    // as that walk is alive, dropping it the moment the route is replaced, cannot be rebuilt, or ends anywhere but
    // ON a reach tile of a target that still resolves (TileMoveSimulator.FaceTarget, both doors). So a route that
    // has emptied with the target still on it IS the arrival, and the same pair of fields answers the abandonment
    // and the failure without the server re-deriving anything the simulator already decided. Re-deriving it here
    // would put a SECOND copy of the reach rule on the server, and the one that disagreed would be invisible until
    // a player stood one tile off the thing they clicked, or a whole map away from it.
    //
    // The arrival TURN is the simulator's too, on the tick the route empties with a target pending, so nothing here
    // writes state.Facing. A write here would be idempotent at best and a second definition of the facing rule at
    // worst.
    //
    // Resolution order is (IssuedTick, slot), which is what TilePendingAction.IssuedTick exists for: two players
    // whose actions come ready on the same tick resolve oldest CLICK first, and the slot breaks a tie between two
    // clicks on the one tick. tickSlots is netIdBySlot's enumeration order, which is neither, and which is history
    // dependent now that disconnects recycle the dictionary's free-list entries. Two players clicking the same
    // object on the same tick is a gameplay decision, not a detail.
    void ResolveActions()
    {
        if (actions.PendingCount == 0) return;
        actionOrder.Clear();
        for (int i = 0; i < tickSlots.Count; i++)
            if (actions.TryPeek(tickSlots[i], out TilePendingAction queued))
                actionOrder.Add((queued.IssuedTick, tickSlots[i]));
        if (actionOrder.Count == 0) return;
        actionOrder.Sort(OldestFirst);

        for (int i = 0; i < actionOrder.Count; i++)
        {
            int slot = actionOrder[i].slot;
            if (!actions.TryPeek(slot, out TilePendingAction pending)) continue;
            if (!netIdBySlot.TryGetValue(slot, out long netId)) continue;
            // Read through TryGetPlayerState, never off the raw component, because the route is what the idle test
            // turns on. WithAssembledRoute is the one place that rule lives and its doc has the failure: a player
            // who crossed a region boundary mid walk reads as ARRIVED on the crossing tick and fires the action a
            // region early.
            if (!TryGetPlayerState(slot, out TileMoveState state)) continue;
            if (!state.Route.IsIdle)
            {
                // Still walking to it, which is the ordinary answer and the one this loop gives most of the time.
                if (TickCount - pending.IssuedTick <= maxActionAgeTicks) continue;
                // Past the cap: a walk this old is not converging. The case is a target that MOVES, which the
                // simulator re-paths toward on every tick it can still see, so the route never empties and the
                // arrival test is never reached. Without the cap that action outlives the session.
                Refuse(slot, pending.Target);
                continue;
            }

            // The route ended. If the target went with it, the simulator could not get there: an unreachable click,
            // a target that resolves to nothing, or a re-path that failed. That is the CannotReach case, and it is
            // the one thing the player has to be told, because their own client is still showing a pending action.
            if (state.InteractTarget != pending.Target)
            {
                Refuse(slot, pending.Target);
                continue;
            }

            actions.Clear(slot);
            if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) continue;
            if (!cell.World.TryGet(e, out TileMoveState live)) continue;

            // Cleared as the action is raised, which is the contract TileMoveState states for the field. Left set,
            // it would re-face the player at the end of every later walk that happened to end on a reach tile.
            live.InteractTarget = 0;
            cell.World.Set(e, live);
            if (pending.Kind == TileActionKind.Interact) OnInteract?.Invoke(slot, netId, pending.Target);
        }
    }

    // One refusal path for both cases, because a client cannot act on the difference: either way the action it is
    // showing as pending is gone, and the notice is what lets it stop showing it. The event is the server's own
    // half, for a game that wants to react (a log line, a queued follow-up), and it is raised BEFORE the notice so
    // a handler that kicks the player is not racing a frame the connection no longer wants.
    void Refuse(int slot, long target)
    {
        actions.Clear(slot);
        OnCannotReach?.Invoke(slot, target);
        SendNotice(slot, TileServerReason.CannotReach);
    }
}
