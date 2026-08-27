namespace KhaozEngine.TileWorld.Netcode;

/// <summary>What one tick's command asks for. Four values, and the wire form is one byte, because a tile world's
/// entire input vocabulary is where to go, what to touch and what to fight.</summary>
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

    /// <summary>Lock onto <see cref="TileCommand.Target"/> as a NET ID, chase it while it moves, and swing at it
    /// whenever the cooldown is ready and it is in reach. Replaces any interaction already pending, and is itself
    /// replaced by a <see cref="WalkTo"/>, which is how a player disengages.
    /// <para>A KIND of its own is mandatory rather than tidy, and the reason is that the two id spaces OVERLAP
    /// EXACTLY. <c>TileObject.Id</c> is a document-wide counter starting at 1 and a net id is
    /// <c>(nodeId &lt;&lt; 48) | counter</c> with the counter starting at 1 and node 0 for a single server, so
    /// object id 7 and the seventh spawned entity are the same 64 bits. A single target field with a single
    /// resolver could not tell which space a click meant, and the failure mode is silent: clicking a barrel would
    /// sometimes attack a player. The kind is the discriminator, and it is the only one available without widening
    /// the frame or tagging the ids.</para></summary>
    Attack = 3,
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
/// <param name="Target">The interaction target id for <see cref="TileCommandKind.Interact"/>, or the combat
/// target's NET ID for <see cref="TileCommandKind.Attack"/>, otherwise 0. One field over two id spaces that
/// overlap exactly, which is why <see cref="Kind"/> is what tells them apart.</param>
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

    /// <summary>Lock onto <paramref name="netId"/> and chase it. Unlike <see cref="Interact"/> this routes nothing
    /// up front: the FOLLOW inside the stepper re-paths every tick the target's committed tile moved, which is what
    /// makes a chase a chase rather than a one-shot walk to where something used to be.</summary>
    public static TileCommand Attack(long netId, TileMoveMode mode) =>
        new(TileCommandKind.Attack, default, mode, netId);
}
