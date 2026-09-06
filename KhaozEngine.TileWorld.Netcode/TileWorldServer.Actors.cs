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
    // netId -> the cell each actor was last SEEN owned by. Written at the spawn door and refreshed by
    // WriteActorCommand, which is already unconditional every tick for every live actor and already holds the cell,
    // so the entry costs one dictionary store on a path that does two component writes. What it is FOR is the one
    // question ShardHost cannot answer: a cell eviction removes the CellSim outright, so an entity frozen in that
    // cell is owned by nothing the host holds and TryGetOwner reports it gone. Materialising the coordinate is what
    // runs a head's restore hook and hands the entity back, and this is the only record of WHICH coordinate. It is
    // a coordinate rather than a CellSim for the reason wiredCells is: a cell is a different object each time it
    // comes back. One tick stale after a crossing, which only matters for an actor whose NEW cell was evicted
    // between the two, and there the resolve simply falls through as it did before.
    readonly Dictionary<long, CellCoord> actorCells = new();
    // netId -> the registered topology selected at spawn. This server-owned index survives a region handoff and
    // lets the actor pass restore the server-only tag before movement.
    readonly Dictionary<long, TileActorTraversalProfile> actorTraversalByNetId = new();

    /// <summary>The spawner list and the actor tick, driven from this server's own tick body at step 1b. A head adds
    /// its authored spawn points here and never has to call anything per tick.</summary>
    public TileActorHost Actors { get; }

    /// <summary>Raised with the new actor's net id once the entity exists and every component is on it, so a game
    /// may attach its own there (a kind discriminator, a stat record). The mirror of <see cref="PlayerJoined"/> for
    /// something with no account and no connection.
    /// <para>THE SPAWNER IS ALREADY LINKED when this fires, so <see cref="TileActorHost.TryGetSpawnerOf"/> answers
    /// inside the handler and its <see cref="TileActorSpawner.Definition"/> is what says which kind of actor this
    /// is. That is the whole reason a game attaches a component here rather than on a later tick, and it is why the
    /// link is written from inside the spawn instead of after it returns. An actor a head built through
    /// <see cref="SpawnActor"/> itself has no spawner and answers false, which is the caller who already has the
    /// spec in hand.</para></summary>
    public event Action<long>? OnActorSpawned;

    /// <summary>Live actors on this server.</summary>
    public int ActorCount => actorNetIds.Count;

    // The actor simulator's pathfinder window, which is the ceiling a definition's LeashRadius is authored against:
    // a leash longer than the window is a walk home the pathfinder cannot plan in one go. Internal because the
    // reader is TileActorHost.Add, which is the one door a definition comes through and the only place both the
    // definition and this server's config are in hand at once.
    internal int ActorPathRadius => config.ActorMove.MaxPathRadius;

    internal void RegisterActorTraversalProfile(TileActorTraversalProfile profile, TileCollisionMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.PlaneCount != config.PlaneCount)
            throw new ArgumentException(
                $"Actor traversal map plane count {map.PlaneCount} does not match the server's {config.PlaneCount} planes.",
                nameof(map));
        var actorSimulator = new TileMoveSimulator(map, config.StepTicks, interactionTargets, config.ActorMove,
            combatTargets);
        actorTraversalProfiles.Register(profile, map, actorSimulator);
    }

    internal bool TryGetActorTraversal(TileActorTraversalProfile profile, out TileCollisionMap map)
    {
        if (actorTraversalProfiles.TryGet(profile, out TileActorTraversalEntry entry))
        {
            map = entry.Map;
            return true;
        }
        map = null!;
        return false;
    }

    internal TileActorTraversalProfile ActorTraversalProfileOf(long netId) =>
        actorTraversalByNetId.GetValueOrDefault(netId, TileActorTraversalProfile.Unresolved);

    // The map an entity's movement uses. Players always use the constructor map. A live actor whose server index
    // is unexpectedly missing gets no answer, which keeps combat from falling back after movement already froze.
    internal bool TryGetMoverTraversalMap(long netId, out TileCollisionMap map)
    {
        if (actorTraversalByNetId.TryGetValue(netId, out TileActorTraversalProfile profile))
            return TryGetActorTraversal(profile, out map);
        if (actorNetIds.Contains(netId))
        {
            map = null!;
            return false;
        }
        map = simulator.Map;
        return true;
    }

    internal bool IsActorTraversalPlacementBlocked(TileActorTraversalProfile profile, TileCoord at)
    {
        if (profile == TileActorTraversalProfile.Default) return false;
        return !TryGetActorTraversal(profile, out TileCollisionMap map)
            || !map.HasRegion(at.Region)
            || TileCollision.IsBlocked(map, at.X, at.Z, at.Plane);
    }

    internal void ValidateActorTraversalPlacement(TileActorTraversalProfile profile, TileCoord at,
        string parameterName)
    {
        if (!TryGetActorTraversal(profile, out _))
            throw new ArgumentException($"Actor traversal profile {profile.Value} is not registered.", parameterName);
        if (IsActorTraversalPlacementBlocked(profile, at))
            throw new ArgumentException(
                $"Actor traversal profile {profile.Value} blocks the spawn tile {at}.", parameterName);
    }

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
    /// <param name="spec">The numbers that go on its components and its registered traversal profile.</param>
    /// <returns>The new actor's net id, or 0 when the destination cell already holds
    /// <see cref="TileWorldServerConfig.MaxActorsPerCell"/> actors.</returns>
    /// <exception cref="ArgumentException"><paramref name="at"/> is on a plane at or above
    /// <see cref="TileWorldServerConfig.PlaneCount"/>, or in a region the collision map has not loaded. Also thrown
    /// when the spawn names an unregistered profile or a non-default profile blocks the spawn tile. The default
    /// profile retains the legacy blocked-home rule.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="spec"/> asks for a max health of zero.</exception>
    public long SpawnActor(TileCoord at, in TileActorSpawn spec) => SpawnActorFrom(at, spec, null);

    // The same door with the SPAWNER the actor came from, so the host's index is written before OnActorSpawned
    // fires. Internal because a spawner link is the host's to make: a caller handing one in here could file an
    // actor under a spawner that never built it, and there is nothing a game can do with that a plain SpawnActor
    // plus its own bookkeeping cannot. Named apart from the public overload rather than overloading it, because
    // every cref naming SpawnActor across this package would otherwise be ambiguous.
    internal long SpawnActorFrom(TileCoord at, in TileActorSpawn spec, TileActorSpawner? spawner)
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
        ValidateActorTraversalPlacement(spec.TraversalProfile, at, nameof(spec));

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
        cell.World.Set(e, new TileActor { TraversalProfile = spec.TraversalProfile });
        cell.World.Set(e, new TileHealth { Current = spec.MaxHealth, Max = spec.MaxHealth });
        cell.World.Set(e, new TileCombatState { AttackTicks = spec.AttackTicks });
        cell.World.Set(e, new Transient { Scope = TransientScope.DurableOnly });
        // NO BindClient, and NO TileIdentity. The first is the whole difference between an actor and a player. The
        // second is the localization rule: a player's display name is a verified fact the connect token produced,
        // and a monster's name is PROSE the server owns no catalog for.
        actorNetIds.Add(netId);
        actorCells[netId] = cell.Coord;
        actorTraversalByNetId[netId] = spec.TraversalProfile;
        // The tile it was BORN on, recorded before anything else can see the actor, because this is the one place
        // that knows it without asking the world. It is what a behaviour is handed as HOME for an actor no spawner
        // built, and a home read off the actor's current tile instead is a home that moves with it.
        Actors.NoteSpawn(netId, at);
        // The spawner link goes in BEFORE the event, because the event's whole job is to be the place a game
        // attaches its own components and the only thing that says WHICH kind of actor this is is the spawner's
        // definition. Raised first, the handler asks TryGetSpawnerOf and is told false, silently, on every spawn.
        if (spawner is not null) Actors.LinkSpawner(netId, spawner);
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
        bool owned = TryResolveActor(netId, out CellSim cell, out Entity e);
        actorCells.Remove(netId);
        actorTraversalByNetId.Remove(netId);
        if (owned && cell.World.IsAlive(e))
        {
            cell.UnregisterOwned(netId);
            cell.World.Despawn(e);
        }
        return true;
    }

    // TryGetOwner, plus the cell this actor was last SEEN in materialised first when nothing answers. An entity
    // frozen in an evicted cell is owned by no live cell at all, so the host reports it gone and a despawn that
    // stopped there left it in the freeze to come back on the next visit to that coordinate as an entity nothing
    // on this server indexes. Instantiating the coordinate is what runs whatever restore a head wired to
    // CellCreated, and that is the only path this package has to a freeze it cannot see: cell persistence is the
    // head's, and TileWorld.Netcode is a sibling of the stack that owns an evictor rather than a dependent of it.
    // Skipped when the coordinate IS live, so an ordinary despawn is exactly the lookup it always was and a miss
    // there still means the entity is genuinely gone rather than frozen.
    bool TryResolveActor(long netId, out CellSim cell, out Entity entity)
    {
        if (host.TryGetOwner(netId, out cell, out entity)) return true;
        if (!actorCells.TryGetValue(netId, out CellCoord last) || host.TryGetCell(last, out _)) return false;
        host.EnsureCell(last);
        return host.TryGetOwner(netId, out cell, out entity);
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

    /// <summary>Drops one entity's damage record, and ONLY when it names <paramref name="attacker"/>. Returns false
    /// and writes nothing when no cell owns the id, when it carries no combat state, or when the record names
    /// somebody else.
    /// <para>WHAT A GAME USES IT FOR is a death it owns. The engine's own half of a death clears the DEAD entity's
    /// target and nothing else, and what lets it stop there is the reap: every other lock naming a corpse stops
    /// resolving the moment the entity leaves the world. A PLAYER is never reaped, so a game that answers
    /// <see cref="OnDied"/> by moving the body owns both halves of the killer's break. The LOCK has a public idiom
    /// already, which is to latch a command through <see cref="TileActorHost.Command"/> and let the one stepper clear
    /// it, the same idiom the leash break uses. This is the other half: nothing else ages
    /// <see cref="TileCombatState.LastDamagedBy"/>, so a retaliating behaviour reads it on the first tick the actor
    /// holds no target and hands the same victim straight back.</para>
    /// <para>TARGETED RATHER THAN A CLEAR, which is what separates it from the leash break's own forget. A death
    /// ends ONE fight, so a grudge against a third party who was also hitting this actor has to survive it, or
    /// first-attacker-wins quietly becomes whoever-died-last-wins.</para>
    /// <para>SAFE FROM <see cref="OnDied"/>, which is the intra-tick ordering the use above rests on: the combat pass
    /// stamps every damage record while it APPLIES the tick's swings and raises <see cref="OnDied"/> only after every
    /// one of them has landed, so a record dropped from that handler cannot be re-stamped on the same tick. Calling
    /// it from <see cref="OnCombatEvent"/> instead is calling it from INSIDE that apply phase, where a later swing of
    /// the same tick stamps the record straight back.</para>
    /// <para>NOT DONE UNPROMPTED AT A DEATH, because the engine does not know what a death MEANS for a body it did
    /// not remove. Where that body goes is the game's answer (a spawn, a hospital, a revive where it fell), and a
    /// game whose death is a knockdown is still in the fight it was in. An automatic drop would also be
    /// unrecoverable, since a record that is gone cannot be put back.</para>
    /// </summary>
    /// <param name="netId">The entity that forgets.</param>
    /// <param name="attacker">The net id its record has to name for the record to be dropped. Zero drops nothing,
    /// because zero is what an empty record already reads as.</param>
    public bool ForgetAttacker(long netId, long attacker)
    {
        if (attacker == 0L) return false;
        if (!TryGetCombatState(netId, out TileCombatState combat)) return false;
        // Each record is dropped only when it names THIS opponent, independently: a third party's landed hit
        // survives an opponent's death exactly as before, and since the swing-aggro ruling the SWUNG-AT record
        // is the one the retaliation actually reads, so leaving it standing here would hand the killer its
        // victim back through the new record instead of the old one.
        bool dropped = false;
        if (combat.LastDamagedBy == attacker)
        {
            combat.LastDamagedBy = 0L;
            combat.LastDamagedTick = 0L;
            dropped = true;
        }
        if (combat.LastAttackedBy == attacker)
        {
            combat.LastAttackedBy = 0L;
            combat.LastAttackedTick = 0L;
            dropped = true;
        }
        return dropped && SetCombatState(netId, combat);
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

    // Who is locked onto this entity, out of the SAME tick's snapshot the target tiles come from, 0 when nobody
    // is. Internal for the same reason TryGetTargetTile is: it is the snapshot rather than the world, and the one
    // consumer is the actor decision pass, which hands it to behaviours as TileActorContext.TargetedBy.
    internal long TargetedByOf(long netId) => combatTargets.TargetedBy(netId);

    // Writes one entity's server-only combat state. INTERNAL while its readers are, unlike the public read beside
    // it: the writers are the leash break, which drops the damage record so a broken fight cannot be re-acquired
    // from a stale one, the combat pass, which stamps the cooldown and the record itself, and the public
    // ForgetAttacker above, which is the same drop for a fight a GAME ended. A game reaches the numbers through its
    // rules seam rather than through the component, and it reaches the one field it has to be able to drop through
    // that door, so this stays shut until something outside the assembly needs to write a number here.
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
        // The last-seen cell, refreshed here because this is the one pass that is documented to run for every live
        // actor on every tick and it already has the cell in hand.
        actorCells[netId] = cell.Coord;
        TileActorTraversalProfile profile = actorTraversalByNetId.GetValueOrDefault(netId,
            TileActorTraversalProfile.Unresolved);
        cell.World.Set(e, new TileActor { TraversalProfile = profile });
        cell.World.Set(e, new PendingTileCommand { Command = command });
        return true;
    }

    // Actors currently owned by one cell. Walked rather than counted into a dictionary because a spawn is rare (a
    // spawner fires once per respawn delay) and the list is bounded by MaxActorsPerCell times the resident cells,
    // so an index would be a cache to keep correct across every handoff for no measurable saving.
    int ActorsIn(CellCoord coord)
    {
        // MATERIALISED FIRST, because this is a CAP and an entity frozen in an evicted cell is one no live cell
        // owns: a DurableOnly actor waiting in a freeze would be invisible here and a spawner would put another one
        // on top of it, over the budget the moment the coordinate is re-entered. Instantiating it runs a head's
        // restore hook, which is what hands the frozen actors back, and it costs nothing on either answer: a
        // PASSING count is followed by a SpawnOwned that creates the same cell a few lines later, and a REFUSING
        // count needs the cell to be there already to have anything to refuse.
        host.EnsureCell(coord);
        int n = 0;
        for (int i = 0; i < actorNetIds.Count; i++)
            if (host.TryGetOwner(actorNetIds[i], out CellSim cell, out _) && cell.Coord.Equals(coord)) n++;
        return n;
    }
}
