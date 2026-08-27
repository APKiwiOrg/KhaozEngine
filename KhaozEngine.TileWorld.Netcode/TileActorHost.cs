using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Owns the spawner list and runs tick step 1b: every spawner ticks, then every live actor gets its
/// <see cref="PendingTileCommand"/> and its <see cref="TileActor"/> tag written. Driven by
/// <see cref="TileWorldServer"/>'s own tick body, BEFORE the movement pass, so anything decided here moves on the
/// same tick and ships in the same tick's snapshot.
/// <para>WHERE THE COMMAND COMES FROM, in order. A LATCHED <see cref="Command"/> outranks everything, which is what
/// lets a head take direct control of one actor (a scripted event, a boss phase, a test) without replacing the
/// behaviour for all of them. Otherwise <see cref="Behaviour"/> decides, and a null one (the default) answers
/// <see cref="TileCommand.Continue"/> so an unwired server's actors stand exactly where they were put. Either way
/// the command's MODE is the spawner's <see cref="TileActorDefinition.StepMode"/>, which is how a definition's
/// cadence stays live on every tick rather than only on the one a spawn latch would have covered.</para>
/// <para>An intent names a tile or a target and never a route, so everything about HOW an actor gets somewhere stays
/// in the one stepper both kinds of entity run and an actor can never move in a way a player could not.</para>
/// <para>BOTH COMPONENT WRITES ARE UNCONDITIONAL, EVERY TICK, FOR EVERY LIVE ACTOR, and that is a rule rather than
/// an implementation detail. Neither <see cref="PendingTileCommand"/> nor <see cref="TileActor"/> is registered for
/// replication, so a cell handoff rebuilds the entity from its Migrate capture without either of them and the actor
/// silently falls out of <see cref="TileMovementSystem"/>'s three-component query. A player is immune because step 1
/// of the tick body rewrites its command every tick, and this is that same immunity for something with no slot to
/// drive it. A later optimisation that skipped the write for an idle actor would reintroduce the bug at a region
/// boundary, which is the kind of bug that only reproduces on one tile of one map.</para>
/// <para>The actor pass walks <see cref="TileWorldServer.ActorNetIds"/> rather than an ECS query over the tag, and
/// that follows from the same fact: the tag is exactly what a crossing drops, so a query over it cannot see the one
/// actor that most needs the write. The net id list is the only index of who is an actor that survives a crossing.
/// </para>
/// <para>SPAWNER ORDER IS ADD ORDER, never a dictionary enumeration, for the reason <c>TileActionQueue</c> gives
/// about its own: a hash layout must never reach a decision. Here it decides net id assignment, and net id decides
/// the combat roll order, so an incidental order would make a fight's rolls depend on a runtime's hashing.</para>
/// </summary>
public sealed class TileActorHost
{
    // What an actor with no spawner is decided against. Real numbers rather than zeroes, so a head that spawned one
    // straight through TileWorldServer.SpawnActor gets a monster that wanders and leashes instead of one that stands
    // in place forever wondering why its radii are zero.
    static readonly TileActorDefinition FallbackDefinition = new() { Id = "ke:unspawned", MaxHealth = 1 };

    readonly TileWorldServer server;
    readonly List<TileActorSpawner> spawners = new();
    // Latched intent for the NEXT tick, latest wins, exactly as TileWorldClient.Queue is for a player. Keyed by net
    // id, and the dictionary's order never reaches a decision: it is only ever probed by key from inside the loop
    // over the ordered actor list.
    readonly Dictionary<long, TileCommand> nextCommand = new();
    // The actor list snapshotted per tick, because writing a command can spawn or despawn and a live collection
    // cannot be walked across one.
    readonly List<long> tickActors = new();
    // netId -> the spawner that built it, so the decision pass can hand a behaviour its definition and its home. An
    // actor spawned straight through TileWorldServer.SpawnActor has no entry, and is handed a home equal to its own
    // tile plus the fallback definition, so a head that spawns outside a spawner still gets a decision.
    readonly Dictionary<long, TileActorSpawner> spawnerByActor = new();

    /// <summary>Binds a host to the server it drives.</summary>
    /// <param name="server">The server that owns the actors.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    public TileActorHost(TileWorldServer server) =>
        this.server = server ?? throw new ArgumentNullException(nameof(server));

    /// <summary>The spawners, in the order they were added, which is the order they fire in.</summary>
    public IReadOnlyList<TileActorSpawner> Spawners => spawners;

    /// <summary>The decisions, and NULL by default. A null behaviour leaves every actor standing still unless
    /// something latches a command on it, which is the right default for a head that has not wired one yet, and it
    /// is deliberately not the engine's own: an engine that installed <see cref="TileWanderBehaviour"/> on every
    /// server would be picking a game's monster behaviour for it, which is the same line the engine refuses to cross
    /// with a combat number. Set it to <see cref="TileWanderBehaviour"/> for the engine's own, or to a game
    /// implementation that dispatches on <see cref="TileActorDefinition.Kind"/>.</summary>
    public ITileActorBehaviour? Behaviour { get; set; }

