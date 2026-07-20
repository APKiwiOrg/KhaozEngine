using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// A world-space point pull applied to every live particle in a <see cref="ParticleSystem"/> whose config
/// did not opt out via <see cref="EmitterConfig.IgnoreAttractor"/>. Re-assign to
/// <see cref="ParticleSystem.Attractor"/> each frame to track a moving target. Setting the property to null
/// releases the particles to free drift: they keep their velocity and fade out on their own lifetimes.
/// A particle that enters <see cref="KillRadius"/> is absorbed: removed and reported through the system's
/// absorb counters and <see cref="ParticleSystem.OnAbsorbed"/>.
/// </summary>
public struct ParticleAttractor
{
    /// <summary>World-space point particles accelerate toward.</summary>
    public Vector3 Target;

    /// <summary>Peak pull acceleration in world units per second squared. &lt;= 0 disables the pull
    /// (absorption via <see cref="KillRadius"/> still applies).</summary>
    public float Strength;

    /// <summary>Pull envelope over each particle's normalised age: the effective pull is
    /// <c>Strength * StrengthCurve.Evaluate(n)</c>. The default (<see cref="ParticleCurveKind.Linear"/>)
    /// ramps 0 to 1 across the lifetime, a beat of free drift before the pull takes over. EaseIn holds the
    /// drift longer, <see cref="ParticleCurve.One"/> pulls at full strength from birth.</summary>
    public ParticleCurve StrengthCurve;

    /// <summary>Absorb radius around <see cref="Target"/> in world units. &lt;= 0 disables absorption.</summary>
    public float KillRadius;

    /// <summary>Speed cap in world units per second, applied while an attractor is set. &lt;= 0 leaves
    /// speed unclamped.</summary>
    public float MaxSpeed;
}
