using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The command drained for this player this tick, handed to the cell's movement system. A component rather than a
/// parameter because the step runs inside the cell's own ECS update, where the only thing reaching a player entity
/// is the data attached to it.
/// <para>MIGRATE ONLY: registered on <see cref="KhaozEngine.Replication.ReplicationChannels.Migrate"/> alone, so it
/// crosses a cell handoff and reaches no client and no persistence blob. A command is a fact about ONE tick, so
/// persisting it would apply a click across a server restart, and replicating it would put a player's input on
/// another player's wire.</para>
/// <para>THE HANDOFF IS THE ONE IT HAS TO SURVIVE, because <see cref="TileMovementSystem"/> reads this alongside
/// the move state and the route: an entity arriving in the destination cell without it drops out of the query
/// entirely and is not stepped at all. It used to arrive without it, and nothing noticed, because the server
/// writes a fresh one onto every player at tick step 1 and onto every actor at step 1b, both before the next
/// movement pass. That rewrite was propping the component up, and the day an idle entity is skipped to save the
/// write is the day it strands unmovable on one tile of one map edge.</para>
/// <para>Carrying it costs nothing at the destination, because the movement pass RESETS it to
/// <see cref="TileCommand.Continue"/> at the mode the step left the player in, and the pass runs at tick step 2
/// while the handoff runs at step 3. So what a handoff ever captures is the tick's neutral rather than a click
/// waiting to be applied a second time, and the mode a running player crossed the border in crosses with
/// them.</para>
/// </summary>
public struct PendingTileCommand : IComponent
{
    /// <summary>The command to apply on the next step. Reset once it has run to
    /// <see cref="TileCommand.Continue"/> at the player's CURRENT mode, never to <see cref="TileCommand.None"/>:
    /// None is Continue at walk, so it would read as a run toggled off and drop a running player to a walking
    /// cadence on the first tick its packets did not arrive.</summary>
    public TileCommand Command;
}
