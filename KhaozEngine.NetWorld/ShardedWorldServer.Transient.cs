using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

// The per-entity persist opt-out (#326), by net id, so a game marks a transient server-owned entity in the same
// vocabulary it spawned it in. Split out of ShardedWorldServer.cs by concern, as ShardedWorldServer.Pickups.cs and
// ShardedWorldServer.Eviction.cs are. The marker itself is KhaozEngine.Sharding's Transient tag, honoured by
// CellSim.SnapshotOwned, which SnapshotCell drives.
public sealed partial class ShardedWorldServer
{
    /// <summary>
    /// Marks a server-owned non-player entity <see cref="Transient"/>: it is excluded from every cell snapshot from
    /// now on, so it is never saved and can never be resurrected as a husk by a restore. Idempotent. False when
    /// <paramref name="netId"/> names no entity this server owns, including when it names a player (a player is
    /// already excluded from cell snapshots and persists on its own record).
    /// <para>The mark travels with the entity, including across a cell handoff, so a transient entity that walks
    /// into another cell does not become persistable there. It does NOT retroactively clean a blob written before
    /// the mark, which is what the one-time boot sweep on <see cref="WorldPickups"/> is for.</para>
    /// <para><see cref="WorldPickups"/> marks every pickup it spawns, so a game using that seam needs none of this.
    /// Reach for it for the other transient server-owned things: a timed spawn, a wave of adds, a projectile, a
    /// temporary marker entity.</para>
    /// </summary>
    public bool MarkTransient(long netId)
    {
        if (!TryGetEntity(netId, out World world, out Entity entity)) return false;
        world.Set(entity, default(Transient));
        return true;
    }

    /// <summary>
    /// Clears the <see cref="Transient"/> mark, so the entity is persisted with its cell again from the next
    /// snapshot on. Idempotent. False when <paramref name="netId"/> names no entity this server owns.
    /// </summary>
    public bool ClearTransient(long netId)
    {
        if (!TryGetEntity(netId, out World world, out Entity entity)) return false;
        world.Remove<Transient>(entity);
        return true;
    }

    /// <summary>True while <paramref name="netId"/> names an owned entity carrying the <see cref="Transient"/> mark.
    /// False for an unmarked entity and for a net id this server does not own.</summary>
    public bool IsTransient(long netId) =>
        TryGetEntity(netId, out World world, out Entity entity) && world.Has<Transient>(entity);
}
