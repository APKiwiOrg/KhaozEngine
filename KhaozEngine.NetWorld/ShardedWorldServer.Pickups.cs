using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

// IWorldPickupHost: resolving and removing a server-owned non-player entity by net id, the two halves SpawnEntity
// was missing. Split out of ShardedWorldServer.cs by concern (that file already owns the session, routing, handoff
// and serving pipeline). WorldServer.Pickups.cs is the single-world twin, and WorldPickups is the seam both drive.
public sealed partial class ShardedWorldServer
{
    /// <inheritdoc />
    /// <remarks>Resolves through the shard host's ownership index, so this finds ANY entity owned by a live cell,
    /// including one <see cref="CellPersistence"/> restored from a save. That is deliberate: a boot sweep of
    /// resurrected entities (see the persistence hazard on <see cref="WorldPickups"/>) has no other handle on them.
    /// A ghost mirrored from a neighbouring cell is not owned here and does not resolve.</remarks>
    public bool TryGetEntity(long netId, out World world, out Entity entity)
    {
        if (!IsPlayerNetId(netId) && host.TryGetOwner(netId, out CellSim cell, out Entity found) && cell.World.IsAlive(found))
        {
            world = cell.World;
            entity = found;
            return true;
        }
        world = null!;
        entity = default;
        return false;
    }

    /// <inheritdoc />
    public bool DespawnEntity(long netId)
    {
        if (IsPlayerNetId(netId)) return false;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e) || !cell.World.IsAlive(e)) return false;
        cell.UnregisterOwned(netId);   // eager: drop it from the ownership index before despawning (as OnLeave does)
        cell.World.Despawn(e);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>Straight off the shard host's grid geometry (<see cref="ShardHost.CoordFor"/>), so it answers for
    /// any coordinate in the world, including one whose cell has been evicted or was never instantiated.</remarks>
    public bool TryGetCellCoord(float x, float z, out CellCoord coord)
    {
        coord = host.CoordFor(x, z);
        return true;
    }

    // A player's entity is owned by the session layer (OnJoin / OnLeave) and is mutated through SetPlayerState, so it
    // must never be reachable through the server-owned-entity seam. Linear over the joined slots rather than a second
    // index: it is bounded by MaxPlayers and only runs on an explicit resolve or despawn, never per tick per entity.
    private bool IsPlayerNetId(long netId)
    {
        foreach (long playerNetId in netIdBySlot.Values)
            if (playerNetId == netId) return true;
        return false;
    }
}
