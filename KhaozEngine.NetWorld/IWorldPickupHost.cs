using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="WorldPickups"/> drives, so the same pickup seam serves both the single-world
/// <see cref="WorldServer"/> and the multi-cell <see cref="ShardedWorldServer"/>. Both engine servers implement it
/// and a game does not write one (the same posture as <see cref="ICellPersistenceHost"/> and
/// <see cref="IWorldPersistenceHost"/>). Every member is already how the servers talk about players and server-owned
/// entities, so the seam adds no new concept to either of them.
/// </summary>
/// <remarks>
/// The four read members plus <see cref="SpawnEntity"/> are the servers' pre-existing public API verbatim. Only
/// <see cref="TryGetEntity"/> and <see cref="DespawnEntity"/> are new, and both are the missing halves of
/// <see cref="SpawnEntity"/>: without them a server-owned entity could be created by net id and then never resolved
/// or removed by net id.
/// </remarks>
public interface IWorldPickupHost
{
    /// <summary>The slots of all currently joined players. Enumerated once per <see cref="WorldPickups.Update"/> for
    /// the proximity scan.</summary>
    IReadOnlyCollection<int> JoinedSlots { get; }

    /// <summary>The net id of the player entity for a joined slot. False for an unknown slot.</summary>
    bool TryGetPlayerNetId(int slot, out long netId);

    /// <summary>The current authoritative movement state (its <see cref="PlayerMoveState.Position"/> is what the
    /// proximity test measures) for a joined slot. False for an unknown slot.</summary>
    bool TryGetPlayerState(int slot, out PlayerMoveState state);

    /// <summary>Spawns a server-owned non-player entity at world position (<paramref name="x"/>,
    /// <paramref name="z"/>) and returns its net id. See <see cref="WorldServer.SpawnEntity"/> /
    /// <see cref="ShardedWorldServer.SpawnEntity"/>.</summary>
    long SpawnEntity(float x, float z, Action<World, Entity>? configure = null);

    /// <summary>
    /// Resolves a server-owned non-player entity by net id, handing back the <see cref="World"/> that holds it (the
    /// single world, or the owning cell's world on a sharded server) so a component can be read or rewritten. False
    /// when no such entity is owned here.
    /// <para>Player entities are deliberately NOT resolvable through this: a player is owned by the session layer and
    /// is mutated through <c>SetPlayerState</c> / <c>SetPlayerDisplayName</c>, not through the entity seam.</para>
    /// </summary>
    bool TryGetEntity(long netId, out World world, out Entity entity);

    /// <summary>
    /// Despawns a server-owned non-player entity by net id, propagating to clients as a normal area-of-interest
    /// removal. False when no such entity is owned here (including when <paramref name="netId"/> is a player's, which
    /// this never touches). The counterpart to <see cref="SpawnEntity"/>.
    /// </summary>
    bool DespawnEntity(long netId);

    /// <summary>
    /// The grid cell that owns world position (<paramref name="x"/>, <paramref name="z"/>), so
    /// <see cref="WorldPickups"/> can record which cell each pickup lives in and drop its tracking when that cell is
    /// unloaded (<see cref="WorldPickups.ForgetCell"/>). Pure geometry: it answers for a coordinate whether or not a
    /// cell is instantiated there, and instantiates nothing.
    /// <para>The default implementation returns false, which is the honest answer for a host with no cell grid at
    /// all. <see cref="WorldServer"/> takes it: a single-world server never evicts anything, so its pickups have no
    /// cell to be stranded by. <see cref="ShardedWorldServer"/> overrides it.</para>
    /// </summary>
    bool TryGetCellCoord(float x, float z, out CellCoord coord)
    {
        coord = default;
        return false;
    }
}
