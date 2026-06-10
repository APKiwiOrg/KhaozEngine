using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>
/// Fixed-size, zero-allocation pool of rectangle particles. Emit bursts from
/// <see cref="ParticleEmitterConfig"/> presets; one system can mix particles from
/// different presets. Update with real (unscaled) delta so effects stay smooth
/// regardless of game speed.
/// </summary>
public sealed class ParticleSystem
{
    private struct Particle
    {
        public float X, Y;
        public float VelX, VelY;
        public float Life, MaxLife;
        public float StartSize, EndSizeFactor;
        public float AccelX, AccelY;
        public float SwayFrequency, SwayAmplitude, Phase;
        public Color Color;
    }

    private readonly Particle[] _particles;
    private readonly Random _rng;
    private int _cursor;

    /// <summary>Creates a system with a seeded RNG and pool capacity (default 80).</summary>
    public ParticleSystem(Random rng, int poolSize = 80)
    {
        if (poolSize <= 0) throw new ArgumentOutOfRangeException(nameof(poolSize));
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        _particles = new Particle[poolSize];
    }

    /// <summary>Number of currently live particles.</summary>
    public int ActiveCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _particles.Length; i++)
                if (_particles[i].Life > 0f) n++;
            return n;
        }
    }

    /// <summary>Emits <paramref name="count"/> particles at <paramref name="position"/> using White as the base color.</summary>
    public void Emit(ParticleEmitterConfig config, Vector2 position, int count)
        => Emit(config, position, Color.White, count);

    /// <summary>Emits <paramref name="count"/> particles at <paramref name="position"/>, blending from <paramref name="baseColor"/>.</summary>
    public void Emit(ParticleEmitterConfig config, Vector2 position, Color baseColor, int count)
    {
        Color color = config.OverrideColor
            ?? Color.Lerp(baseColor, config.BlendTarget, config.BlendAmount);

        for (int i = 0; i < count; i++)
        {
            float speed = config.MinSpeed + (float)(_rng.NextDouble() * (config.MaxSpeed - config.MinSpeed));
            float life = config.MinLife + (float)(_rng.NextDouble() * (config.MaxLife - config.MinLife));

            float vx, vy;
            if (config.Emission == ParticleEmission.Radial)
            {
                double angle = _rng.NextDouble() * Math.PI * 2.0;
                vx = (float)Math.Cos(angle) * speed;
                vy = (float)Math.Sin(angle) * speed;
            }
            else
            {
                float baseAngle = (float)Math.Atan2(config.Direction.Y, config.Direction.X);
                float spread = config.SpreadRadians <= 0f
                    ? 0f
                    : (float)((_rng.NextDouble() * 2.0 - 1.0) * config.SpreadRadians);
                float angle = baseAngle + spread;
                vx = (float)Math.Cos(angle) * speed;
                vy = (float)Math.Sin(angle) * speed;
            }

            float offsetX = (float)((_rng.NextDouble() * 2.0 - 1.0) * config.JitterX);
            float offsetY = (float)((_rng.NextDouble() * 2.0 - 1.0) * config.JitterY);

            ref Particle p = ref _particles[_cursor];
            p.X = position.X + offsetX;
            p.Y = position.Y + offsetY;
            p.VelX = vx;
            p.VelY = vy;
            p.Life = life;
            p.MaxLife = life;
            p.StartSize = config.StartSize;
            p.EndSizeFactor = config.EndSizeFactor;
            p.AccelX = config.Acceleration.X;
            p.AccelY = config.Acceleration.Y;
            p.SwayFrequency = config.SwayFrequency;
            p.SwayAmplitude = config.SwayAmplitude;
            p.Phase = config.SwayAmplitude > 0f ? (float)(_rng.NextDouble() * Math.PI * 2.0) : 0f;
            p.Color = color;

            _cursor = (_cursor + 1) % _particles.Length;
        }
    }
}
