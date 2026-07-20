using System;
using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// One layer of an authored effect: an <see cref="EmitterConfig"/> plus its scheduling. A phase can be an
/// instant burst (<see cref="Duration"/> 0 with a positive <see cref="BurstCount"/>), a timed stream
/// (<see cref="RatePerSecond"/> &gt; 0 while active), or both.
/// </summary>
public struct ParticleEffectPhase
{
    /// <summary>The emitter this phase drives. Its <c>Direction</c> is rotated onto the played direction at spawn.</summary>
    public EmitterConfig Config;

    /// <summary>Seconds after an effect is played before this phase starts.</summary>
    public float Delay;

    /// <summary>Seconds the stream stays active after <see cref="Delay"/>. 0 with <see cref="BurstCount"/> &gt; 0 is an instant burst.</summary>
    public float Duration;

    /// <summary>Particles per second emitted while the phase is active. 0 disables streaming.</summary>
    public float RatePerSecond;

    /// <summary>Particles emitted once when the phase starts.</summary>
    public int BurstCount;

    /// <summary>Pool capacity for this phase's particle system. &lt;= 0 defaults to 256.</summary>
    public int PoolCapacity;

    /// <summary>Effect-local offset of this phase's emission origin, authored with +Y as the effect axis and
    /// rotated with the played direction (a ground ring lifts slightly off the surface, a muzzle phase sits
    /// ahead of the hand). Zero keeps the played origin.</summary>
    public Vector3 OriginOffset;

    /// <summary>Per-particle trail history depth for this phase's pool. 0 disables trails.</summary>
    public int TrailSamples;

    /// <summary>Optional emission-rate envelope over the phase's active window: the effective stream rate is
    /// <see cref="RatePerSecond"/> times <c>RateCurve.Evaluate(local / Duration)</c>. Null (the default) keeps
    /// the flat legacy rate. Bursts are unaffected.</summary>
    public ParticleCurve? RateCurve;
}

/// <summary>
/// An immutable, authored multi-phase effect (for example impact = flash burst + spark burst + smoke stream +
/// ring). Play instances of it through a <see cref="ParticleEffectPlayer"/>.
/// </summary>
public sealed class ParticleEffect
{
    private readonly ParticleEffectPhase[] _phases;

    /// <summary>Build an effect from its phases. The array is defensively copied.</summary>
    public ParticleEffect(params ParticleEffectPhase[] phases)
    {
        _phases = phases is null || phases.Length == 0
            ? Array.Empty<ParticleEffectPhase>()
            : (ParticleEffectPhase[])phases.Clone();
    }

    /// <summary>Number of phases.</summary>
    public int PhaseCount => _phases.Length;

    /// <summary>The phase at <paramref name="index"/> (a copy: phases are value types).</summary>
    public ParticleEffectPhase GetPhase(int index) => _phases[index];
}
