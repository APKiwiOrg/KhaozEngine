using System;
using System.Numerics;
using KhaozEngine.Primitives;

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
    private readonly Color[] _startColor;
    private readonly Color[] _endColor;
    private readonly Vector3[] _gravity;
    private readonly float[] _drag;

    // Modernisation bakes (also index-parallel + swap-removed). Zero/default values reproduce legacy behaviour.
    private readonly Color[] _midColor;
    private readonly bool[] _hasMid;
    private readonly ParticleCurve[] _sizeCurve;
    private readonly ParticleCurve[] _alphaCurve;
    private readonly float[] _spin;
    private readonly float[] _turbStrength;
    private readonly float[] _turbFreq;

    private XorRng _rng;
    private int _count;

    // Monotonic emit counter feeding the per-particle Seed hash (never an RNG draw).
    private uint _emitCounter;

    // Accumulated simulation time (seconds), advanced once per Update, drives the turbulence field.
    private float _time;

    public ParticleSystem(int capacity, uint seed = 1)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be non-negative.");
        }

        _particles = new Particle[capacity];
        _startSize = new float[capacity];
        _endSize = new float[capacity];
        _startColor = new Color[capacity];
        _endColor = new Color[capacity];
        _gravity = new Vector3[capacity];
        _drag = new float[capacity];
        _midColor = new Color[capacity];
        _hasMid = new bool[capacity];
        _sizeCurve = new ParticleCurve[capacity];
        _alphaCurve = new ParticleCurve[capacity];
        _spin = new float[capacity];
        _turbStrength = new float[capacity];
        _turbFreq = new float[capacity];
        _rng = new XorRng(seed);
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
            // RNG draw discipline: the legacy prefix (life, direction, speed) stays byte-identical for a
            // zero-default config. Every new feature's draws are gated and appended after that prefix, so an
            // unmodernised burst consumes exactly the historical sequence.
            float life = _rng.Range(cfg.LifetimeMin, cfg.LifetimeMax);

            // Direction is drawn up-front only in Cone mode; Radial derives it from the spawn offset below.
            Vector3 coneDir = default;
            if (cfg.VelocityMode == ParticleVelocityMode.Cone)
            {
                coneDir = SampleConeDirection(cfg.Direction, cfg.SpreadDegrees);
            }

            float speed = _rng.Range(cfg.SpeedMin, cfg.SpeedMax);

            // Shape offset draws come after the legacy prefix.
            Vector3 offset = cfg.Shape == EmissionShape.Point ? Vector3.Zero : SampleShapeOffset(cfg);
            Vector3 spawnPos = origin + offset;

            Vector3 dir;
            if (cfg.VelocityMode == ParticleVelocityMode.Radial)
            {
                float offLenSq = offset.LengthSquared();
                // Outward through the spawn point when it is off the origin, else fall back to a sphere draw.
                dir = offLenSq > 1e-12f ? offset / MathF.Sqrt(offLenSq) : SampleSphere();
            }
            else
            {
                dir = coneDir;
            }

            // Appended per-feature draws, in the fixed order: start rotation, spin, size variance, colour t.
            float rotation = cfg.RandomStartRotation ? _rng.NextFloat() * (MathF.PI * 2f) : 0f;
            float spin = (cfg.SpinMin != 0f || cfg.SpinMax != 0f) ? _rng.Range(cfg.SpinMin, cfg.SpinMax) : 0f;

            float sizeMul = 1f;
            if (cfg.SizeVariance > 0f)
            {
                float u = _rng.NextFloat();
                sizeMul = 1f + cfg.SizeVariance * (2f * u - 1f);
            }

            Color startCol = cfg.StartColor;
            Color endCol = cfg.EndColor;
            if (cfg.VaryColor)
            {
                float ct = _rng.NextFloat();
                startCol = Color.Lerp(cfg.StartColor, cfg.StartColorB, ct);
                endCol = Color.Lerp(cfg.EndColor, cfg.EndColorB, ct);
            }

            float startSize = cfg.StartSize * sizeMul;
            float endSize = cfg.EndSize * sizeMul;
            // Seed is hashed from the monotonic emit counter, never an RNG draw.
            float seed = SeedFromCounter(_emitCounter++);

            int idx = _count++;
            _particles[idx] = new Particle
            {
                Position = spawnPos,
                Velocity = dir * speed,
                Age = 0f,
                Life = life,
                Size = startSize,
                Color = startCol,
                Rotation = rotation,
                Seed = seed,
            };

            _startSize[idx] = startSize;
            _endSize[idx] = endSize;
            _startColor[idx] = startCol;
            _endColor[idx] = endCol;
            _gravity[idx] = cfg.Gravity;
            _drag[idx] = cfg.Drag;
            _midColor[idx] = cfg.MidColor;
            _hasMid[idx] = cfg.UseMidColor;
            _sizeCurve[idx] = cfg.SizeCurve;
            _alphaCurve[idx] = cfg.AlphaCurve;
            _spin[idx] = spin;
            _turbStrength[idx] = cfg.TurbulenceStrength;
            _turbFreq[idx] = cfg.TurbulenceFrequency;
        }
    }

    /// <summary>Age, integrate, interpolate and recycle every live particle by <paramref name="dt"/> seconds.</summary>
    public void Update(float dt)
    {
        _time += dt;

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

            if (_turbStrength[i] > 0f)
            {
                float freq = _turbFreq[i] <= 0f ? 1f : _turbFreq[i];
                p.Velocity += ParticleNoise.Curl(p.Position * freq, _time * freq, p.Seed) * (_turbStrength[i] * dt);
            }

            p.Position += p.Velocity * dt;
            p.Rotation += _spin[i] * dt;

            float n = p.Norm;

            ParticleCurve sizeCurve = _sizeCurve[i];
            ParticleCurve alphaCurve = _alphaCurve[i];
            if (sizeCurve.Kind == ParticleCurveKind.Linear && alphaCurve.Kind == ParticleCurveKind.Linear && !_hasMid[i])
            {
                // Legacy path: the exact historical single lerps so results stay bit-identical.
                p.Size = MathUtil.Lerp(_startSize[i], _endSize[i], n);
                p.Color = (Color)Vector4.Lerp(_startColor[i], _endColor[i], n);
            }
            else
            {
                p.Size = MathUtil.Lerp(_startSize[i], _endSize[i], sizeCurve.Evaluate(n));

                // Colour rgb tracks the raw norm; alpha tracks the alpha curve. Both walk the same 2- or 3-stop
                // gradient, so a mid stop applies to both channels.
                float an = alphaCurve.Evaluate(n);
                Color rgb = GradientAt(n, _hasMid[i], _startColor[i], _midColor[i], _endColor[i]);
                Color alpha = GradientAt(an, _hasMid[i], _startColor[i], _midColor[i], _endColor[i]);
                p.Color = new Color(rgb.R, rgb.G, rgb.B, alpha.A);
            }

            i++;
        }
    }

    /// <summary>A 2-stop (start to end) or, when <paramref name="hasMid"/>, 3-stop (start to mid at 0.5 to end) colour lerp at <paramref name="t"/>.</summary>
    private static Color GradientAt(float t, bool hasMid, Color start, Color mid, Color end)
    {
        if (!hasMid)
        {
            return Color.Lerp(start, end, t);
        }
        return t < 0.5f ? Color.Lerp(start, mid, t * 2f) : Color.Lerp(mid, end, (t - 0.5f) * 2f);
    }

    /// <summary>Map the monotonic emit counter to a stable per-particle value in [0,1) via a Wang integer hash.</summary>
    private static float SeedFromCounter(uint c)
    {
        uint h = c;
        h = (h ^ 61u) ^ (h >> 16);
        h += h << 3;
        h ^= h >> 4;
        h *= 0x27D4EB2Du;
        h ^= h >> 15;
        return (h >> 8) * (1f / 16777216.0f);
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
            _midColor[i] = _midColor[last];
            _hasMid[i] = _hasMid[last];
            _sizeCurve[i] = _sizeCurve[last];
            _alphaCurve[i] = _alphaCurve[last];
            _spin[i] = _spin[last];
            _turbStrength[i] = _turbStrength[last];
            _turbFreq[i] = _turbFreq[last];
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

    /// <summary>
    /// Sample a spawn offset (from the origin) inside/on the configured <see cref="EmissionShape"/>. Draws in
    /// a fixed order so the modernised burst stays deterministic. Never called for
    /// <see cref="EmissionShape.Point"/>.
    /// </summary>
    private Vector3 SampleShapeOffset(in EmitterConfig cfg)
    {
        switch (cfg.Shape)
        {
            case EmissionShape.Sphere:
            {
                Vector3 dir = SampleSphere();
                float u = _rng.NextFloat();
                float r = cfg.ShapeRadius * MathUtil.Lerp(MathF.Cbrt(u), 1f, cfg.ShapeShell);
                return dir * r;
            }
            case EmissionShape.Hemisphere:
            {
                Vector3 dir = SampleSphere();
                float u = _rng.NextFloat();
                float r = cfg.ShapeRadius * MathUtil.Lerp(MathF.Cbrt(u), 1f, cfg.ShapeShell);
                Vector3 offset = dir * r;
                Vector3 axis = SafeAxis(cfg.Direction);
                float d = Vector3.Dot(offset, axis);
                if (d < 0f)
                {
                    // Fold the below-axis half up so the dome opens along the axis.
                    offset -= 2f * d * axis;
                }
                return offset;
            }
            case EmissionShape.Disc:
            {
                Vector3 axis = SafeAxis(cfg.Direction);
                BuildBasis(axis, out Vector3 t, out Vector3 b);
                float phi = (MathF.PI * 2f) * _rng.NextFloat();
                float u = _rng.NextFloat();
                float r = cfg.ShapeRadius * MathUtil.Lerp(MathF.Sqrt(u), 1f, cfg.ShapeShell);
                return (t * MathF.Cos(phi) + b * MathF.Sin(phi)) * r;
            }
            default:
                return Vector3.Zero;
        }
    }

    /// <summary>Unit axis from <paramref name="dir"/>, defaulting to <c>+Y</c> when it is ~zero.</summary>
    private static Vector3 SafeAxis(Vector3 dir)
    {
        float lenSq = dir.LengthSquared();
        return lenSq < 1e-12f ? Vector3.UnitY : dir / MathF.Sqrt(lenSq);
    }

    /// <summary>Two unit vectors orthogonal to <paramref name="n"/> and to each other.</summary>
    private static void BuildBasis(Vector3 n, out Vector3 t, out Vector3 b)
    {
        // Pick a helper axis least aligned with n to avoid a degenerate cross product.
        Vector3 helper = MathF.Abs(n.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
        t = Vector3.Normalize(Vector3.Cross(helper, n));
        b = Vector3.Cross(n, t);
    }

}
