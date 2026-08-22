using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// How many ticks one step takes, per mode. CONFIGURATION the engine is handed rather than a constant it owns:
/// a tile game's whole sense of pace is these two numbers against the tick length, and the engine has no business
/// picking either. Both heads must hold the SAME pair, because a step that fills on tick 4 for one and tick 5 for
/// the other commits its tile a tick apart and every step of the walk then reads as a misprediction.
/// <para>Counted in TICKS rather than seconds on purpose. A tick count is an integer, so a step boundary falls on
/// exactly the same tick on every machine whatever its frame time, which is what
/// <see cref="TileMoveSimulator"/> reproduces its steps from. Seconds would put a float division on the one path
/// determinism depends on.</para>
/// </summary>
public readonly record struct TileStepTicks
{
    /// <summary>Both counts must be at least 1: a zero-tick step would fill on the tick it started and go on
    /// committing a tile every tick, which is a teleport rather than a fast walk.</summary>
    /// <param name="walk">Ticks a walking step takes.</param>
    /// <param name="run">Ticks a running step takes.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either count is zero.</exception>
    public TileStepTicks(byte walk, byte run)
    {
        if (walk == 0) throw new ArgumentOutOfRangeException(nameof(walk), "A step takes at least one tick.");
        if (run == 0) throw new ArgumentOutOfRangeException(nameof(run), "A step takes at least one tick.");
        Walk = walk;
        Run = run;
    }

    /// <summary>Ticks a walking step takes.</summary>
    public byte Walk { get; }

    /// <summary>Ticks a running step takes.</summary>
    public byte Run { get; }

    /// <summary>The engine's neutral default, walk 4 and run 2. Deliberately plain rather than tuned: a game
    /// supplies its own pair, and nothing in the engine reads this except a caller that did not.</summary>
    public static TileStepTicks Default => new(4, 2);

    /// <summary>The tick count for one mode. Anything that is not <see cref="TileMoveMode.Run"/> walks, so a
    /// state carrying a mode value from an older build steps at the slower rate instead of throwing mid tick.</summary>
    public byte For(TileMoveMode mode) => mode == TileMoveMode.Run ? Run : Walk;
}
