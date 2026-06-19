namespace KhaozEngine.Primitives;

/// <summary>
/// Tiny deterministic xorshift32 PRNG as a value type, for allocation-free hot paths (particle emission,
/// audio noise). Two instances with the same seed and call sequence produce identical streams. Copy the
/// struct to snapshot. No System.Random, no wall-clock. For resumable/derivable streams use
/// <see cref="DeterministicRng"/> instead.
/// </summary>
public struct XorRng
{
    private uint _state;

    public XorRng(uint seed)
    {
        // xorshift collapses to 0 forever if seeded with 0; map it to a non-zero constant.
        _state = seed != 0u ? seed : 0x9E3779B9u;
    }

    /// <summary>Next raw 32-bit value.</summary>
    public uint NextUInt()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Float in [0, 1). Uses the top 24 bits for a clean mantissa.</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    /// <summary>Float uniformly in [min, max). Degenerate (max &lt;= min) returns min.</summary>
    public float Range(float min, float max) => max <= min ? min : min + (max - min) * NextFloat();
}
