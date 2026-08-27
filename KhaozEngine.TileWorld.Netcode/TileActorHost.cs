using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Owns the spawner list and runs tick step 1b: every spawner ticks, then every live actor gets its
/// <see cref="PendingTileCommand"/> and its <see cref="TileActor"/> tag written. Driven by
/// <see cref="TileWorldServer"/>'s own tick body, BEFORE the movement pass, so anything decided here moves on the
/// same tick and ships in the same tick's snapshot.
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
    readonly TileWorldServer server;
    readonly List<TileActorSpawner> spawners = new();
    // Latched intent for the NEXT tick, latest wins, exactly as TileWorldClient.Queue is for a player. Keyed by net
    // id, and the dictionary's order never reaches a decision: it is only ever probed by key from inside the loop
    // over the ordered actor list.
    readonly Dictionary<long, TileCommand> nextCommand = new();
    // The actor list snapshotted per tick, because writing a command can spawn or despawn and a live collection
    // cannot be walked across one.
    readonly List<long> tickActors = new();

    /// <summary>Binds a host to the server it drives.</summary>
    /// <param name="server">The server that owns the actors.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    public TileActorHost(TileWorldServer server) =>
        this.server = server ?? throw new ArgumentNullException(nameof(server));

    /// <summary>The spawners, in the order they were added, which is the order they fire in.</summary>
    public IReadOnlyList<TileActorSpawner> Spawners => spawners;

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
            TileCommand command = nextCommand.Remove(netId, out TileCommand latched)
                ? latched
                : TileCommand.Continue(state.Mode);
            server.WriteActorCommand(netId, command);
        }
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
                    spawner.Wait(spawner.Definition.RespawnDelayTicks);
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
        // The definition's cadence is LIVE FROM THE FIRST TICK. A spawn writes TileMoveState.At, whose mode is Walk,
        // and the fallback below is Continue at whatever mode the state already holds, so without this latch a
        // running actor would walk until something else commanded it. Latched rather than written onto the state,
        // because the command stream is where a cadence belongs on both kinds of entity: it is how a player's run
        // toggle reaches the stepper too.
        Command(netId, TileCommand.Continue(d.StepMode));
    }
}
