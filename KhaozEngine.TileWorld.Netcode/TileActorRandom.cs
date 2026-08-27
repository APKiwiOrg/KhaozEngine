namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// A deterministic per-actor random stream, seeded by the engine from the host's seed, the actor's net id and the
/// tick. A value type over one <see cref="ulong"/>, so it allocates nothing, needs no per-actor storage anywhere,
/// and a fresh <see cref="For"/> reproduces the same draws forever.
/// <para>Deliberately NOT <see cref="System.Random"/>. Its sequence is explicitly not guaranteed stable across .NET
/// releases, so a replay or a golden built on it would fail on a runtime upgrade rather than on a regression. This is
/// splitmix64, pure integer arithmetic with no floats anywhere, which is the same property the rest of this package's
/// determinism rests on.</para>
/// <para>MUTATING BY DESIGN. <see cref="TileActorContext"/> carries one by value and hands it to a behaviour through
/// an <c>in</c> parameter, so a behaviour copies it to a local and draws from the copy. That is correct rather than a
/// hazard: the stream is derived fresh per actor per tick, so nothing has to carry forward between calls. It is also
/// the first mistake a game will make here: <c>context.Rng.Next(10)</c> called twice takes a DEFENSIVE COPY each
/// time and hands back the identical number, silently and deterministically, because the advance lands on a copy the
/// caller never sees.</para>
/// <para>The sequence is pinned BY VALUE by a test, so treat it as recorded output rather than as an implementation
/// detail: changing a constant, a shift or how the three inputs compose breaks every replay taken against it.</para>
/// </summary>
public struct TileActorRandom
{
    ulong state;

    /// <summary>Builds a stream from a raw seed. A zero seed is replaced, because splitmix64's step is an addition
    /// and a zero state is a legitimate but conspicuous starting point.</summary>
    /// <param name="seed">The raw state to start from.</param>
    public TileActorRandom(ulong seed) => state = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;

    /// <summary>The stream for one actor on one tick. Two servers built with the same host seed produce the same
    /// draws for the same actor on the same tick, which is what a reproducibility test pins.</summary>
    /// <param name="seed">The host's seed.</param>
    /// <param name="netId">The actor's net id.</param>
    /// <param name="tick">The server tick.</param>
    public static TileActorRandom For(int seed, long netId, long tick) => new(
        unchecked((ulong)seed * 0x9E3779B97F4A7C15UL
                ^ (ulong)netId * 0xBF58476D1CE4E5B9UL
                ^ (ulong)tick * 0x94D049BB133111EBUL));

    /// <summary>The next 64 bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A value in <c>[0, maxExclusive)</c>, and 0 for a bound of one or less, so a caller with a degenerate
    /// range gets an answer rather than an exception inside a server tick.
    /// <para>MODULO, AND THAT IS THE RULING RATHER THAN AN OVERSIGHT. The bias is bounded by
    /// <c>maxExclusive / 2^64</c>, about 1e-17 for any roll a combat pass makes (an accuracy roll, a max hit, a
    /// d100), so this is FIT for one and a rejection loop is not wanted: a loop makes the number of draws per roll
    /// depend on the values drawn, which is a worse property for a reproducible sequence than a bias nobody can
    /// measure. Do not wrap this call to correct it.</para></summary>
    /// <param name="maxExclusive">The exclusive upper bound.</param>
    public int Next(int maxExclusive) =>
        maxExclusive <= 1 ? 0 : (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>A value in <c>[minInclusive, maxExclusive)</c>, and <paramref name="minInclusive"/> for an empty or
    /// inverted range.</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound.</param>
    public int Next(int minInclusive, int maxExclusive) =>
        maxExclusive <= minInclusive ? minInclusive : minInclusive + Next(maxExclusive - minInclusive);
}
