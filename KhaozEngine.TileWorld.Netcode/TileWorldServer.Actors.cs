using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The actor half of <see cref="TileWorldServer"/>: how a server-owned non-player entity is built, torn down and
/// read. See the other partials for construction (<c>TileWorldServer.cs</c>), the tick order
/// (<c>TileWorldServer.Tick.cs</c>), the session lifecycle (<c>TileWorldServer.Sessions.cs</c>) and the pending
/// action resolution (<c>TileWorldServer.Actions.cs</c>).
/// <para>An actor is a PLAYER MINUS A CONNECTION, nearly literally. <see cref="SpawnActor"/> allocates from the same
/// <c>NetIdAllocator</c>, calls the same <c>ShardHost.SpawnOwned</c>, and sets the same movement components. The one
/// thing it does NOT do is <c>ShardHost.BindClient</c>, and that single omission is the whole difference: net ids
/// know nothing about connections, the interest grid indexes anything the position accessor answers for, and the
/// only place a binding is required is the VIEWER side of a serve. So nothing downstream needs a player
/// predicate.</para>
/// </summary>
public sealed partial class TileWorldServer
{
    // Live actors in SPAWN ORDER. A list rather than a set because every pass over the actors has to be authored
    // rather than incidental: a hash layout must never reach a decision, which is the rule TileActionQueue states
    // about its own dictionary. It is also the ONLY index of who is an actor that survives a cell handoff, since
    // the TileActor tag is on no replication channel and a Migrate capture therefore drops it.
    readonly List<long> actorNetIds = new();

    /// <summary>The spawner list and the actor tick, driven from this server's own tick body at step 1b. A head adds
    /// its authored spawn points here and never has to call anything per tick.</summary>
    public TileActorHost Actors { get; }

    /// <summary>Raised with the new actor's net id once the entity exists and every component is on it, so a game
    /// may attach its own there (a kind discriminator, a stat record). The mirror of <see cref="PlayerJoined"/> for
    /// something with no account and no connection.</summary>
    public event Action<long>? OnActorSpawned;

    /// <summary>Live actors on this server.</summary>
    public int ActorCount => actorNetIds.Count;

    /// <summary>Live actors' net ids in SPAWN ORDER, which is the order every actor pass runs in. The live list, so
    /// it reflects a spawn or a despawn immediately and must not be enumerated across one.</summary>
    public IReadOnlyList<long> ActorNetIds => actorNetIds;

    /// <summary>Actor spawns refused because the destination cell was already at
    /// <see cref="TileWorldServerConfig.MaxActorsPerCell"/>. A healthy world never climbs this, so it is a
    /// content signal (too many spawners authored into one region) rather than a statistic.</summary>
    public long RefusedActorSpawnCount { get; private set; }

    /// <summary>
    /// Builds a server-owned actor at <paramref name="at"/> and returns its net id, or 0 when the destination cell
    /// is full.
    /// <para>REFUSED AT THE DOOR, in the two shapes the two kinds of wrong deserve. A malformed placement (a plane
    /// the world does not have, a region the collision map never loaded, a zero max health) THROWS, exactly as
    /// <see cref="SetPlayerState"/> throws, because it is a caller bug and a stack trace is the cheapest way to
    /// find it. A FULL CELL answers 0 instead, because the caller is normally a spawner running inside a server
    /// tick and a throw there would take that tick down for every player on the server. Net ids start at 1, so 0
    /// can never be a real answer, and <see cref="RefusedActorSpawnCount"/> makes the refusal countable rather
    /// than silent.</para>
    /// <para>The actor is marked <see cref="Transient"/> at <see cref="TransientScope.DurableOnly"/>: a respawning
    /// monster has nothing worth persisting (its position is its spawner's and its health is full on spawn), so a
    /// cell capture must not carry it, while an in-process cell eviction and a route back should hand back the same
    /// entity under the same net id.</para>
    /// </summary>
    /// <param name="at">The tile to build it on.</param>
    /// <param name="spec">The numbers that go on its components.</param>
    /// <returns>The new actor's net id, or 0 when the destination cell already holds
    /// <see cref="TileWorldServerConfig.MaxActorsPerCell"/> actors.</returns>
    /// <exception cref="ArgumentException"><paramref name="at"/> is on a plane at or above
    /// <see cref="TileWorldServerConfig.PlaneCount"/>, or in a region the collision map has not loaded.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="spec"/> asks for a max health of zero.</exception>
    public long SpawnActor(TileCoord at, in TileActorSpawn spec)
    {
        if (spec.MaxHealth == 0)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.MaxHealth,
                "An actor's MaxHealth must be above zero: one at zero is dead on the tick it exists.");
        TileMoveState state = TileMoveState.At(at, spec.Facing);
        // The cadence goes on the STATE rather than into a command, so an actor stands at its definition's mode from
        // the tick it exists and the actor pass has a mode to fall back to that nothing has to keep re-stating.
        state.Mode = spec.Mode;
        // The same door a written player state comes through, and deliberately the same one: a plane the world does
        // not have and a region the map never loaded both leave an entity nobody can see and that can never step,
        // whether the entity has a connection behind it or not. A freshly placed state has no route, so the array
        // this hands back is empty and is written out for the same reason SpawnPlayer writes an empty one.
        TileDirection[] steps = ValidatePlayerState(state);

