using System;
using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// Capacity-bounded particle pool. Emit bursts, <see cref="Update"/> to age/integrate/interpolate/recycle.
/// Dead particles are swap-removed so the live ones stay compacted at the front of the buffer, exposed as
/// the contiguous <see cref="Active"/> span. Fully deterministic given a fixed seed and call sequence.
/// </summary>
public sealed class ParticleSystem
{
    private readonly Particle[] _particles;

    // Per-particle lerp endpoints + integration params, baked at Emit time so the EmitterConfig can be
    // discarded afterwards. Kept index-parallel to _particles and swap-removed in lockstep.
    private readonly float[] _startSize;
    private readonly float[] _endSize;
    private readonly Vector4[] _startColor;
    private readonly Vector4[] _endColor;
    private readonly Vector3[] _gravity;
    private readonly float[] _drag;

    private Xorshift32 _rng;
    private int _count;

    public ParticleSystem(int capacity, uint seed = 1)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be non-negative.");
        }

        _particles = new Particle[capacity];
        _startSize = new float[capacity];
        _endSize = new float[capacity];
        _startColor = new Vector4[capacity];
        _endColor = new Vector4[capacity];
        _gravity = new Vector3[capacity];
        _drag = new float[capacity];
        _rng = new Xorshift32(seed);
    }

    /// <summary>Maximum simultaneous live particles.</summary>
    public int Capacity => _particles.Length;

    /// <summary>Current live particle count.</summary>
    public int ActiveCount => _count;

    /// <summary>The live particles, contiguous, for a renderer to iterate.</summary>
    public ReadOnlySpan<Particle> Active => _particles.AsSpan(0, _count);

    /// <summary>
    /// Spawn a burst of up to <c>min(count, Capacity - ActiveCount)</c> particles at <paramref name="origin"/>.
    /// Excess is silently clamped.
    /// </summary>
    public void Emit(in EmitterConfig cfg, Vector3 origin, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int room = _particles.Length - _count;
        if (count > room)
        {
            count = room;
        }

        for (int i = 0; i < count; i++)
        {
            float life = _rng.Range(cfg.LifetimeMin, cfg.LifetimeMax);
            Vector3 dir = SampleConeDirection(cfg.Direction, cfg.SpreadDegrees);
            float speed = _rng.Range(cfg.SpeedMin, cfg.SpeedMax);

            int idx = _count++;
            _particles[idx] = new Particle
            {
                Position = origin,
                Velocity = dir * speed,
                Age = 0f,
                Life = life,
                Size = cfg.StartSize,
                Color = cfg.StartColor,
            };

            _startSize[idx] = cfg.StartSize;
            _endSize[idx] = cfg.EndSize;
            _startColor[idx] = cfg.StartColor;
            _endColor[idx] = cfg.EndColor;
            _gravity[idx] = cfg.Gravity;
            _drag[idx] = cfg.Drag;
        }
    }

    /// <summary>Age, integrate, interpolate and recycle every live particle by <paramref name="dt"/> seconds.</summary>
    public void Update(float dt)
    {
        int i = 0;
        while (i < _count)
        {
            ref Particle p = ref _particles[i];
            p.Age += dt;

            if (p.Age >= p.Life)
            {
                RecycleAt(i);
                // Do not advance i: the swapped-in particle now occupies this slot.
                continue;
            }

            p.Velocity += _gravity[i] * dt;
            float damp = 1f - _drag[i] * dt;
            if (damp < 0f)
            {
                damp = 0f;
            }
            p.Velocity *= damp;
            p.Position += p.Velocity * dt;

            float n = p.Norm;
            p.Size = Lerp(_startSize[i], _endSize[i], n);
            p.Color = Vector4.Lerp(_startColor[i], _endColor[i], n);

            i++;
        }
    }

    /// <summary>Kill all particles.</summary>
    public void Clear() => _count = 0;

    private void RecycleAt(int i)
    {
        int last = --_count;
        if (i != last)
        {
            _particles[i] = _particles[last];
            _startSize[i] = _startSize[last];
            _endSize[i] = _endSize[last];
            _startColor[i] = _startColor[last];
            _endColor[i] = _endColor[last];
            _gravity[i] = _gravity[last];
            _drag[i] = _drag[last];
        }
    }

    /// <summary>
    /// Sample a unit direction inside a cone of half-angle <paramref name="spreadDegrees"/> around
    /// <paramref name="axis"/>. A ~zero axis yields a uniform direction on the full sphere.
    /// </summary>
    private Vector3 SampleConeDirection(Vector3 axis, float spreadDegrees)
    {
        float axisLenSq = axis.LengthSquared();
        bool omni = axisLenSq < 1e-12f;

        // Clamp the half-angle. Omni or >=180 degrees => full sphere.
        float half = spreadDegrees;
        if (half < 0f)
        {
            half = 0f;
        }
        if (omni || half >= 180f)
        {
            return SampleSphere();
        }

        Vector3 a = axis / MathF.Sqrt(axisLenSq);

        // Uniform-in-cone: cosTheta in [cos(half), 1].
        float cosHalf = MathF.Cos(half * (MathF.PI / 180f));
        float cosTheta = cosHalf + (1f - cosHalf) * _rng.NextFloat();
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        float phi = (MathF.PI * 2f) * _rng.NextFloat();

        // Local direction with +Z as the cone axis.
        Vector3 local = new(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);

        // Build an orthonormal basis whose Z maps to the axis, then rotate local into world.
        BuildBasis(a, out Vector3 t, out Vector3 b);
        return Vector3.Normalize(t * local.X + b * local.Y + a * local.Z);
    }

    /// <summary>Uniform point on the unit sphere via the cosTheta-uniform method.</summary>
    private Vector3 SampleSphere()
    {
        float z = 1f - 2f * _rng.NextFloat();
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        float phi = (MathF.PI * 2f) * _rng.NextFloat();
        return new Vector3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
    }

    /// <summary>Two unit vectors orthogonal to <paramref name="n"/> and to each other.</summary>
    private static void BuildBasis(Vector3 n, out Vector3 t, out Vector3 b)
    {
        // Pick a helper axis least aligned with n to avoid a degenerate cross product.
        Vector3 helper = MathF.Abs(n.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
        t = Vector3.Normalize(Vector3.Cross(helper, n));
        b = Vector3.Cross(n, t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
