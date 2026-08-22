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
    /// Marks a server-owned non-player entity <see cref="Transient"/> at <see cref="TransientScope.Always"/>: it is
    /// excluded from every cell snapshot from now on, durable and eviction alike, so it is never saved, can never be
    /// resurrected as a husk by a restore, and does not come back when an unloaded cell is re-entered. Idempotent.
    /// False when <paramref name="netId"/> names no entity this server owns, including when it names a player (a
    /// player is already excluded from cell snapshots and persists on its own record).
    /// <para>The shipped 17.38.0 behaviour, kept as this overload so an existing call means exactly what it meant.
    /// Call <see cref="MarkTransient(long, TransientScope)"/> with
    /// <see cref="TransientScope.DurableOnly"/> for an entity that must survive an in-process unload and still never
    /// be saved.</para>
    /// <para>The mark travels with the entity, including across a cell handoff, so a transient entity that walks
    /// into another cell does not become persistable there. It does NOT retroactively clean a blob written before
    /// the mark, which is what the one-time boot sweep on <see cref="WorldPickups"/> is for.</para>
    /// <para><see cref="WorldPickups"/> marks every pickup it spawns, so a game using that seam needs none of this.
    /// Reach for it for the other transient server-owned things: a timed spawn, a wave of adds, a projectile, a
    /// temporary marker entity.</para>
    /// </summary>
    public bool MarkTransient(long netId) => MarkTransient(netId, TransientScope.Always);

    /// <summary>
    /// Marks a server-owned non-player entity <see cref="Transient"/> at <paramref name="scope"/>, which decides
    /// which cell captures leave it out: <see cref="TransientScope.Always"/> both of them, and
    /// <see cref="TransientScope.DurableOnly"/> the save alone, so the entity is never written yet an unloaded cell
    /// hands it back under the same net id when the coordinate is re-entered (#668). Idempotent, and re-marking an
    /// already-marked entity at a different scope moves it to that scope. False when <paramref name="netId"/> names
    /// no entity this server owns, including when it names a player.
    /// <para><see cref="TransientScope.DurableOnly"/> is what whole-zone agent state wants: a spawner holding one
    /// record per authored creature keyed to a net id, dormant while its cell is unloaded and expecting its entity
    /// back on the restore, but re-spawned from the authored content after a restart rather than resurrected out of
    /// a blob. Read the caveat on <see cref="TransientScope.DurableOnly"/> first: the route back reaches only as far
    /// as <see cref="CellEvictionConfig.MaxCachedSnapshots"/>.</para>
    /// </summary>
    public bool MarkTransient(long netId, TransientScope scope)
    {
        if (!TryGetEntity(netId, out World world, out Entity entity)) return false;
        world.Set(entity, new Transient { Scope = scope });
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

    /// <summary>True while <paramref name="netId"/> names an owned entity carrying the <see cref="Transient"/> mark,
    /// at ANY <see cref="TransientScope"/> (every scope is excluded from the durable save, which is what this asks).
    /// False for an unmarked entity and for a net id this server does not own. Use
    /// <see cref="TryGetTransientScope"/> to tell the two scopes apart.</summary>
    public bool IsTransient(long netId) =>
        TryGetEntity(netId, out World world, out Entity entity) && world.Has<Transient>(entity);

    /// <summary>
    /// Reads the <see cref="TransientScope"/> of a marked owned entity. False (with <paramref name="scope"/> at
    /// <see cref="TransientScope.Always"/>) when the entity carries no mark or the net id is not owned here, so a
    /// false is "not transient" rather than a scope.
    /// </summary>
    public bool TryGetTransientScope(long netId, out TransientScope scope)
    {
        scope = TransientScope.Always;
        if (!TryGetEntity(netId, out World world, out Entity entity)) return false;
        if (!world.TryGet(entity, out Transient mark)) return false;
        scope = mark.Scope;
        return true;
    }
}
