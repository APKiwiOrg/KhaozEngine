using KhaozEngine.Ecs;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Runs <see cref="TileMoveSimulator"/> over every OWNED player entity inside the cell's own fixed tick, so
/// authority follows the cell that owns the entity and the per-cell fan-out stays free of shared mutable state.
/// One instance is added to each cell's world, and the simulator behind it is shared by all of them because
/// nothing about it is stateful.
/// <para>A <see cref="Ghost"/> is a read-only mirror of an entity another cell simulates, and a
/// <see cref="Migrating"/> entity has already been captured and sent to its destination. Stepping either would
/// simulate one player twice in one tick, in two cells, from two copies of its state, so both are skipped.</para>
/// <para>The route is reassembled from <see cref="TileRouteState"/> when the state carries none: a cell handoff
/// rebuilds the entity from its Migrate capture, and the capture carries the route in that component rather than
/// inside <see cref="TileMoveState"/> (whose codec deliberately omits it). Writing the route back out every tick
/// allocates one small array per walking player per tick, which at a tile world's tick rate is not worth a pool.
/// </para>
/// <para>The command is reset to <see cref="TileCommand.Continue"/> at the mode the step LEFT the player in,
/// never to <see cref="TileCommand.None"/>. The reset is what a second application of the same component would
/// see (a tick that could not route a command, a cell stepped twice), and None is a run toggled off, so the reset
/// would quietly slow a running player down.</para>
/// </summary>
public sealed class TileMovementSystem : ISystem
{
    readonly TileMoveSimulator simulator;

    /// <summary>Steps every owned player through <paramref name="simulator"/>.</summary>
    /// <param name="simulator">The one stepper both heads run. Shared across every cell, since it holds no state.</param>
    public TileMovementSystem(TileMoveSimulator simulator) => this.simulator = simulator;

    /// <inheritdoc/>
    public void Update(World world, float dt)
    {
        world.ForEach((Entity e, ref TileMoveState state, ref TileRouteState route, ref PendingTileCommand pending) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;

            TileMoveState s = state;
            if (s.Route.IsIdle && route.Remaining is { Length: > 0 })
                s.Route = TileRoute.FromSteps(s.Tile, route.Remaining);

            s = simulator.Step(s, pending.Command, dt);

            state = s;
            route.Remaining = s.Route.RemainingSteps(s.Tile);
            pending.Command = TileCommand.Continue(s.Mode);
        });
    }
}
