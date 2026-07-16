using System;
using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// Plays bounded concurrent instances of a <see cref="ParticleEffect"/>. Owns one <see cref="ParticleSystem"/>
/// pool per phase (so mixed per-phase looks stay renderable) and schedules each instance's bursts and streams
/// from a per-instance clock. Deterministic given the ctor seed and the call sequence. Headless: no rendering.
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
                    int c = _rateAcc[slot].Advance(dt, phase.RatePerSecond);
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
