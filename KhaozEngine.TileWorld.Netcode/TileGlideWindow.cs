using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// How long the drawn body is allowed to lag the tile it is already committed to, in SECONDS. A step commits
/// <see cref="TileMoveState.Tile"/> when it STARTS and the body glides in afterwards (see that type), so this is the
/// one number that bounds how far the picture may sit behind the truth the rules answer from.
/// <para>SECONDS rather than a fraction of the step, and that is the whole point of the type. A run is a shorter
/// step than a walk, so a fraction would make the walking catch-up take twice as long as the running one and the two
/// would not feel like the same game. In seconds the body reaches its tile the same wall-clock time after the commit
/// whatever it is moving at, which is what a player reads as a consistent feel.</para>
/// <para>A window at or above the step's own duration is the FULL-STEP glide the tile stack has always drawn, and
/// that is <see cref="WholeStep"/>, the default. A window of zero puts the body on the tile the tick it commits.
/// In between, the body covers the whole step in the window's seconds and then WAITS on its tile for the rest of the
/// step, so the divergence between the picture and the committed tile is bounded by the window rather than by the
/// step. Everything about the simulation is untouched: this is read on the way to a view and written to nothing, so
/// no replay, no reconciliation and no server tick can see it.</para>
/// <para>The bound is measured on the state's OWN timeline, and each drawing path adds an offset of its own that
/// predates this type and is unchanged by it. The LOCAL player is placed from the state the prediction layer is
/// holding, so its catch-up is the window itself, and what rides on top is whatever is left of a decaying
/// reconciliation offset after a misprediction (bounded by
/// <see cref="KhaozEngine.Netcode.PredictionSettings.HardSnapDistance"/>, and zero on the deterministic lattice's
/// ordinary case). A REMOTE is drawn off the delayed timeline
/// <see cref="TileWorldClientConfig.InterpolationDelayTicks"/> names, which is a whole tick per delay tick and by
/// default the widest of the three. So what a head actually draws is the window plus its path's own offset, and the
/// window is what it CAN control.</para>
/// </summary>
public readonly record struct TileGlideWindow
{
    /// <summary>Builds a window of <paramref name="seconds"/> against a tick of <paramref name="tickSeconds"/>.</summary>
    /// <param name="seconds">Seconds the body may lag its committed tile by. Zero snaps on the commit,
    /// <see cref="float.PositiveInfinity"/> is the full-step glide.</param>
    /// <param name="tickSeconds">Seconds per command tick, which is what turns a step's TICK count into a duration.
    /// The same number both heads clock at, <see cref="TileWorldClientConfig.TickSeconds"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is negative or not a number, or
    /// <paramref name="tickSeconds"/> is zero, negative or not a number. A zero tick is refused rather than read as
    /// <see cref="WholeStep"/>, because a caller that passed one meant to configure a window and would otherwise get
    /// the feature silently switched off.</exception>
    public TileGlideWindow(float seconds, float tickSeconds)
    {
        if (float.IsNaN(seconds) || seconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A glide window is zero or longer.");
        if (float.IsNaN(tickSeconds) || tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "A tick is longer than zero seconds.");
        Seconds = seconds;
        TickSeconds = tickSeconds;
    }

    /// <summary>Seconds the drawn body may lag its committed tile by.</summary>
    public float Seconds { get; }

    /// <summary>Seconds per command tick, zero on <see cref="WholeStep"/> and positive on every constructed
    /// window.</summary>
    public float TickSeconds { get; }

    /// <summary>The window that covers the whole step, which is the DEFAULT and draws exactly what the tile stack
    /// drew before windows existed. It is <c>default</c> deliberately: a zero tick length cannot name a duration, so
    /// the unconfigured value can only mean "no window", and a caller who wanted a zero-second window has to build
    /// one against a real tick to say so.</summary>
    public static TileGlideWindow WholeStep => default;

    /// <summary>True when this window covers a step of <paramref name="stepTotal"/> ticks whole, so the pose is the
    /// untouched full-step glide.</summary>
    /// <param name="stepTotal">Ticks the step takes, <see cref="TileMoveState.StepTotal"/>.</param>
    public bool CoversWholeStep(byte stepTotal) => !(FractionOf(stepTotal) < 1f);

    // The window as a fraction of a step that takes stepTotal ticks. On WholeStep the tick length is zero, so this
    // is an infinity (a positive window) or a NaN (a zero one), and both fail every "< 1" test below, which is how
    // the unconfigured value costs no branch of its own. An infinite window does the same for a real tick.
    internal float FractionOf(byte stepTotal) => Seconds / (stepTotal * TickSeconds);

    // THE remap, stated once so the local and the remote pose cannot drift apart. Takes the fraction of the step
    // already spent and returns the fraction of the way from StepFrom to Tile to DRAW at.
    //
    // Linear inside the window and flat after it, deliberately: an ease-out would spend its last frames crawling the
    // final centimetres, which is exactly the "still not there" the window exists to remove, and it would blur the
    // one thing this is a contract about (the instant the body IS on its tile). A game that wants a curve has the
    // window itself to tune, which changes when the body arrives rather than how it dawdles on the way.
    //
    // Both callers hand in a fraction already inside [0, 1] (the remote path clamps its own, and the local one builds
    // it from a tick count and a phase that cannot go negative), so the clamp here is a guard rather than a branch
    // either path relies on. It is kept because the alternative is extrapolation: divided by a small window, a
    // fraction a hair either side of the range would fling the body a whole multiplied step past the tiles it is
    // meant to be between, and a guard is cheaper than the invariant it would cost to prove that can never happen.
    internal static float Remap(float fraction, float window)
    {
        // Covers the step (and NaN, and infinity): today's linear glide, byte for byte, with nothing recomputed.
        if (!(window < 1f)) return fraction;
        // A zero window has no inside. The body is on its committed tile from the tick the step starts, which is the
        // strictest reading of the invariant and the one a game asks for when it wants no visual truth gap at all.
        if (!(window > 0f)) return 1f;
        return Math.Clamp(fraction / window, 0f, 1f);
    }
}
