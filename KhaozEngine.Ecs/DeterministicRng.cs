namespace KhaozEngine.Ecs;

/// <summary>
/// Seeded, fixed-algorithm pseudo-random generator (xorshift128+, seeded via splitmix64). Reproducible
/// across .NET versions and platforms — unlike <see cref="System.Random"/>. Opt-in: a game owns an
/// instance and persists <see cref="State"/> for save/resume. Used inside deferred commands so draws
/// occur in a deterministic order (see the outcome-buffer contract).
/// </summary>
public sealed class DeterministicRng
{
    private ulong _s0, _s1;

    public DeterministicRng(ulong seed)
    {
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
    public int Next(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);

    /// <summary>An int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
