namespace KhaozEngine.Particles;

/// <summary>
/// Tiny deterministic xorshift32 PRNG. Seeded by the <see cref="ParticleSystem"/> ctor so two systems
/// with the same seed and the same call sequence produce identical particles. No System.Random, no
/// wall-clock, no DateTime.
/// </summary>
internal struct Xorshift32
{
    private uint _state;

    public Xorshift32(uint seed)
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
    public float NextFloat()
    {
        // 24 bits => exact representation in a float, uniform in [0,1).
        return (NextUInt() >> 8) * (1.0f / 16777216.0f);
    }

    /// <summary>Float uniformly in [min, max) (half-open, since the underlying unit float is [0, 1)).</summary>
    public float Range(float min, float max)
    {
        if (max <= min)
        {
            return min;
        }

        return min + (max - min) * NextFloat();
    }
}
