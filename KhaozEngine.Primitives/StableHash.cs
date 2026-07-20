namespace KhaozEngine.Primitives;

/// <summary>
/// Stateless, allocation-free integer hashing: fold one to three <see cref="uint"/> inputs into a well-distributed
/// <see cref="uint"/> (an FNV-1a accumulate followed by a Murmur3-style finalizer avalanche), and fold a hash into a
/// float in [0, 1). Unlike <see cref="XorRng"/> - a stateful stream whose next value depends on the prior draws - a
/// <see cref="Mix(uint)"/> is a PURE function of its inputs: the same inputs always yield the same hash on every
/// machine and every run, so it is the right tool for reproducible procedural content keyed off ids or coordinates (a
/// client deriving the SAME scatter pattern from an effect id, terrain detail keyed off a tile coordinate) with no
/// shared RNG stream to thread through. Kept as its own type for exactly that reason - one is a sequence, the other a
/// key-to-value map - but the two share ONE <see cref="ToUnitFloat"/> fold, so a hashed value and an
/// <see cref="XorRng.NextFloat"/> draw land in [0, 1) bit-identically off the same 32 bits. Distinct from the
/// string-keyed 64-bit <see cref="DeterministicRng.StableHash(string)"/> (used to derive a seed from a name): this is
/// the uint-keyed 32-bit sibling for combining numeric ids.
/// </summary>
public static class StableHash
{
    // FNV-1a 32-bit constants: the offset basis seeds the accumulator, the prime multiplies after each XOR-in.
    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    /// <summary>Hash a single <see cref="uint"/> into a well-distributed <see cref="uint"/>.</summary>
    public static uint Mix(uint a) => Avalanche((FnvOffsetBasis ^ a) * FnvPrime);

    /// <summary>Hash two <see cref="uint"/> inputs into a well-distributed <see cref="uint"/> (order significant).</summary>
    public static uint Mix(uint a, uint b)
    {
        uint h = (FnvOffsetBasis ^ a) * FnvPrime;
        h = (h ^ b) * FnvPrime;
        return Avalanche(h);
    }

    /// <summary>Hash three <see cref="uint"/> inputs into a well-distributed <see cref="uint"/> (order significant).</summary>
    public static uint Mix(uint a, uint b, uint c)
    {
        uint h = (FnvOffsetBasis ^ a) * FnvPrime;
        h = (h ^ b) * FnvPrime;
        h = (h ^ c) * FnvPrime;
        return Avalanche(h);
    }

    /// <summary>Fold a hash (or any 32-bit value) into a float uniformly in [0, 1), using the top 24 bits for a clean
    /// mantissa. This IS the fold <see cref="XorRng.NextFloat"/> applies to its draw, so the same 32 bits map to the
    /// same float whether they came from a hash or a stream draw.</summary>
    public static float ToUnitFloat(uint hash) => (hash >> 8) * (1.0f / 16777216.0f);

    // Murmur3-style finalizer (the "avalanche"): scrambles the accumulated FNV state so every input bit influences
    // every output bit. The uint multiplications wrap (the language default is unchecked), which is the intended mixing.
    private static uint Avalanche(uint h)
    {
        h ^= h >> 16;
        h *= 0x7feb352du;
        h ^= h >> 15;
        h *= 0x846ca68bu;
        h ^= h >> 16;
        return h;
    }
}