    /// <summary>The seed every actor's per-tick random stream is derived from. Two servers built with the same seed
    /// and driven with the same commands produce the same wander, which is what a replay depends on.</summary>
    public int Seed { get; set; }

    /// <summary>The spawner that built one actor.</summary>
    /// <param name="netId">The actor's net id.</param>
    /// <param name="spawner">Its spawner, null when it was built outside one.</param>
    public bool TryGetSpawnerOf(long netId, out TileActorSpawner spawner) =>
        spawnerByActor.TryGetValue(netId, out spawner!);

    /// <summary>Adds an authored spawn point and returns it, so a caller can read its state later. It spawns on the
    /// next tick rather than now, which keeps every actor's first tick the same tick.</summary>
    /// <param name="definition">What to build there.</param>
    /// <param name="home">Where to build it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="definition"/> is refused by
    /// <see cref="TileActorSpawner"/>'s own door.</exception>
    public TileActorSpawner Add(TileActorDefinition definition, TileCoord home)
    {
        var spawner = new TileActorSpawner(definition, home);
        spawners.Add(spawner);
        return spawner;
    }

    /// <summary>
    /// Latches the command one actor is stepped with on the NEXT tick, LATEST WINS. The seam an actor's intent lands
    /// on, and the reason step 1b can be tested without a behaviour. Spent once: the tick after it is applied falls
    /// back to <see cref="TileCommand.Continue"/> at the mode the step left the actor in, never
    /// <see cref="TileCommand.None"/>, because None is a run toggled off and would drop a running actor to a walk.
    /// </summary>
    /// <param name="netId">The actor's net id. An id this server holds no entity for is ignored on the next tick.</param>
    /// <param name="command">The command to step it with.</param>
    public void Command(long netId, in TileCommand command) => nextCommand[netId] = command;

    /// <summary>
    /// Drops any unspent latch for <paramref name="netId"/>. <see cref="TileWorldServer.DespawnActor"/> calls it,
    /// because a latch for an actor that no longer exists would otherwise sit in the dictionary forever: net ids
    /// are never recycled, so nothing else would ever read or replace it, and combat makes death-before-the-next-tick
    /// the routine case rather than the rare one.
    /// </summary>
    /// <param name="netId">The despawned actor's net id.</param>
    public void Forget(long netId) => nextCommand.Remove(netId);

    /// <summary>
    /// How many latched commands are waiting for their tick. The observable that pins the latch lifecycle: spent
    /// once by the tick that applies it, pruned by a despawn, so a healthy host trends to zero between commands.
    /// </summary>
    public int PendingCommandCount => nextCommand.Count;

    /// <summary>
    /// Tick step 1b. Every spawner ticks in add order, then every live actor in spawn order gets its command and its
    /// tag written. Called by <see cref="TileWorldServer"/> between the player command drain and the movement pass.
    /// </summary>
    public void Tick()
    {
        for (int i = 0; i < spawners.Count; i++) TickSpawner(spawners[i]);

        tickActors.Clear();
        tickActors.AddRange(server.ActorNetIds);
        for (int i = 0; i < tickActors.Count; i++)
        {
            long netId = tickActors[i];
            if (!server.TryGetActorState(netId, out TileMoveState state)) continue;
            spawnerByActor.TryGetValue(netId, out TileActorSpawner? spawner);
            TileMoveMode mode = spawner?.Definition.StepMode ?? state.Mode;

            // A LATCHED command outranks the behaviour, which is what lets a head take direct control of one actor
            // (a scripted event, a boss phase, a test) without replacing the behaviour for all of them.
            TileCommand command = nextCommand.Remove(netId, out TileCommand latched)
                ? latched
                : Decide(netId, state, spawner, mode, server.TickCount);

            server.WriteActorCommand(netId, command);
            RestoreOnArrival(netId, state, spawner);
        }
    }

