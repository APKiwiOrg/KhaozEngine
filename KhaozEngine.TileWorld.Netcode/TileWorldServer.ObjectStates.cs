using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The object-state half of <see cref="TileWorldServer"/>: authored objects that have left their authored form,
/// as replicated entities, set by a game's own rules, reverted by the server's clock or by the game, and served
/// through the same interest grid and plane filter every other entity rides. See
/// <c>TileWorldServer.GroundItems.cs</c> for the shape this mirrors, step for step.
/// <para>A GROUND ITEM MINUS ITS SPAWN: the same allocator, the same <c>ShardHost.SpawnOwned</c>, the same
/// <see cref="Transient"/> scope, and one thing a drop does not need, a reverse index from the object's document
/// id to the entity's net id. A drop is identified by the net id the spawn handed back, while an object state is
/// identified by an id the DOCUMENT chose, so every call here names an object and the index turns it into an
/// entity.</para>
/// <para>Deliberately WITHOUT a per-cell budget, where a drop has
/// <see cref="TileWorldServerConfig.MaxGroundItemsPerCell"/>. A drop's population is driven by an event rate a
/// kill farm can raise without limit, so it needs a ceiling. An object state's population is bounded by the
/// world document: there is at most one state per authored object, because <see cref="SetObjectState"/> on an
/// id that already has one UPDATES it, and a cap would refuse a legitimate depletion in a dense forest for no
/// safety this shape does not already have.</para>
/// </summary>
public sealed partial class TileWorldServer
{
    // The reverse index, and the membership set: an object id is in here exactly when a state exists for it.
    // Kept beside the entity rather than derived from it, because every call names the OBJECT and the world is
    // only reachable by net id.
    readonly Dictionary<long, long> objectStateNetIdByObject = new();
    // Absolute expiry ticks, keyed by object id, holding ONLY the states that were given a clock. A state with
    // no TTL stands until the game clears it, and is simply absent here. Absolute rather than remaining, the
    // drop clock's reasoning: a countdown would drift across a pause in Tick calls.
    readonly Dictionary<long, long> objectStateExpiry = new();
    readonly List<long> objectStateScratch = new();

    /// <summary>Raised with an object's id when its clock ran out and the engine cleared the state unprompted,
    /// after the entity is gone: the stump that regrew on its own. A clear through
    /// <see cref="ClearObjectState"/> does not raise it, the drop expiry's contract: the caller that asked
    /// already knows.</summary>
    public event Action<long>? OnObjectStateExpired;

    /// <summary>How many authored objects currently carry a state on this server.</summary>
    public int ObjectStateCount => objectStateNetIdByObject.Count;

    /// <summary>
    /// Puts an object into a state, or moves the state of one already in one, and returns the entity's net id.
    /// The engine assigns <paramref name="state"/> no meaning at all: see <see cref="TileObjectState"/>.
    /// <para>Never refused for capacity, unlike <see cref="SpawnGroundItem"/>: there is one state per object and
    /// an object is authored, so the population is the document's rather than an event rate's. A malformed
    /// placement (a plane the world does not have, a region the collision map never loaded) still THROWS, and a
    /// negative TTL throws, because both are caller bugs in the class the drop door already refuses.</para>
    /// <para>Calling it again for an object that already has a state UPDATES the state in place and re-arms the
    /// clock from this tick, keeping the same entity and the same net id, so a client sees a value change rather
    /// than a despawn and a respawn. The one exception is a call naming a tile in a DIFFERENT cell, which cannot
    /// keep the entity (a cell owns its entities) and so clears and re-creates: a fresh net id, and the object
    /// is momentarily absent from a viewer's frame. An authored object does not move, so that path exists to be
    /// correct rather than to be used.</para>
    /// </summary>
    /// <param name="objectId">The authored object's <c>TileObject.Id</c>. Opaque to the engine, which never
    /// holds the document and so cannot tell a real id from one nothing placed.</param>
    /// <param name="state">What the object has become. The game's own constant.</param>
    /// <param name="at">The tile the object stands on, which is what puts the state into the interest grid and
    /// through the per-viewer plane filter. Collision is deliberately not consulted: an object stands where it
    /// is authored, walls included.</param>
    /// <param name="ttlTicks">Ticks until the engine clears the state unprompted, raising
    /// <see cref="OnObjectStateExpired"/>. 0, the default, means no clock: the state stands until the game
    /// clears it.</param>
    /// <returns>The state entity's net id.</returns>
    public long SetObjectState(long objectId, int state, TileCoord at, long ttlTicks = 0)
    {
        if (ttlTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(ttlTicks), ttlTicks,
                "A TTL runs forward or not at all: 0 is no clock, and a negative one is a caller bug.");
        // The same door a spawned state goes through, used for its throws alone: a bad plane or an unloaded
        // region must fail the caller loudly, and this is the one validator that knows both.
        ValidatePlayerState(TileMoveState.At(at, TileDirection.S));

        if (objectStateNetIdByObject.TryGetValue(objectId, out long existing))
        {
            if (TryUpdateObjectState(existing, objectId, state, at, ttlTicks)) return existing;
            // A different cell: the entity cannot follow, so it goes and a fresh one takes its place.
            ClearObjectState(objectId);
        }

        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(at.X, at.Z, netId, out CellSim cell);
        cell.World.Set(e, new TileObjectState
        {
            ObjectId = objectId,
            State = state,
            X = at.X,
            Z = at.Z,
            Plane = at.Plane,
        });
        // The drop's scope for the drop's reason: nothing here is worth persisting (the game owns what a state
        // MEANS and therefore owns saving it), while an in-process cell eviction and a route back should hand
        // back the same entity under the same net id.
        cell.World.Set(e, new Transient { Scope = TransientScope.DurableOnly });
        objectStateNetIdByObject[objectId] = netId;
        ArmObjectStateClock(objectId, ttlTicks);
        return netId;
    }

