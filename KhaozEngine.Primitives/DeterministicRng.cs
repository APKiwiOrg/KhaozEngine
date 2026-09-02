using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// Seeded, fixed-algorithm pseudo-random generator (a xorshift128+-derived recurrence, seeded via
/// splitmix64). Reproducible across .NET versions and platforms (unlike <see cref="System.Random"/>).
/// Opt-in: a game owns an instance and persists <see cref="State"/> for save/resume. Used inside
/// deferred commands so draws occur in a deterministic order (see the outcome-buffer contract).
/// <para>
/// DERIVED, NOT THE CANONICAL CONSTRUCTION. The state update is Vigna's xorshift128+ word for word,
/// but the returned value is formed AFTER that update rather than before it, so the stream is not the
/// one the studied xorshift128+ generator emits. See <see cref="NextULong"/> for why it stays that way.
/// </para>
/// </summary>
public sealed class DeterministicRng
{
    private readonly ulong _seed;
    private ulong _s0, _s1;

    public DeterministicRng(ulong seed)
    {
        _seed = seed;
        ulong z = seed;
        _s0 = SplitMix(ref z);
        _s1 = SplitMix(ref z);
        if ((_s0 | _s1) == 0) _s1 = 1;   // xorshift state must not be all-zero
    }

    /// <summary>The full internal state, for save/resume of an in-progress deterministic run.</summary>
    public (ulong S0, ulong S1) State
    {
        get => (_s0, _s1);
        set { _s0 = value.S0; _s1 = value.S1; }
    }

    /// <summary>
    /// The next 64-bit draw. The state update is xorshift128+ exactly, but the sum is taken over the
    /// NEW second word and the old one, where the canonical generator sums both words as they stood
    /// before the update. That makes the output stream a derived variant rather than the specifically
    /// studied construction, and it carries whatever statistical properties an unreviewed variant
    /// carries.
    /// <para>
    /// IT STAYS THAT WAY ON PURPOSE. The stream is a shipped contract, not an implementation detail:
    /// <see cref="State"/> is public and games persist it for save/resume, the class promises the same
    /// seed gives the same sequence on every platform and .NET version, and content generated from a
    /// seed (dungeon layouts, procedural placement, audio track order) is a function of it. Two known
    /// vectors in the test suite pin it deliberately. Moving the addition to the pre-update words would
    /// shift every seeded stream in the fleet from that point forward, so the name was corrected here
    /// instead of the recurrence.
    /// </para>
    /// </summary>
    public ulong NextULong()
    {
        ulong s1 = _s0, s0 = _s1;
        _s0 = s0;
        s1 ^= s1 << 23;
        _s1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);
        return _s1 + s0;
    }

    public uint NextUInt() => (uint)(NextULong() >> 32);

    /// <summary>A double in [0, 1) with 53 bits of precision.</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);   // 2^53

    /// <summary>A float in [0, 1).</summary>
    public float NextFloat() => (float)NextDouble();

    /// <summary>An int in [0, <paramref name="maxExclusive"/>). Uses modulo (negligible bias for game ranges).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is &lt;= 0.</exception>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be positive.");
        return (int)(NextULong() % (ulong)maxExclusive);
    }

    /// <summary>An int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is &lt;= <paramref name="minInclusive"/>.</exception>
    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be greater than minInclusive.");
        return minInclusive + Next(maxExclusive - minInclusive);
    }

    /// <summary>
    /// Returns a new generator whose stream is a stable function of THIS generator's
    /// construction seed and <paramref name="systemName"/>. The same construction seed and
    /// name always yield the same stream; different names that hash distinctly (64-bit
    /// DJB2-xor space), or different parent seeds, yield decorrelated streams. Derivation
    /// uses the construction seed, not the live draw state,
    /// so the result is independent of how many numbers this generator has drawn and is NOT
    /// affected by a <see cref="State"/> restore. Lets each subsystem own an isolated,
    /// reproducible stream (e.g. "combat", "oreField").
    /// </summary>
    /// <param name="systemName">
    /// Stable subsystem identifier. Changing it shifts the stream. Empty string is allowed;
    /// must not be null.
    /// </param>
    public DeterministicRng CreateDerived(string systemName)
    {
        ArgumentNullException.ThrowIfNull(systemName);
        ulong derivedSeed = _seed ^ StableHash(systemName);
        return new DeterministicRng(derivedSeed);
    }

    /// <summary>
    /// Platform-stable string hash (DJB2 xor variant). Deterministic across runs, .NET
    /// versions, and platforms, unlike <see cref="string.GetHashCode()"/>, which is
    /// randomized per process.
    /// </summary>
    public static ulong StableHash(string s)
    {
        unchecked
        {
            ulong hash = 5381UL;
            for (int i = 0; i < s.Length; i++)
                hash = ((hash << 5) + hash) ^ s[i];
            return hash;
        }
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