    TileCommand Decide(long netId, in TileMoveState state, TileActorSpawner? spawner, TileMoveMode mode, long tick)
    {
        if (Behaviour is null) return TileCommand.Continue(mode);

        // Both reads answer false for an entity that carries neither component, which leaves the out parameter at
        // default, which is exactly the right view to hand a behaviour for one.
        server.TryGetCombatState(netId, out TileCombatState combat);
        server.TryGetHealth(netId, out TileHealth health);

        var context = new TileActorContext(
            NetId: netId,
            Tile: state.Tile,
            Home: spawner?.Home ?? state.Tile,
            Definition: spawner?.Definition ?? FallbackDefinition,
            Health: health,
            CombatTarget: state.CombatTarget,
            LastDamagedBy: combat.LastDamagedBy,
            LastDamagedTick: combat.LastDamagedTick,
            Walking: !state.Route.IsIdle || state.IsStepping,
            Tick: tick,
            Rng: TileActorRandom.For(Seed, netId, tick));

        TileActorIntent intent = Behaviour.Decide(context);
        switch (intent.Kind)
        {
            case TileActorIntentKind.WalkTo:
                if (spawner is not null) spawner.Returning = false;
                return TileCommand.WalkTo(intent.Tile, mode);
            case TileActorIntentKind.Attack:
                if (spawner is not null) spawner.Returning = false;
                return TileCommand.Attack(intent.Target, mode);
            case TileActorIntentKind.Break:
                // A WalkTo is what BREAKS the lock, on the state, through the one stepper. Nothing here clears
                // CombatTarget by hand, because a second place that cleared it is a second definition of the rule.
                //
                // THE DAMAGE RECORD IS THE OTHER HALF OF THE FIGHT, and dropping it is the same act rather than a
                // second one. Nothing else ages LastDamagedBy, so a break that left it set had the retaliation rule
                // re-acquire the SAME attacker on the first tick the actor was back inside its radius, which is
                // inside the window for any leash the walk home fits in. That re-acquire also clears Returning, so
                // the arrival restore never fired and the heal was lost for that break. A player who stopped
                // attacking got the monster back anyway.
                ForgetAttacker(netId);
                if (spawner is null) return TileCommand.Continue(mode);
                spawner.Returning = true;
                return TileCommand.WalkTo(spawner.Home, mode);
            default:
                return TileCommand.Continue(mode);
        }
    }

    // Drops the damage record, which is what makes a break a break rather than a pause. Read first and written only
    // when it holds something, so a leash that fires on every tick of a walk home costs one component read per tick
    // rather than a write into the archetype.
    void ForgetAttacker(long netId)
    {
        if (!server.TryGetCombatState(netId, out TileCombatState combat)) return;
        if (combat.LastDamagedBy == 0L && combat.LastDamagedTick == 0L) return;
        combat.LastDamagedBy = 0L;
        combat.LastDamagedTick = 0L;
        server.SetCombatState(netId, combat);
    }

    // The arrival half of a leash break: full health when it is HOME with nothing left to walk, never when it broke.
    // Gated on the flag rather than on the tile alone, so a monster whose home is where the fight is does not heal
    // every tick, and the flag is cleared here so it fires exactly once per break.
    // ARRIVED means COMMITTED TO THE TILE, not "the step animation finished", which is the same definition the
    // pending-action pass uses (a walk resolves on the tick its last step STARTS). It also has to be, because this
    // reads the TICK-START state: a gate on the step being over would first be true on the tick after the one every
    // other reader of the server already sees the actor standing at home on.
    void RestoreOnArrival(long netId, in TileMoveState state, TileActorSpawner? spawner)
    {
        if (spawner is null || !spawner.Returning) return;
        if (!state.Tile.Equals(spawner.Home) || !state.Route.IsIdle) return;
        spawner.Returning = false;
        if (server.TryGetHealth(netId, out TileHealth health) && health.Current < health.Max)
            server.SetHealth(netId, new TileHealth { Current = health.Max, Max = health.Max });
    }

    void TickSpawner(TileActorSpawner spawner)
    {
        switch (spawner.State)
        {
            case TileActorSpawnerState.Empty:
                TrySpawn(spawner);
                return;
            case TileActorSpawnerState.Alive:
                if (!server.TryGetActorState(spawner.ActorNetId, out _))
                {
                    spawnerByActor.Remove(spawner.ActorNetId);
                    spawner.Wait(spawner.Definition.RespawnDelayTicks);
                }
                return;
            case TileActorSpawnerState.Waiting:
                if (spawner.TickDown()) TrySpawn(spawner);
                return;
        }
    }

    void TrySpawn(TileActorSpawner spawner)
    {
        TileActorDefinition d = spawner.Definition;
        long netId = server.SpawnActor(spawner.Home, new TileActorSpawn(d.MaxHealth, d.AttackTicks, TileDirection.S));
        // Zero is the per-cell cap refusing at the door. The spawner keeps its state and tries again on the next
        // tick, which is the right answer for a transient condition: a cell over its budget is usually not over it a
        // moment later, and stranding the spawner would need an operator to notice.
        if (netId == 0) return;
        spawner.Alive(netId);
        // A fresh actor is never mid leash-break, and a spawner reused after a respawn would otherwise hand its new
        // actor the old one's flag and heal it on its first arrival home.
        spawner.Returning = false;
        // The index the decision pass reads a definition and a home out of. Keyed by net id rather than walked,
        // because the actor pass is a loop over net ids and ids are never recycled, so an entry can only ever be
        // replaced by the same spawner's next actor or dropped when that actor is gone.
        spawnerByActor[netId] = spawner;
        // The definition's cadence is LIVE FROM THE FIRST TICK, carried by the mode the actor pass falls back to
        // (the spawner's StepMode) rather than by a latch spent on the spawn tick. A spawn writes TileMoveState.At,
        // whose mode is Walk, so without that fallback a running actor would walk until something else commanded it
        // and a definition's StepMode would be a field nothing ever read. It rides the command stream either way,
        // which is where a cadence belongs on both kinds of entity: it is how a player's run toggle reaches the
        // stepper too.
    }
}
