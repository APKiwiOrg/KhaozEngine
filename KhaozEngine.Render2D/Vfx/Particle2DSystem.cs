using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Fixed-size, zero-allocation pool of 2D (screen-space) particles. Emit bursts or a continuous stream from a
    /// <see cref="Particle2DEmitterConfig"/>; one system can mix particles from different presets. Per particle:
    /// velocity, constant acceleration (gravity), drag (velocity damping), horizontal sway, rotation + angular
    /// velocity, size lerp and colour lerp over life, and a per-particle <see cref="BlendMode"/>. The pool is a
    /// ring buffer - emitting into a full pool overwrites the oldest particles. Motion is driven by a deterministic
    /// seeded <see cref="XorRng"/> (not <see cref="System.Random"/>), so a given seed + call sequence is fully
    /// reproducible and headless-testable. Render-agnostic: <see cref="Draw(SpriteBatch, Texture2D)"/> takes the
    /// batch plus a texture (a 1x1 white pixel for solid squares, or a baked glow dot for soft sprites).
    /// </summary>
    public sealed class Particle2DSystem
    {
        struct Particle
        {
            public Vector2 Pos;
            public Vector2 Vel;
            public Vector2 Accel;
            public float Drag;
            public float Life, MaxLife;
            public float StartSize, EndSize;
            public float SwayFrequency, SwayAmplitude, Phase;
            public float Rotation, AngularVelocity;
            public Color StartColor, EndColor;
            public BlendMode Blend;
        }

        readonly Particle[] _particles;
        XorRng _rng;
        int _cursor;

        /// <summary>Creates a system with pool <paramref name="capacity"/> (default 256) seeded by <paramref name="seed"/>.</summary>
        public Particle2DSystem(int capacity = 256, uint seed = 1u)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _particles = new Particle[capacity];
            _rng = new XorRng(seed);
        }

        /// <summary>Pool capacity (the maximum number of simultaneously live particles).</summary>
        public int Capacity => _particles.Length;

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

        /// <summary>Emits <paramref name="count"/> particles at <paramref name="origin"/> using White as the tint.</summary>
        public void Emit(in Particle2DEmitterConfig cfg, Vector2 origin, int count)
            => Emit(cfg, origin, Color.White, count);

        /// <summary>
        /// Emits <paramref name="count"/> particles at <paramref name="origin"/>, multiplying the preset's
        /// start/end colours by <paramref name="tint"/>. The pool is a ring buffer: emitting into a full pool
        /// overwrites the oldest live particles.
        /// </summary>
        public void Emit(in Particle2DEmitterConfig cfg, Vector2 origin, Color tint, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            Color startColor = MultiplyColor(cfg.StartColor, tint);
            Color endColor = MultiplyColor(cfg.EndColor, tint);
            float baseAngle = MathF.Atan2(cfg.Direction.Y, cfg.Direction.X);

            for (int i = 0; i < count; i++)
            {
                // Fixed RNG draw order so a seed reproduces exactly (collapsed ranges consume nothing).
                float speed = _rng.Range(cfg.MinSpeed, cfg.MaxSpeed);
                float life = _rng.Range(cfg.MinLife, cfg.MaxLife);

                float angle;
                if (cfg.Emission == Particle2DEmission.Radial)
                    angle = _rng.Range(0f, MathF.PI * 2f);
                else
                    angle = baseAngle + (cfg.SpreadRadians > 0f ? _rng.Range(-cfg.SpreadRadians, cfg.SpreadRadians) : 0f);

                float offsetX = _rng.Range(-cfg.JitterX, cfg.JitterX);
                float offsetY = _rng.Range(-cfg.JitterY, cfg.JitterY);
                float rotation = cfg.RotationJitter > 0f ? _rng.Range(-cfg.RotationJitter, cfg.RotationJitter) : 0f;
                float angularVel = _rng.Range(cfg.MinAngularVelocity, cfg.MaxAngularVelocity);
                float phase = cfg.SwayAmplitude > 0f ? _rng.Range(0f, MathF.PI * 2f) : 0f;

                ref Particle p = ref _particles[_cursor];
                p.Pos = new Vector2(origin.X + offsetX, origin.Y + offsetY);
                p.Vel = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
                p.Accel = cfg.Acceleration;
                p.Drag = cfg.Drag;
                p.Life = life;
                p.MaxLife = life;
                p.StartSize = cfg.StartSize;
                p.EndSize = cfg.EndSize;
                p.SwayFrequency = cfg.SwayFrequency;
                p.SwayAmplitude = cfg.SwayAmplitude;
                p.Phase = phase;
                p.Rotation = rotation;
                p.AngularVelocity = angularVel;
                p.StartColor = startColor;
                p.EndColor = endColor;
                p.Blend = cfg.Blend;

                _cursor = (_cursor + 1) % _particles.Length;
            }
        }

        /// <summary>Advances all live particles by <paramref name="dt"/> seconds.</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref Particle p = ref _particles[i];
                if (p.Life <= 0f) continue;

                p.Life -= dt;
                if (p.Life <= 0f) { p.Life = 0f; continue; }

                p.Vel += p.Accel * dt;
                if (p.Drag > 0f) p.Vel *= MathF.Max(0f, 1f - p.Drag * dt);
                p.Pos += p.Vel * dt;

                if (p.SwayAmplitude > 0f)
                {
                    float elapsed = p.MaxLife - p.Life;
                    p.Pos.X += MathF.Sin(elapsed * p.SwayFrequency + p.Phase) * p.SwayAmplitude * dt;
                }

                p.Rotation += p.AngularVelocity * dt;
            }
        }

        /// <summary>Deactivates every particle.</summary>
        public void Clear()
        {
            for (int i = 0; i < _particles.Length; i++) _particles[i].Life = 0f;
            _cursor = 0;
        }

        /// <summary>Life fraction elapsed in [0,1]: 0 at spawn, 1 at death.</summary>
        static float ElapsedFraction(in Particle p) => p.MaxLife > 0f ? 1f - p.Life / p.MaxLife : 1f;

        static float CurrentSize(in Particle p)
        {
            float t = ElapsedFraction(p);
            return p.StartSize + (p.EndSize - p.StartSize) * t;
        }

        static Color CurrentColor(in Particle p) => Color.Lerp(p.StartColor, p.EndColor, ElapsedFraction(p));

        static Color MultiplyColor(Color a, Color b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

        /// <summary>
        /// Enumerates live particles as snapshots. For tests and occasional inspection; allocates an iterator per
        /// call, so prefer <see cref="Draw(SpriteBatch, Texture2D)"/> for per-frame rendering.
        /// </summary>
        public IEnumerable<Particle2DView> ActiveParticles()
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                Particle p = _particles[i];
                if (p.Life <= 0f) continue;
                yield return new Particle2DView(
                    p.Pos, p.Vel, p.Rotation, CurrentSize(p), CurrentColor(p), p.Life, p.MaxLife, p.Blend);
            }
        }

        /// <summary>
        /// Draws every live particle as a rotated, sized, colour-lerped quad of <paramref name="texture"/> (pass a
        /// 1x1 white pixel for solid squares, or a baked glow dot for soft sprites). Each particle uses its own
        /// <see cref="BlendMode"/>. The batch's blend mode is restored afterwards.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D texture)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(texture);
            BlendMode prev = batch.BlendMode;
            for (int i = 0; i < _particles.Length; i++)
            {
                ref Particle p = ref _particles[i];
                if (p.Life <= 0f) continue;
                float size = CurrentSize(p);
                if (size <= 0f) continue;
                batch.BlendMode = p.Blend;
                batch.Draw(texture, p.Pos, new Vector2(size, size), new Vector2(0.5f, 0.5f),
                    p.Rotation, PrimitiveRenderer.FullUV, CurrentColor(p));
            }
            batch.BlendMode = prev;
        }

        /// <summary>
        /// As <see cref="Draw(SpriteBatch, Texture2D)"/>, but forces every particle to
        /// <paramref name="blendOverride"/> (ignoring each particle's own blend mode).
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D texture, BlendMode blendOverride)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(texture);
            BlendMode prev = batch.BlendMode;
            batch.BlendMode = blendOverride;
            for (int i = 0; i < _particles.Length; i++)
            {
                ref Particle p = ref _particles[i];
                if (p.Life <= 0f) continue;
                float size = CurrentSize(p);
                if (size <= 0f) continue;
                batch.Draw(texture, p.Pos, new Vector2(size, size), new Vector2(0.5f, 0.5f),
                    p.Rotation, PrimitiveRenderer.FullUV, CurrentColor(p));
            }
            batch.BlendMode = prev;
        }
    }
}
