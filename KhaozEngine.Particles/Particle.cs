using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Particles;

/// <summary>
/// Live particle state (current interpolated values). Public so a renderer can read it from
/// <see cref="ParticleSystem.Active"/>.
/// </summary>
public struct Particle
{
    /// <summary>World position.</summary>
    public Vector3 Position;

    /// <summary>World velocity (units/second).</summary>
    public Vector3 Velocity;

    /// <summary>Seconds since spawn.</summary>
    public float Age;

    /// <summary>Total lifetime in seconds.</summary>
    public float Life;

    /// <summary>Current (interpolated) size.</summary>
    public float Size;

    /// <summary>Current (interpolated) RGBA colour.</summary>
    public Color Color;

    /// <summary>Current billboard rotation in radians (integrated from the emitter's spin).</summary>
    public float Rotation;

    /// <summary>Stable per-particle randomiser in [0,1), hashed from a monotonic emit counter (consumes no
    /// RNG draw). Drives per-particle variation in a renderer or the turbulence field.</summary>
    public float Seed;

    /// <summary>True while the particle has not exceeded its lifetime.</summary>
    public readonly bool Alive => Age < Life;

    /// <summary>Normalised age in 0..1 over the lifetime (used for lerp).</summary>
    public readonly float Norm => Life > 0f ? Age / Life : 1f;
}