        CellCoord target = CellCoord.FromWorld(at.X, at.Z, config.CellSize);
        if (ActorsIn(target) >= config.MaxActorsPerCell)
        {
            RefusedActorSpawnCount++;
            return 0L;
        }

        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(at.X, at.Z, netId, out CellSim cell);
        cell.World.Set(e, state);
        cell.World.Set(e, new TileRouteState { Remaining = steps });
        cell.World.Set(e, new PendingTileCommand { Command = TileCommand.Continue(state.Mode) });
        cell.World.Set(e, new TileActor());
        cell.World.Set(e, new TileHealth { Current = spec.MaxHealth, Max = spec.MaxHealth });
        cell.World.Set(e, new TileCombatState { AttackTicks = spec.AttackTicks });
        cell.World.Set(e, new Transient { Scope = TransientScope.DurableOnly });
        // NO BindClient, and NO TileIdentity. The first is the whole difference between an actor and a player. The
        // second is the localization rule: a player's display name is a verified fact the connect token produced,
        // and a monster's name is PROSE the server owns no catalog for.
        actorNetIds.Add(netId);
        // The tile it was BORN on, recorded before anything else can see the actor, because this is the one place
        // that knows it without asking the world. It is what a behaviour is handed as HOME for an actor no spawner
        // built, and a home read off the actor's current tile instead is a home that moves with it.
        Actors.NoteSpawn(netId, at);
        OnActorSpawned?.Invoke(netId);
        return netId;
    }

    /// <summary>
    /// Removes an actor and its entity. Idempotent by answer rather than by silence: the second call for one id is
    /// false, which is what a caller racing a death against a despawn needs to see.
    /// <para>The ownership index is cleared BEFORE the despawn, exactly as a player's leave path does it, so a
    /// handoff pass on the same frame cannot find a dead entity through it.</para>
    /// </summary>
    /// <param name="netId">The actor's net id.</param>
    /// <returns>False when this server holds no actor under that id.</returns>
    public bool DespawnActor(long netId)
    {
        if (!actorNetIds.Remove(netId)) return false;
        Actors.Forget(netId);
        if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
        {
            cell.UnregisterOwned(netId);
            cell.World.Despawn(e);
        }
        return true;
    }

    /// <summary>
    /// One entity's authoritative state BY NET ID, route included, through
    /// <see cref="TileProtocol.AssembleMoveState"/>. Named for its main reader, and it answers for any entity the
    /// host owns with a move state, a PLAYER included, which is what the combat pass needs when a player and an
    /// actor are each other's target.
    /// <para>NEVER read <c>TileMoveState.Route</c> off the raw component instead. A freshly adopted entity carries
    /// no route on its move state, because the encoding does not have one, so a raw read makes an actor that
    /// crossed a region boundary mid walk read as ARRIVED a whole region early.</para>
    /// </summary>
    /// <param name="netId">The entity's net id.</param>
    /// <param name="state">The authoritative state, default when no cell owns the id.</param>
    public bool TryGetActorState(long netId, out TileMoveState state)
    {
        state = default;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        if (!cell.World.TryGet(e, out state)) return false;
        cell.World.TryGet(e, out TileRouteState route);
        state = TileProtocol.AssembleMoveState(state, route);
        return true;
    }

    /// <summary>One entity's health by net id, players included.</summary>
    /// <param name="netId">The entity's net id.</param>
    /// <param name="health">Its health, default when no cell owns the id or it carries none.</param>
    public bool TryGetHealth(long netId, out TileHealth health)
    {
        health = default;
        return host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.TryGet(e, out health);
    }

    /// <summary>Writes one entity's health. The engine owns health MECHANICALLY and owns none of its meaning, so
    /// this is the door a game's own skill core writes <see cref="TileHealth.Max"/> through, and the one a heal goes
    /// through. Nothing is clamped here: a caller that writes a <see cref="TileHealth.Current"/> above
    /// <see cref="TileHealth.Max"/> gets exactly that, and the wire codec clamps it on the way to a viewer.
    /// <para>A PLAYER HAS NO HEALTH UNTIL THIS IS CALLED FOR THEM, so this is not optional for a game with combat.
    /// <see cref="SpawnPlayer"/> writes no <see cref="TileHealth"/>, and a combatant carrying none is skipped by the
    /// combat pass in BOTH roles: that player cannot swing and cannot be hit, and nothing throws or notices. Call it
    /// on join, and again on a respawn. <see cref="SkippedHealthlessCombatantCount"/> is the reading that says a
    /// game forgot. An ACTOR needs no call: <see cref="SpawnActor"/> writes its spawn spec's max health.</para>
    /// </summary>
    /// <param name="netId">The entity's net id.</param>
    /// <param name="health">The health to write.</param>
    /// <returns>False when no cell owns the id.</returns>
    public bool SetHealth(long netId, in TileHealth health)
    {
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        cell.World.Set(e, health);
        return true;
    }

    /// <summary>One entity's server-only combat state by net id.</summary>
    /// <param name="netId">The entity's net id.</param>
    /// <param name="combat">Its combat state, default when no cell owns the id or it carries none.</param>
    public bool TryGetCombatState(long netId, out TileCombatState combat)
    {
        combat = default;
        return host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.TryGet(e, out combat);
    }

    // One entity's tile out of THIS TICK's combat target snapshot, which is what step 0c exists to make one answer.
    // False for an id the snapshot does not hold, which is what a dead, despawned or mid-handoff target reads as,
    // the same answer the follow acts on. Internal because it is the SNAPSHOT rather than the world: a public read
    // would be a second way to ask where something is, answering differently depending on when in the tick it was
    // called, which is exactly the drift TileEntityTargets exists to remove.
    internal bool TryGetTargetTile(long netId, out TileCoord tile)
    {
        tile = default;
        if (!combatTargets.TryGetFootprint(netId, out TileRect footprint, out int plane)) return false;
        tile = new TileCoord(footprint.X, footprint.Z, plane);
        return true;
    }

    // Writes one entity's server-only combat state. INTERNAL while its readers are, unlike the public read beside
    // it: the two writers are the leash break, which drops the damage record so a broken fight cannot be re-acquired
    // from a stale one, and the combat pass, which stamps the cooldown and the record itself. A game reaches the
    // numbers through its rules seam rather than through the component, so widening this is a decision to take when
    // something outside the assembly actually needs it.
    internal bool SetCombatState(long netId, in TileCombatState combat)
    {
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        if (!cell.World.IsAlive(e)) return false;
        cell.World.Set(e, combat);
        return true;
    }

    // Both writes are UNCONDITIONAL and both are the same fix, which is why they are one method. Neither component
    // is on a replication channel, so a Migrate capture rebuilds the entity without either and the actor falls out
    // of TileMovementSystem's three-component query. See TileActorHost's doc for why an optimisation that skipped
    // this for an idle actor would reintroduce the bug at a region boundary.
    internal bool WriteActorCommand(long netId, in TileCommand command)
    {
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return false;
        if (!cell.World.IsAlive(e)) return false;
        cell.World.Set(e, new TileActor());
        cell.World.Set(e, new PendingTileCommand { Command = command });
        return true;
    }

    // Actors currently owned by one cell. Walked rather than counted into a dictionary because a spawn is rare (a
    // spawner fires once per respawn delay) and the list is bounded by MaxActorsPerCell times the resident cells,
    // so an index would be a cache to keep correct across every handoff for no measurable saving.
    int ActorsIn(CellCoord coord)
    {
        int n = 0;
        for (int i = 0; i < actorNetIds.Count; i++)
            if (host.TryGetOwner(actorNetIds[i], out CellSim cell, out _) && cell.Coord.Equals(coord)) n++;
        return n;
    }
}
