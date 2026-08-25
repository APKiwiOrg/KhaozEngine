namespace KhaozEngine.TileWorld.Netcode;

/// <summary>What one tick's command asks for. Three values, and the wire form is one byte, because a tile world's
/// entire input vocabulary is where to go and what to touch.</summary>
public enum TileCommandKind : byte
{
    /// <summary>Keep doing what you are doing. The command a client sends while a route plays out, and what makes
    /// the one-command-per-tick sequence model fit with no "walking" flag on the wire. A tick with nothing to say
    /// still sends one, because the sequence numbers are what the queue drains and reconciliation replays.</summary>
    None = 0,

    /// <summary>Path to <see cref="TileCommand.Goal"/> and walk it at <see cref="TileCommand.Mode"/>. Replaces any
    /// route already in progress, which is what makes a second click feel immediate. The goal is on the player's
    /// OWN plane, or the whole command is dropped when it is applied.</summary>
    WalkTo = 1,

    /// <summary>Route to a reach tile of <see cref="TileCommand.Target"/>, face it, and raise the action on
    /// arrival. The target rather than a tile is sent because a thing can move between the click and the
    /// arrival.</summary>
    Interact = 2,
}

/// <summary>
/// One tick of player intent, sent once per client tick and drained one per player per server tick by
/// <see cref="KhaozEngine.Netcode.RemoteCommandQueue{TCommand}"/>. Integer-only by construction: a tile world has
/// no analogue axis to quantize and no non-finite value to reject, so the whole class of float validation the
/// continuous protocol needs simply has no surface here.
/// <para>A fixed 24 byte frame regardless of kind. Every field is carried on every command so the decoder has no
/// branches over layout, which is one fewer way for an attacker-shaped frame to reach code that assumed a
/// kind.</para>
/// </summary>
/// <param name="Kind">What this command asks for.</param>
/// <param name="Goal">The destination tile for <see cref="TileCommandKind.WalkTo"/>, otherwise ignored. Its plane
/// must be the player's own, or the command is dropped at apply.</param>
/// <param name="Mode">Walk or run. Carried on every kind, so the run toggle rides the tick stream rather than the
/// click, and a change takes effect at the start of the next step.</param>
/// <param name="Target">The interaction target id for <see cref="TileCommandKind.Interact"/>, otherwise 0.</param>
public readonly record struct TileCommand(TileCommandKind Kind, TileCoord Goal, TileMoveMode Mode, long Target)
{
    /// <summary>Keep walking the current route, or keep standing, at <paramref name="mode"/>. THE factory a client
    /// uses on every tick the player issued nothing, because the mode it carries is what holds run on: the
    /// simulator applies it every tick, and a change lands at the start of the next step rather than cutting the
    /// step already under way.</summary>
    public static TileCommand Continue(TileMoveMode mode) => new(TileCommandKind.None, default, mode, 0);

    /// <summary>The neutral command: <see cref="Continue"/> at <see cref="TileMoveMode.Walk"/>. What a decoder
    /// hands back when it rejects a frame, and what a client that never offers a run toggle sends. A client that
    /// does offer one sends <see cref="Continue"/> instead, because this one holds a run OFF.</summary>
    public static TileCommand None => Continue(TileMoveMode.Walk);

    /// <summary>Path to <paramref name="goal"/> and walk it. <paramref name="goal"/> is on the player's own plane
    /// or the command is dropped whole when it is applied: the simulator refuses a cross-plane goal rather than
    /// walking the player to the same X and Z on the plane they are already standing on.</summary>
    public static TileCommand WalkTo(TileCoord goal, TileMoveMode mode) =>
        new(TileCommandKind.WalkTo, goal, mode, 0);

    /// <summary>Route to a reach tile of <paramref name="target"/> and interact as the walk COMMITS to it, which is
    /// the tick the last step starts and not the tick the drawn body gets there.</summary>
    public static TileCommand Interact(long target, TileMoveMode mode) =>
        new(TileCommandKind.Interact, default, mode, target);
}
