using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

// IWorldPickupHost: resolving and removing a server-owned non-player entity by net id, the two halves SpawnEntity
// was missing. Split out of WorldServer.cs by concern (that file already owns the session, movement and serving
// pipeline). ShardedWorldServer.Pickups.cs is the multi-cell twin, and WorldPickups is the seam both drive.
public sealed partial class WorldServer
{
    // netId -> the entity SpawnEntity created for it. The single world has no netId index of its own (the AoI grid is
    // rebuilt per tick and keyed by position), and players live in entityBySlot, so this is both the lookup AND the
    // guard that keeps the members below off player entities. Entries self-heal: a game that despawned an entity
    // straight through World drops out on the next resolve rather than lingering as a stale handle.
    private readonly Dictionary<long, Entity> spawnedEntities = new();

    /// <inheritdoc />
    /// <remarks>On the single-world server this resolves the entities <see cref="SpawnEntity"/> handed out. An entity
    /// a game spawned into <see cref="World"/> itself is not resolvable by net id here (it already holds the
    /// <see cref="Entity"/>). The sharded server resolves any owned non-player entity, including one restored by
    /// <see cref="CellPersistence"/>, because it has a real ownership index.</remarks>
    public bool TryGetEntity(long netId, out World world, out Entity entity)
    {
        if (spawnedEntities.TryGetValue(netId, out Entity found))
        {
            if (this.world.IsAlive(found))
            {
                world = this.world;
                entity = found;
                return true;
            }
            spawnedEntities.Remove(netId);   // stale handle (despawned out of band): reap it
        }
        world = null!;
        entity = default;
        return false;
    }

    /// <inheritdoc />
    public bool DespawnEntity(long netId)
    {
        if (!TryGetEntity(netId, out World w, out Entity e)) return false;
        spawnedEntities.Remove(netId);
        w.Despawn(e);
        return true;
    }
}
