using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The ground-item half of <see cref="TileWorldServer"/>: dropped stacks as replicated entities, spawned by a
/// game's death handler, expired by the server's own clock, and despawned by whatever taking one means to the
/// game. See <c>TileWorldServer.Actors.cs</c> for the shape this mirrors.
/// <para>A ground item is an ACTOR MINUS EVERYTHING BUT EXISTENCE: the same allocator, the same
/// <c>ShardHost.SpawnOwned</c>, the same <see cref="Transient"/> scope, and none of the movement, health or
/// combat components, because a drop is born on a tile and never does anything but sit on it and stop
/// existing. What the engine owns is the lifecycle. What an item IS, what dropping one MEANS, and the rule
/// for picking one up are all the game's: it spawns drops from its own death handler, reads them back
/// through <see cref="TryGetGroundItem"/>, validates its own proximity rule, moves the payload into its own
/// storage, and calls <see cref="DespawnGroundItem"/>.</para>
/// </summary>
public sealed partial class TileWorldServer
{
    // Live drops in SPAWN ORDER, the actor list's reasoning: every pass is authored, and this is the index
    // that survives everything the entity does not carry itself. The expiry rides beside it keyed by net id,
    // absolute in ticks, because a TTL relative to anything else would drift across a pause in Tick calls.
    readonly List<long> groundItemNetIds = new();
    readonly Dictionary<long, long> groundItemExpiry = new();
    readonly List<long> groundItemScratch = new();

    /// <summary>Raised with the new drop's net id once the entity exists and its component is on it.</summary>
    public event Action<long>? OnGroundItemSpawned;

    /// <summary>Raised when a drop's clock runs out, after the entity is gone. A despawn through
    /// <see cref="DespawnGroundItem"/> does not raise it: the caller that asked already knows.</summary>
    public event Action<long>? OnGroundItemExpired;

    /// <summary>Live drops on this server.</summary>
    public int GroundItemCount => groundItemNetIds.Count;

    /// <summary>Live drops' net ids in SPAWN ORDER. The live list: it reflects a spawn or a despawn
    /// immediately and must not be enumerated across one.</summary>
    public IReadOnlyList<long> GroundItemNetIds => groundItemNetIds;

    /// <summary>Drop spawns refused because the destination cell was already at
    /// <see cref="TileWorldServerConfig.MaxGroundItemsPerCell"/>. A healthy world never climbs this, so it is
    /// a content signal (a kill farm nobody clears) rather than a statistic.</summary>
    public long RefusedGroundItemSpawnCount { get; private set; }

    /// <summary>
    /// Drops a stack on a tile and returns its net id, or 0 when the destination cell is already at its
    /// ground-item budget.
    /// <para>Refused at the door in <see cref="SpawnActor"/>'s two shapes: a malformed placement (a plane the
    /// world does not have, a region the collision map never loaded) THROWS, because it is a caller bug, and
    /// a full cell answers 0, because the caller is normally a death handler running inside a server tick.
    /// A non-positive count throws too: a drop of nothing is a caller bug in the same class.</para>
    /// </summary>
    /// <param name="at">The tile the drop sits on. Collision is deliberately not consulted: a monster dies
    /// where it dies, walls included, and a drop the reach rules cannot serve is the TTL's business.</param>
    /// <param name="itemId">The game's item id. Opaque to the engine.</param>
    /// <param name="count">How many ride the stack. At least 1.</param>
    /// <param name="ttlTicks">Ticks until the server despawns it unprompted. At least 1.</param>
    /// <returns>The drop's net id, or 0 when the cell is full.</returns>
    public long SpawnGroundItem(TileCoord at, int itemId, int count, long ttlTicks)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "A ground item holds at least one of something: a drop of nothing is a caller bug.");
        if (ttlTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(ttlTicks), ttlTicks,
                "A ground item lives at least one tick: born expired is a caller bug.");
        // The same door a spawned state goes through, used for its throws alone: a bad plane or an unloaded
        // region must fail the caller loudly, and this is the one validator that knows both.
        ValidatePlayerState(TileMoveState.At(at, TileDirection.S));

        CellCoord target = CellCoord.FromWorld(at.X, at.Z, config.CellSize);
        if (GroundItemsIn(target) >= config.MaxGroundItemsPerCell)
        {
            RefusedGroundItemSpawnCount++;
            return 0L;
        }

        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(at.X, at.Z, netId, out CellSim cell);
        cell.World.Set(e, new TileGroundItem
        {
            ItemId = itemId,
            Count = count,
            X = at.X,
            Z = at.Z,
            Plane = at.Plane,
        });
        // The actor's scope for the actor's reason: a drop has nothing worth persisting, while an in-process
        // cell eviction and a route back should hand back the same entity under the same net id.
        cell.World.Set(e, new Transient { Scope = TransientScope.DurableOnly });
        groundItemNetIds.Add(netId);
        groundItemExpiry[netId] = TickCount + ttlTicks;
        OnGroundItemSpawned?.Invoke(netId);
        return netId;
    }

    /// <summary>A live drop's payload, by net id.</summary>
    /// <param name="netId">The drop's net id.</param>
    /// <param name="item">The drop, when the answer is true.</param>
    /// <returns>False when this server holds no ground item under that id.</returns>
    public bool TryGetGroundItem(long netId, out TileGroundItem item)
    {
        item = default;
        return groundItemExpiry.ContainsKey(netId)
            && host.TryGetOwner(netId, out CellSim cell, out Entity e)
            && cell.World.IsAlive(e)
            && cell.World.TryGet(e, out item);
    }

    /// <summary>
    /// Removes a drop and its entity: the game's pickup, and any other deliberate removal. Idempotent by
    /// answer rather than by silence, <see cref="DespawnActor"/>'s contract: the second call for one id is
    /// false, which is what a pickup racing the expiry sweep needs to see, and the race is exactly why a
    /// game's take handler moves the payload only after this answers true.
    /// </summary>
    /// <param name="netId">The drop's net id.</param>
    /// <returns>False when this server holds no ground item under that id.</returns>
    public bool DespawnGroundItem(long netId)
    {
        if (!groundItemExpiry.Remove(netId)) return false;
        groundItemNetIds.Remove(netId);
        if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
        {
            cell.UnregisterOwned(netId);
            cell.World.Despawn(e);
        }
        return true;
    }

    // The expiry sweep, run by the tick body: collect first, despawn after, because a handler on the expiry
    // event may spawn or despawn and the collection must not be enumerated across either.
    void SweepExpiredGroundItems()
    {
        if (groundItemExpiry.Count == 0) return;
        groundItemScratch.Clear();
        foreach (KeyValuePair<long, long> entry in groundItemExpiry)
            if (TickCount >= entry.Value) groundItemScratch.Add(entry.Key);
        for (int i = 0; i < groundItemScratch.Count; i++)
        {
            if (DespawnGroundItem(groundItemScratch[i]))
                OnGroundItemExpired?.Invoke(groundItemScratch[i]);
        }
    }

    // ActorsIn's walk for the drop list: rare (a death handler fires per kill), bounded, no index to keep
    // correct across handoffs.
    int GroundItemsIn(CellCoord coord)
    {
        int n = 0;
        for (int i = 0; i < groundItemNetIds.Count; i++)
            if (host.TryGetOwner(groundItemNetIds[i], out CellSim cell, out _) && cell.Coord.Equals(coord)) n++;
        return n;
    }
}