    /// <summary>The state an object is in, when it is in one.</summary>
    /// <param name="objectId">The authored object's id.</param>
    /// <param name="state">The state, when the answer is true.</param>
    /// <returns>False when the object is in its authored form, which is every object this server was never told
    /// about.</returns>
    public bool TryGetObjectState(long objectId, out int state)
    {
        state = 0;
        if (!objectStateNetIdByObject.TryGetValue(objectId, out long netId)) return false;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e) || !cell.World.IsAlive(e)) return false;
        if (!cell.World.TryGet(e, out TileObjectState held)) return false;
        state = held.State;
        return true;
    }

    /// <summary>The state entity's net id for an object, for a game that wants to read or write the component
    /// itself. False when the object carries no state.</summary>
    /// <param name="objectId">The authored object's id.</param>
    /// <param name="netId">The entity's net id, when the answer is true.</param>
    /// <returns>False when the object is in its authored form.</returns>
    public bool TryGetObjectStateNetId(long objectId, out long netId)
        => objectStateNetIdByObject.TryGetValue(objectId, out netId);

    /// <summary>
    /// Puts an object back to its authored self and removes its entity: the game's own revert, and any other
    /// deliberate clear. Idempotent by answer rather than by silence, <see cref="DespawnGroundItem"/>'s
    /// contract: the second call for one object is false, which is what a game's revert racing the TTL sweep
    /// needs to see.
    /// </summary>
    /// <param name="objectId">The authored object's id.</param>
    /// <returns>False when the object carried no state.</returns>
    public bool ClearObjectState(long objectId)
    {
        if (!objectStateNetIdByObject.Remove(objectId, out long netId)) return false;
        objectStateExpiry.Remove(objectId);
        if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
        {
            cell.UnregisterOwned(netId);
            cell.World.Despawn(e);
        }
        return true;
    }

    // The expiry sweep, run by the tick body beside the drops': collect first, clear after, because a handler on
    // the expiry event may set or clear a state and the collection must not be enumerated across either.
    void SweepExpiredObjectStates()
    {
        if (objectStateExpiry.Count == 0) return;
        objectStateScratch.Clear();
        foreach (KeyValuePair<long, long> entry in objectStateExpiry)
            if (TickCount >= entry.Value) objectStateScratch.Add(entry.Key);
        for (int i = 0; i < objectStateScratch.Count; i++)
        {
            if (ClearObjectState(objectStateScratch[i]))
                OnObjectStateExpired?.Invoke(objectStateScratch[i]);
        }
    }

    // Rewrites a live state's component in place, keeping the entity and the net id. False when the new tile
    // belongs to another cell, which is the one case the entity cannot survive: the caller then clears and
    // re-creates. Also false when the index points at an entity the world no longer holds, which a cell removal
    // could leave behind, and re-creating is the right answer there too.
    bool TryUpdateObjectState(long netId, long objectId, int state, TileCoord at, long ttlTicks)
    {
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e) || !cell.World.IsAlive(e)) return false;
        if (!cell.Coord.Equals(CellCoord.FromWorld(at.X, at.Z, config.CellSize))) return false;
        cell.World.Set(e, new TileObjectState
        {
            ObjectId = objectId,
            State = state,
            X = at.X,
            Z = at.Z,
            Plane = at.Plane,
        });
        ArmObjectStateClock(objectId, ttlTicks);
        return true;
    }

    // A TTL of 0 is not a clock of zero ticks, it is the ABSENCE of a clock, so it removes any clock a previous
    // call armed rather than expiring the state on the next sweep.
    void ArmObjectStateClock(long objectId, long ttlTicks)
    {
        if (ttlTicks <= 0) objectStateExpiry.Remove(objectId);
        else objectStateExpiry[objectId] = TickCount + ttlTicks;
    }
}
