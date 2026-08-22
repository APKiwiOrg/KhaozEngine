using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The remaining walk as one step direction per tile, measured from the owner's CURRENT tile. Its own component
/// rather than a field of <see cref="TileMoveState"/> because it is the one part of a player's movement that not
/// everybody may see, and a component is the unit the replication channels act on.
/// <para>Registered on <see cref="KhaozEngine.Replication.ReplicationChannels.OwnerOnly"/>, so it reaches only the
/// client that owns the entity. The owner NEEDS it: a reconciliation basis carries the authoritative state a
/// replay starts from, and a basis with no route stands the player still, so every correction would stop a walk
/// the player never cancelled. An observer needs none of it, because a remote is drawn from its tile plus its step
/// fraction and would only gain a map-wide view of where everyone is heading.</para>
/// <para>Also on the Persist and Migrate channels, which is not a detail: a cell handoff at a region boundary and
/// a server restart both rebuild a player from these bytes, and dropping the route there is the same standing
/// player, arrived at by a different route.</para>
/// <para>Step DIRECTIONS rather than tiles: one byte per step instead of nine, and
/// <see cref="TileRoute.FromSteps"/> rebuilds the tiles on the receiving head with no pathfinder run and no
/// dependence on the receiver holding the same collision data.</para>
/// </summary>
public struct TileRouteState : IComponent
{
    /// <summary>Step directions from the owner's current tile to the route end. Null or empty when standing, and
    /// both spellings are accepted on write, because a defaulted struct is reachable in normal ECS use.</summary>
    public TileDirection[]? Remaining;
}
