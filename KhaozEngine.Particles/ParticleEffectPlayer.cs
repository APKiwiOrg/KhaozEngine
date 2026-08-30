using System;
using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// Plays bounded concurrent instances of a <see cref="ParticleEffect"/>. Owns one <see cref="ParticleSystem"/>
/// pool per phase (so mixed per-phase looks stay renderable) and schedules each instance's bursts and streams
/// from a per-instance clock. Deterministic given the ctor seed and the call sequence. Headless: no rendering.
/// <para><b>A pool is per PHASE, not per instance.</b> Every concurrent <see cref="Play"/> emits into the same
/// pool for a given phase, so a phase whose <c>PoolCapacity</c> only fits one instance's burst will clamp the
/// second overlapping play. That is a sizing decision the effect author owns, and
/// <see cref="DroppedLastUpdate"/> / <see cref="DroppedTotal"/> are how it stops being invisible (issue
/// #124): size a phase for the bursts the game really overlaps, and watch the counter to know when it is
/// wrong.</para>
/// </summary>
public sealed class ParticleEffectPlayer
{
    private readonly ParticleEffect _effect;
    private readonly int _phaseCount;
    private readonly int _maxInstances;
    private readonly ParticleSystem[] _pools;

    // Per-instance state.
    private readonly bool[] _instActive;
    private readonly float[] _instAge;
    private readonly Vector3[] _instOrigin;
    private readonly Quaternion[] _instRotation;

    // Per-instance, per-phase scheduling state (row-major: instance * phaseCount + phase).
    private readonly RateAccumulator[] _rateAcc;
    private readonly bool[] _burstFired;

    private readonly float _maxPhaseEnd;

    private ParticleAttractor? _attractor;
    private Action<Particle>? _onAbsorbed;

