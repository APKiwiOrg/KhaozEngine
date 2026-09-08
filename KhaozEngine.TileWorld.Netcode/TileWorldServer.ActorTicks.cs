using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

// One resolved actor for one actor-host tick. The CellSim and Entity stay internal because they are authority
// implementation details. TileActorHost keeps this value only across its synchronous decision callback.
readonly record struct TileActorTickAccess(CellSim Cell, Entity Entity, TileMoveState State);

public sealed partial class TileWorldServer
{
    // The actor host's one ownership lookup for a normal actor tick. The route is assembled here for the same
    // reason as TryGetActorState. Component reads below stay on this resolved world and entity.
    internal bool TryOpenActorTick(long netId, out TileActorTickAccess access)
    {
        access = default;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity entity)) return false;
        if (!cell.World.TryGet(entity, out TileMoveState state)) return false;
        cell.World.TryGet(entity, out TileRouteState route);
        access = new TileActorTickAccess(cell, entity, TileProtocol.AssembleMoveState(state, route));
        return true;
    }

    internal static bool ReadActorTickHealth(in TileActorTickAccess access, out TileHealth health)
    {
        health = default;
        return access.Cell.World.IsAlive(access.Entity) && access.Cell.World.TryGet(access.Entity, out health);
    }

    internal static bool ReadActorTickCombat(in TileActorTickAccess access, out TileCombatState combat)
    {
        combat = default;
        return access.Cell.World.IsAlive(access.Entity) && access.Cell.World.TryGet(access.Entity, out combat);
    }

    internal bool ReadCurrentActorTickCombat(long netId, in TileActorTickAccess opened,
        out TileActorTickAccess current, out TileCombatState combat)
    {
        combat = default;
        return TryCurrentActorTick(netId, opened, out current) && ReadActorTickCombat(current, out combat);
    }

    // A behaviour is caller code and may remove or hand off the actor while deciding. The ordinary path uses the
    // tick-local access with no second owner lookup. A dead, ghosted or migrating source falls back to the current
    // owner so callback-triggered lifecycle work keeps the semantics the old fresh lookup provided.
    bool TryCurrentActorTick(long netId, in TileActorTickAccess opened, out TileActorTickAccess current)
    {
        if (opened.Cell.World.IsAlive(opened.Entity)
            && !opened.Cell.World.Has<Ghost>(opened.Entity)
            && !opened.Cell.World.Has<Migrating>(opened.Entity))
        {
            current = opened;
            return true;
        }
        return TryOpenActorTick(netId, out current);
    }

    internal bool WriteActorTickCombat(long netId, in TileActorTickAccess opened, in TileCombatState combat)
    {
        if (!TryCurrentActorTick(netId, opened, out TileActorTickAccess current)) return false;
        current.Cell.World.Set(current.Entity, combat);
        return true;
    }

    internal bool WriteActorTickHealth(long netId, in TileActorTickAccess opened, in TileHealth health)
    {
        if (!TryCurrentActorTick(netId, opened, out TileActorTickAccess current)) return false;
        current.Cell.World.Set(current.Entity, health);
        return true;
    }

    internal bool WriteActorTickCommand(long netId, in TileActorTickAccess opened, in TileCommand command,
        out TileActorTickAccess current)
    {
        if (!TryCurrentActorTick(netId, opened, out current)) return false;
        actorCells[netId] = current.Cell.Coord;
        TileActorTraversalProfile profile = actorTraversalByNetId.GetValueOrDefault(netId,
            TileActorTraversalProfile.Unresolved);
        current.Cell.World.Set(current.Entity, new TileActor { TraversalProfile = profile });
        current.Cell.World.Set(current.Entity, new PendingTileCommand { Command = command });
        return true;
    }
}
