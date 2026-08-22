using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The command drained for this player this tick, handed to the cell's movement system. A component rather than a
/// parameter because the step runs inside the cell's own ECS update, where the only thing reaching a player entity
/// is the data attached to it.
/// <para>ECS-only: it is never registered for replication, so it reaches no client, no persistence blob and no
/// handoff capture. That is deliberate. A command is a fact about ONE tick, and a copy of it riding a cell handoff
/// would be applied a second time by the destination cell, which for a <see cref="TileCommandKind.WalkTo"/> means
/// re-pathing from the new tile and losing the step already under way. The server writes a fresh one every tick,
/// including the tick after a handoff, so nothing is lost by leaving it behind.</para>
/// </summary>
public struct PendingTileCommand : IComponent
{
    /// <summary>The command to apply on the next step. Reset once it has run to
    /// <see cref="TileCommand.Continue"/> at the player's CURRENT mode, never to <see cref="TileCommand.None"/>:
    /// None is Continue at walk, so it would read as a run toggled off and drop a running player to a walking
    /// cadence on the first tick its packets did not arrive.</summary>
    public TileCommand Command;
}