    /// <summary>Build a player for <paramref name="effect"/> with up to <paramref name="maxInstances"/> concurrent instances.</summary>
    public ParticleEffectPlayer(ParticleEffect effect, int maxInstances = 8, uint seed = 1)
    {
        _effect = effect ?? throw new ArgumentNullException(nameof(effect));
        if (maxInstances <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInstances), "Instance count must be positive.");
        }

        _phaseCount = effect.PhaseCount;
        _maxInstances = maxInstances;

        _pools = new ParticleSystem[_phaseCount];
        float maxEnd = 0f;
        for (int ph = 0; ph < _phaseCount; ph++)
        {
            ParticleEffectPhase phase = effect.GetPhase(ph);
            int cap = phase.PoolCapacity <= 0 ? 256 : phase.PoolCapacity;
            _pools[ph] = new ParticleSystem(cap, seed + (uint)ph, phase.TrailSamples);
            float end = phase.Delay + phase.Duration;
            if (end > maxEnd)
            {
                maxEnd = end;
            }
        }
        _maxPhaseEnd = maxEnd;

        _instActive = new bool[maxInstances];
        _instAge = new float[maxInstances];
        _instOrigin = new Vector3[maxInstances];
        _instRotation = new Quaternion[maxInstances];
        _rateAcc = new RateAccumulator[maxInstances * _phaseCount];
        _burstFired = new bool[maxInstances * _phaseCount];
    }

    /// <summary>Number of phases (and phase pools).</summary>
    public int PhaseCount => _phaseCount;

    /// <summary>The particle pool for <paramref name="phaseIndex"/>, for a renderer to read.</summary>
    public ParticleSystem PhaseSystem(int phaseIndex) => _pools[phaseIndex];

    /// <summary>The attractor applied to every phase pool, or null for none. Re-assign each frame to track a
    /// moving target. Setting null releases live particles to drift and fade on their own lifetimes.
    /// A phase opts out via <see cref="EmitterConfig.IgnoreAttractor"/> on its config.</summary>
    public ParticleAttractor? Attractor
    {
        get => _attractor;
        set
        {
            _attractor = value;
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                _pools[ph].Attractor = value;
            }
        }
    }

    /// <summary>Handler invoked whenever any phase pool absorbs a particle via its attractor's kill radius.
    /// Forwarded to every phase pool.</summary>
    public Action<Particle>? OnAbsorbed
    {
        get => _onAbsorbed;
        set
        {
            _onAbsorbed = value;
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                _pools[ph].OnAbsorbed = value;
            }
        }
    }

    /// <summary>Particles absorbed by an attractor's kill radius across every phase pool during the last
    /// <see cref="Update"/> call.</summary>
    public int AbsorbedLastUpdate
    {
        get
        {
            int sum = 0;
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                sum += _pools[ph].AbsorbedLastUpdate;
            }
            return sum;
        }
    }

    /// <summary>Total particles absorbed by an attractor's kill radius across every phase pool over the
    /// player's lifetime.</summary>
    public int AbsorbedTotal
    {
        get
        {
            int sum = 0;
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                sum += _pools[ph].AbsorbedTotal;
            }
            return sum;
        }
    }

    /// <summary>Particles this player's phase pools could not fit during the last <see cref="Update"/>, summed
    /// over every phase (issue #124). A pool is per PHASE and shared by every concurrent instance, while
    /// <see cref="Play"/> is per INSTANCE, so two overlapping plays of the same effect compete for one pool's
    /// room and the loser's burst is clamped. That used to be silent, and read only as "the second explosion
    /// had fewer particles". A non-zero value here means a phase's <c>PoolCapacity</c> is too small for the
    /// bursts actually in flight: size it for the concurrency the game really plays, not for one instance.</summary>
    public int DroppedLastUpdate { get; private set; }

    /// <summary>Particles dropped for want of pool room over this player's lifetime, summed over every phase.
    /// Survives <see cref="Clear"/> (lifetime telemetry, not state), so a headless test or a debug overlay can
    /// watch a whole encounter rather than one frame.</summary>
    public int DroppedTotal
    {
        get
        {
            int sum = 0;
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                sum += _pools[ph].DroppedTotal;
            }
            return sum;
        }
    }

    /// <summary>Runtime multiplier on every phase's stream rate (bursts unaffected). Default 1. Drive it per
    /// frame to tie emission to an external ramp, for example a dissolve threshold. Values &lt;= 0 emit
    /// nothing. Does not affect already-live particles.</summary>
    public float RateScale { get; set; } = 1f;

    /// <summary>True while any instance is still scheduling or any phase pool still holds live particles.</summary>
    public bool AnyAlive
    {
        get
        {
            for (int i = 0; i < _maxInstances; i++)
            {
                if (_instActive[i])
                {
                    return true;
                }
            }
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                if (_pools[ph].ActiveCount > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Start one instance at <paramref name="origin"/> aimed along <paramref name="direction"/> (each phase's
    /// emitter Direction is rotated from +Y onto it). Returns false when every instance slot is busy.
    /// </summary>
    public bool Play(Vector3 origin, Vector3 direction)
    {
        for (int i = 0; i < _maxInstances; i++)
        {
            if (_instActive[i])
            {
                continue;
            }

            _instActive[i] = true;
            _instAge[i] = 0f;
            _instOrigin[i] = origin;
            _instRotation[i] = RotationFromYTo(direction);
            for (int ph = 0; ph < _phaseCount; ph++)
            {
                int slot = i * _phaseCount + ph;
                _burstFired[slot] = false;
                _rateAcc[slot].Reset();
            }
            return true;
        }

        return false;
    }

    /// <summary>Advance every instance's schedule and then step each phase pool once by <paramref name="dt"/>.</summary>
    public void Update(float dt)
    {
        // Every phase pool is shared by every instance, so what a burst LOSES is only visible as a difference
        // across the scheduling pass. Snapshot before, diff after (issue #124).
        int droppedBefore = DroppedTotal;

        for (int i = 0; i < _maxInstances; i++)
        {
            if (!_instActive[i])
            {
                continue;
            }

            _instAge[i] += dt;
            float age = _instAge[i];

            for (int ph = 0; ph < _phaseCount; ph++)
            {
                ParticleEffectPhase phase = _effect.GetPhase(ph);
                float local = age - phase.Delay;
                if (local < 0f)
                {
                    continue;
                }

                int slot = i * _phaseCount + ph;
                if (!_burstFired[slot] && phase.BurstCount > 0)
                {
                    EmitInto(ph, i, phase, phase.BurstCount);
                    _burstFired[slot] = true;
                }

                if (phase.RatePerSecond > 0f && local <= phase.Duration)
                {
                    float scale = RateScale > 0f ? RateScale : 0f;
                    float rate = phase.RatePerSecond * scale;
                    if (phase.RateCurve.HasValue && rate > 0f)
                    {
                        float norm = phase.Duration > 0f ? Math.Clamp(local / phase.Duration, 0f, 1f) : 1f;
                        rate *= phase.RateCurve.Value.Evaluate(norm);
                    }
                    int c = _rateAcc[slot].Advance(dt, rate);
                    if (c > 0)
                    {
                        EmitInto(ph, i, phase, c);
                    }
                }
            }

            if (age > _maxPhaseEnd)
            {
                // Done scheduling. The pools keep draining their live particles on their own.
                _instActive[i] = false;
            }
        }

        for (int ph = 0; ph < _phaseCount; ph++)
        {
            _pools[ph].Update(dt);
        }

        DroppedLastUpdate = DroppedTotal - droppedBefore;
    }

    /// <summary>Stop every instance and clear every phase pool.</summary>
    public void Clear()
    {
        for (int i = 0; i < _maxInstances; i++)
        {
            _instActive[i] = false;
        }
        for (int ph = 0; ph < _phaseCount; ph++)
        {
            _pools[ph].Clear();
        }
    }

    private void EmitInto(int phaseIndex, int instance, in ParticleEffectPhase phase, int count)
    {
        EmitterConfig cfg = phase.Config;
        cfg.Direction = Vector3.Transform(phase.Config.Direction, _instRotation[instance]);
        Vector3 origin = _instOrigin[instance] + Vector3.Transform(phase.OriginOffset, _instRotation[instance]);
        _pools[phaseIndex].Emit(cfg, origin, count);
    }

    /// <summary>Quaternion rotating +Y onto <paramref name="dir"/> (identity when the direction is ~zero).</summary>
    private static Quaternion RotationFromYTo(Vector3 dir)
    {
        float lenSq = dir.LengthSquared();
        if (lenSq < 1e-12f)
        {
            return Quaternion.Identity;
        }

        Vector3 to = dir / MathF.Sqrt(lenSq);
        Vector3 from = Vector3.UnitY;
        float d = Vector3.Dot(from, to);
        if (d >= 0.99999f)
        {
            return Quaternion.Identity;
        }
        if (d <= -0.99999f)
        {
            // Opposite: any axis perpendicular to +Y works. Use +X for a stable 180 degree flip.
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }

        Vector3 axis = Vector3.Normalize(Vector3.Cross(from, to));
        float angle = MathF.Acos(Math.Clamp(d, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }
}
