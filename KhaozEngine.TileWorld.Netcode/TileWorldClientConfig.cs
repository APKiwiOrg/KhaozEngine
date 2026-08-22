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
    /// <para>It does not touch the local player, who is predicted rather than interpolated.</para></summary>
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
    /// Prediction tunables. Null derives them from <see cref="TickSeconds"/>: a 64-command window (16 seconds at a
    /// 4 Hz tick), a ONE-TILE hard-snap distance and a small dead zone.
    /// <para>The distance is in TILES rather than metres, because a <see cref="TileMoveState"/>'s position is a
    /// tile-lattice quantity and its vertical is a plane INDEX (see that type's doc).
    /// <see cref="PredictionSettings.Default"/> carries 100, documented in world units, which on this lattice means
    /// a hundred tiles of misprediction before anything ever cut: the same as never snapping at all.</para>
    /// <para>One tile is the threshold with a meaning rather than a feel. Below it the two heads are on the SAME
    /// square and disagree only about how far through a step they are, which is timing and glides. At or above it
    /// they are on DIFFERENT squares, which is a disagreement about the world (a blocker one head cannot see), and
    /// gliding that would walk the avatar across ground it was never routed over before arriving somewhere it had
    /// already been told it was not.</para>
    /// </summary>
    public PredictionSettings? Prediction { get; init; }
}
