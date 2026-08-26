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
    /// Prediction tunables. Null derives them from <see cref="TickSeconds"/>: a 64-command window (16 seconds at a
    /// 4 Hz tick), a HALF-TILE hard-snap distance and a small dead zone.
    /// <para>The distance is in TILES rather than metres, because a <see cref="TileMoveState"/>'s position is a
    /// tile-lattice quantity and its vertical is a plane INDEX (see that type's doc).
    /// <see cref="PredictionSettings.Default"/> carries 100, documented in world units, which on this lattice means
    /// a hundred tiles of misprediction before anything ever cut: the same as never snapping at all.</para>
    /// <para>Half a tile rather than a whole one, and the reason is worth reading before anybody raises it. On a
    /// lattice a CORRECT prediction reconciles to exactly zero error, not to a small one: the replay re-applies the
    /// pending commands on top of the authoritative basis, and the basis carries the authoritative route, so a
    /// client running any number of ticks ahead still lands on the server's own state. Latency therefore
    /// contributes NO error at all here, which is the opposite of the continuous case the engine default was tuned
    /// for.</para>
    /// <para>An error that does appear is USUALLY the two heads having stepped different ways, and that is what the
    /// number below is chosen for, but it is not the only source and a reader raising it should know the other two.
    /// A command that misses a server tick (a click delayed past one by jitter) has the server synthesise a
    /// <see cref="TileCommand.Continue"/> for that tick and apply the real command on the next, so the same command
    /// runs one tick apart on the two heads. And a backlog deeper than the server's catch-up threshold makes its
    /// command queue skip straight to the newest buffered command, discarding ones this client already predicted.
    /// Both are latency artifacts rather than disagreements about the world, both resolve themselves within a tick
    /// or two, and both measure the same magnitude as a real disagreement, so neither can be told apart from one by
    /// distance alone.</para>
    /// <para>Half a tile is the smallest such disagreement that can show up: one tick of a running step. Below it
    /// there is only float noise in the replay, so it glides. At or above it the heads' last steps went different
    /// ways, which is a fact about the world (a blocker one head cannot see) rather than about timing, and gliding
    /// it would slide the avatar across ground it was never routed over on its way to a square it had already been
    /// told it was not on. A WALKING step is a quarter tile per tick, so a walk cuts one tick later than a run
    /// does, which is the right way round: the slower the movement, the smaller the artifact.</para>
    /// </summary>
    public PredictionSettings? Prediction { get; init; }
}
