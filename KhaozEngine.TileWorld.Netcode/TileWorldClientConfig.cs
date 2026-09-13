using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Everything a tile client is handed rather than assumes, and the client half of the DETERMINISM CONTRACT with
/// <see cref="TileWorldServerConfig"/>. <see cref="TickSeconds"/>, <see cref="StepTicks"/> and <see cref="Move"/>
/// must carry the server's values: the two heads replay the same commands through the same
/// <see cref="TileMoveSimulator"/>, so a client stepping at a different cadence, or pathing with a different agent
/// size or route cap, mispredicts every step and every snapshot arrives as a correction.
/// <para><see cref="PlaneCount"/> and <see cref="MaxGoalRadius"/> are part of that contract too, for a subtler
/// reason: they are the server's two refusals of a walk goal, and it REWRITES a refused one to
/// <see cref="TileCommand.Continue"/> at the mode the command carried rather than dropping the tick. A client that
/// does not mirror both refuses nothing, predicts a walk the server never started, and snaps on the next
/// snapshot.</para>
/// </summary>
public sealed record TileWorldClientConfig
{
    /// <summary>Seconds per command tick. Must equal the server's <see cref="TileWorldServerConfig.TickSeconds"/>:
    /// it is both the rate this client issues commands at and the timestep prediction replays them over.</summary>
    public required float TickSeconds { get; init; }

    /// <summary>Ticks per step, per mode. Must equal the server's
    /// <see cref="TileWorldServerConfig.StepTicks"/>, or a step commits a tick apart on the two heads and every
    /// step of every walk reads as a misprediction.</summary>
    public required TileStepTicks StepTicks { get; init; }

    /// <summary>How far behind live a REMOTE is drawn, in ticks. Two ticks absorbs one lost snapshot without the
    /// remote holding on its tile, which is what the delay buys. It costs exactly itself in apparent lag, so a
    /// bigger number is not free.
    /// <para>It does not touch the local player, who is predicted rather than interpolated. It is also the ONLY
    /// presentation knob this config carries: the drawn body glides its whole step, linearly, on the step's own
    /// tick count, and that is a ruled behaviour rather than a tuning default (see <see cref="TilePresenter"/>).
    /// So a remote's divergence from its committed tile is this delay plus the step, and a design that reads other
    /// players' tiles is sized against the sum.</para></summary>
    public float InterpolationDelayTicks { get; init; } = 2f;

    /// <summary>Planes the world has. A walk goal naming one it does not is refused before the command is sent,
    /// the same bound the server applies before it steps and the command encoder applies on the wire.</summary>
    public int PlaneCount { get; init; } = TileWorldDocument.DefaultPlaneCount;

    /// <summary>Largest Chebyshev distance from the player a walk goal may name, mirroring
    /// <see cref="TileWorldServerConfig.MaxGoalRadius"/>. A farther goal is rewritten to
    /// <see cref="TileCommand.Continue"/> at the command's own mode BEFORE it is predicted, which is exactly what
    /// the server does with it, so the run toggle the click carried still applies and the two heads step the same
    /// tick. Set it from the server's value: a client with a LARGER radius predicts walks the server refuses, and
    /// one with a smaller radius refuses walks the server runs.</summary>
    public int MaxGoalRadius { get; init; } = TilePathfinder.DefaultMaxRadius;

    /// <summary>Simulator knobs, the other half of the determinism contract. The route cap
    /// (<see cref="TileMoveOptions.MaxRouteSteps"/>) lives here, and both heads truncate the same pathfinder result
    /// to the same tiles, so a long click ends on the same tile on both.</summary>
    public TileMoveOptions Move { get; init; } = new();

    /// <summary>
    /// Prediction tunables. Null derives them from <see cref="TickSeconds"/> and <see cref="StepTicks"/>: a 64-command
    /// window (16 seconds at a 4 Hz tick), a small dead zone, a hard-snap distance of a few tiles, and a correction
    /// speed cap of one WALK step per step duration.
    /// <para>The distances are in TILES rather than metres, because a <see cref="TileMoveState"/>'s position is a
    /// tile-lattice quantity and its vertical is a plane INDEX (see that type's doc).
    /// <see cref="PredictionSettings.Default"/> carries 100, documented in world units, which on this lattice means
    /// a hundred tiles of misprediction before anything ever cut: the same as never snapping at all.</para>
    /// <para>On a lattice a CORRECT prediction reconciles to exactly zero error, not to a small one: the replay
    /// re-applies the pending commands on top of the authoritative basis, and the basis carries the authoritative
    /// route, so a client running any number of ticks ahead still lands on the server's own state. Latency therefore
    /// contributes NO error to an ordinary walk, which is the opposite of the continuous case the engine default was
    /// tuned for. A CHASE is the exception, and it is why the default is not tuned to cut a single step. Predicting
    /// the pursuit of a MOVING target resolves that target's tile from this client's newest snapshot, which trails
    /// the server by the round trip plus its buffered input depth, so near the target's arrival the two heads step
    /// different ways by one tile. That is a timing artifact, not a disagreement about the world, and it appears at
    /// the same one-step magnitude a real blocker disagreement would. Two more sources look identical: a command that
    /// misses a server tick runs one tick apart on the two heads, and a backlog past the server's catch-up threshold
    /// makes its queue skip to the newest buffered command. None can be told from a real disagreement by distance
    /// alone.</para>
    /// <para>So a one-step disagreement is WALKED off rather than cut. <see cref="PredictionSettings.MaxCorrectionSpeed"/>
    /// caps the render correction at a walk step per step duration, so the body finishes the walk onto the corrected
    /// tile at roughly its own pace while the rules already hold it there, which is how OSRS reads: the drawn body
    /// lags and catches up while the tile is the truth. <see cref="PredictionSettings.HardSnapDistance"/> stays at a
    /// few tiles so a genuine disagreement past a chase step still CUTS rather than sliding the avatar across ground
    /// it was never routed over, and a teleport (an authoritative epoch advance) cuts at any distance regardless. A
    /// walking chase caps at the same walk speed a run does, which is deliberate: the corrected body has usually
    /// arrived and stands swinging, so the last tile is walked in whichever mode the approach used.</para>
    /// </summary>
    public PredictionSettings? Prediction { get; init; }
}
