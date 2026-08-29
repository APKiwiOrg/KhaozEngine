using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Runs <see cref="TileMoveSimulator"/> over every OWNED entity, player and actor alike, inside the cell's own
/// fixed tick, so authority follows the cell that owns the entity and the per-cell fan-out stays free of shared
/// mutable state. One instance is added to each cell's world, and the simulators behind it are shared by all of
/// them because nothing about one is stateful.
/// <para>WHICH simulator an entity is stepped through is the <see cref="TileActor"/> TAG, so an actor runs the
/// same stepper as a player over its own options rather than a movement rule of its own. The tag rather than the
/// absence of a connection slot, because nothing inside a cell knows about slots and net ids deliberately know
/// nothing about connections.</para>
/// <para>A <see cref="Ghost"/> is a read-only mirror of an entity another cell simulates, and a
/// <see cref="Migrating"/> entity has already been captured and sent to its destination. Stepping either would
/// simulate one player twice in one tick, in two cells, from two copies of its state, so both are skipped.</para>
/// <para>The state is read through <see cref="TileProtocol.AssembleMoveState"/>, which is the ONE place the
/// route is put back onto it and the only sanctioned way to read <c>TileMoveState.Route</c> on this server. Never
/// take the route off the raw component here: that method's doc has the failure. Writing the route back out every
/// tick allocates one small array per walking player per tick, which at a tile world's tick rate is not worth a
/// pool.</para>
/// <para>The command is reset to <see cref="TileCommand.Continue"/> at the mode the step LEFT the player in,
/// never to <see cref="TileCommand.None"/>. The reset is what a second application of the same component would
/// see (a tick that could not route a command, a cell stepped twice), and None is a run toggled off, so the reset
/// would quietly slow a running player down.</para>
/// </summary>
public sealed class TileMovementSystem : ISystem
{
    readonly TileMoveSimulator players;
    readonly TileMoveSimulator actors;

    /// <summary>Steps every owned entity through one simulator, players and actors alike. The shape that shipped
    /// before actors existed, kept because it is still the right one for a head with no actors.</summary>
    /// <param name="simulator">The one stepper both heads run. Shared across every cell, since it holds no state.</param>
    public TileMovementSystem(TileMoveSimulator simulator) : this(simulator, simulator)
    {
    }

    /// <summary>Steps players through <paramref name="players"/> and <see cref="TileActor"/>s through
    /// <paramref name="actors"/>. Two instances rather than two systems, so the <see cref="Ghost"/> and
    /// <see cref="Migrating"/> skip stays in ONE place and one pass still walks the archetype once. The simulator is
    /// stateless, so a second instance costs nothing but its options.</summary>
    /// <param name="players">The stepper a player entity runs, and the one a client predicts against.</param>
    /// <param name="actors">The stepper an actor entity runs, tuned to a leash-sized path radius.</param>
    public TileMovementSystem(TileMoveSimulator players, TileMoveSimulator actors)
    {
        this.players = players;
        this.actors = actors;
    }

    /// <inheritdoc/>
    public void Update(World world, float dt)
    {
        world.ForEach((Entity e, ref TileMoveState state, ref TileRouteState route, ref PendingTileCommand pending) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;

            // The pick is the TAG rather than the absence of a slot, because nothing in a cell knows about slots and
            // net ids deliberately know nothing about connections. An actor that crossed a region boundary this tick
            // has already had its tag written back by step 1b, which runs before this pass.
            TileMoveSimulator simulator = world.Has<TileActor>(e) ? actors : players;

            // WHOSE state this is, handed to the stepper because the follow's rule 4 cannot derive it: an Attack
            // naming the attacker itself and one naming another entity standing on the same tile resolve to the
            // identical footprint and want opposite answers (a standstill and a step off).
            //
            // Read only when a lock is in play, so an idle world pays nothing for it, and THIS TICK'S Attack counts:
            // the lock it sets is written inside the step below, so the state still reads 0 here on the very tick a
            // self attack begins, which is the one tick that decides the whole case. An actor's own attack arrives
            // through the same field, as a TileCommand its behaviour asked for (TileActorHost's Attack intent), so
            // both doors onto a fresh lock are covered by the one condition.
            //
            // Read as a component rather than added to the ForEach signature above: an entity carrying no NetId
            // would silently drop out of the movement pass if it joined the archetype filter, which is a far worse
            // failure than an unnamed self.
            long self = 0L;
            if ((state.CombatTarget != 0 || pending.Command.Kind == TileCommandKind.Attack)
                && world.TryGet(e, out NetId netId)) self = netId.Value;

            TileMoveState s = simulator.Step(
                TileProtocol.AssembleMoveState(state, route), pending.Command, dt, self);

            state = s;
            route.Remaining = s.Route.RemainingSteps(s.Tile);
            pending.Command = TileCommand.Continue(s.Mode);
        });
    }
}
