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
    /// velocity, size lerp and colour lerp over life, an optional trapezoid fade-in / hold / fade-out alpha
    /// envelope (<see cref="Particle2DEmitterConfig.FadeInDuration"/> / <see cref="Particle2DEmitterConfig.FadeOutDuration"/>),
    /// and a per-particle <see cref="BlendMode"/>. The pool is a ring buffer - emitting into a full pool overwrites
    /// the oldest particles.
    /// <para>
    /// Two lifecycles: a burst pool (<see cref="Emit(in Particle2DEmitterConfig, System.Numerics.Vector2, int)"/>,
    /// particles emit-and-die) or a persistent <b>ambient field</b>
    /// (<see cref="EmitField(in Particle2DEmitterConfig, KhaozEngine.Primitives.Rect, int)"/>): a field fills a
    /// bounds region with particles that, on death or on leaving the region, RESPAWN at a fresh random position
    /// inside the region instead of dying, so the population stays stable with no re-emission pops - dust motes,
    /// embers, falling snow. A field carries a live <see cref="SetFieldTint(int, KhaozEngine.Primitives.Color)"/>
    /// that recolours all its live particles instantly (e.g. following a depth/biome palette).
    /// </para>
    /// Motion is driven by a deterministic seeded <see cref="XorRng"/> (not <see cref="System.Random"/>), so a
    /// given seed + call sequence is fully reproducible and headless-testable. Render-agnostic:
    /// <see cref="Draw(SpriteBatch, Texture2D)"/> takes the batch plus a texture (a 1x1 white pixel for solid
    /// squares, or a baked glow dot for soft sprites).
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
            public float FadeIn, FadeOut;   // trapezoid alpha-envelope legs in seconds (0 = leg disabled)
            public int FieldId;             // -1 = burst particle; >=0 = index into _fields (respawns in-region)
        }

        readonly struct Field
        {
            public Field(in Particle2DEmitterConfig cfg, Rect region, Color tint, float exitMargin)
            {
                Cfg = cfg;
                Region = region;
                Tint = tint;
                ExitMargin = exitMargin;
            }

            public Particle2DEmitterConfig Cfg { get; }
            public Rect Region { get; }
            public Color Tint { get; }
            public float ExitMargin { get; }

            public Field WithTint(Color tint) => new(Cfg, Region, tint, ExitMargin);
        }

        readonly Particle[] _particles;
        readonly List<Field> _fields = new();
        XorRng _rng;
        int _cursor;

        // Sparse-set live-slot index: _liveSlots[0.._liveCount) holds the currently-live slot indices (order =
        // the order each slot most recently became live, NOT ascending slot index), and _liveSlotPos[slot] is
        // that slot's position within _liveSlots, or -1 when the slot is dead. Together they make
        // Update/Draw/ActiveCount O(live) instead of O(Capacity): a fixed-capacity pool that only ever holds a
        // handful of live particles at once no longer pays for scanning every dead/never-used slot every frame.
        // MarkDead swaps the removed slot with the last live slot (O(1)); a removal during Update's own iteration
        // backs the loop index up by one so the just-swapped-in slot is still visited this frame. Field particles
        // respawn in place on death (see FillFieldParticle) so they never leave the live set; only a pure burst
        // particle (FieldId == -1) transitions live -> dead and back via a future Emit at that ring slot.
        readonly int[] _liveSlots;
        readonly int[] _liveSlotPos;
        int _liveCount;

        /// <summary>Creates a system with pool <paramref name="capacity"/> (default 256) seeded by <paramref name="seed"/>.</summary>
        public Particle2DSystem(int capacity = 256, uint seed = 1u)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _particles = new Particle[capacity];
            _rng = new XorRng(seed);
            _liveSlots = new int[capacity];
            _liveSlotPos = new int[capacity];
            Array.Fill(_liveSlotPos, -1);
        }

        /// <summary>Pool capacity (the maximum number of simultaneously live particles).</summary>
        public int Capacity => _particles.Length;

        /// <summary>Number of ambient fields registered via <see cref="EmitField(in Particle2DEmitterConfig, Rect, int)"/> (reset by <see cref="Clear"/>).</summary>
        public int FieldCount => _fields.Count;

        // Adds slot to the live set (no-op if already live). Idempotent so callers can call it unconditionally
        // after (re)writing a slot without first checking its prior state.
        void MarkLive(int slot)
        {
            if (_liveSlotPos[slot] >= 0) return;
            _liveSlotPos[slot] = _liveCount;
            _liveSlots[_liveCount] = slot;
            _liveCount++;
        }

        // Removes slot from the live set via swap-with-last (no-op if already dead).
        void MarkDead(int slot)
        {
            int pos = _liveSlotPos[slot];
            if (pos < 0) return;
            int lastSlot = _liveSlots[_liveCount - 1];
            _liveSlots[pos] = lastSlot;
            _liveSlotPos[lastSlot] = pos;
            _liveCount--;
            _liveSlotPos[slot] = -1;
        }

        /// <summary>Number of currently live particles. O(live), not O(Capacity).</summary>
        public int ActiveCount
        {
            get
            {
                int n = 0;
                for (int li = 0; li < _liveCount; li++)
                    if (_particles[_liveSlots[li]].Life > 0f) n++;
                return n;
            }
        }

        /// <summary>Emits <paramref name="count"/> particles at <paramref name="origin"/> using White as the tint.</summary>
        public void Emit(in Particle2DEmitterConfig cfg, Vector2 origin, int count)
            => Emit(cfg, origin, Color.White, count);

        /// <summary>
        /// Emits <paramref name="count"/> particles at <paramref name="origin"/>, multiplying the preset's
        /// start/end colours by <paramref name="tint"/>. The pool is a ring buffer: emitting into a full pool
        /// overwrites the oldest live particles. These particles emit-and-die (they do not respawn); for a
        /// persistent field use <see cref="EmitField(in Particle2DEmitterConfig, Rect, int)"/>.
        /// </summary>
        public void Emit(in Particle2DEmitterConfig cfg, Vector2 origin, Color tint, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            // Burst particles bake the tint into their colours at emit (fixed for the particle's life).
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
                float sizeScale = SampleSizeScale(cfg);

                ref Particle p = ref _particles[_cursor];
                p.Pos = new Vector2(origin.X + offsetX, origin.Y + offsetY);
                p.Vel = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
                p.Accel = cfg.Acceleration;
                p.Drag = cfg.Drag;
                p.Life = life;
                p.MaxLife = life;
                p.StartSize = cfg.StartSize * sizeScale;
                p.EndSize = cfg.EndSize * sizeScale;
                p.SwayFrequency = cfg.SwayFrequency;
                p.SwayAmplitude = cfg.SwayAmplitude;
                p.Phase = phase;
                p.Rotation = rotation;
                p.AngularVelocity = angularVel;
                p.StartColor = startColor;
                p.EndColor = endColor;
                p.Blend = cfg.Blend;
                p.FadeIn = cfg.FadeInDuration;
                p.FadeOut = cfg.FadeOutDuration;
                p.FieldId = -1;

                // Reconcile the live set for the slot's new state: usually marks it live, but a degenerate
                // zero-life draw (MinLife == MaxLife == 0) must not linger in the live set (matches the
                // Life > 0f gate every reader below uses).
                if (p.Life > 0f) MarkLive(_cursor); else MarkDead(_cursor);
                _cursor = (_cursor + 1) % _particles.Length;
            }
        }

        /// <summary>Registers an ambient field over <paramref name="region"/> and fills it with <paramref name="count"/> particles (White tint).</summary>
        public int EmitField(in Particle2DEmitterConfig cfg, Rect region, int count)
            => EmitField(cfg, region, Color.White, count);

        /// <summary>
        /// Registers a persistent ambient field over <paramref name="region"/> and fills it with
        /// <paramref name="count"/> particles at random positions inside the region, returning the field id (pass
        /// it to <see cref="SetFieldTint(int, Color)"/>). Each field particle that dies, or drifts more than
        /// <paramref name="exitMargin"/> pixels outside the region, RESPAWNS at a fresh random in-region position
        /// with a full life instead of dying, so the population stays stable with no emission pop. The initial fill
        /// randomizes each particle's remaining life across its lifetime, so the field starts already-populated and
        /// mid-envelope (no synchronized fade-in). The field's <paramref name="tint"/> multiplies its particles'
        /// colours live (see <see cref="SetFieldTint(int, Color)"/>), so a running colour change recolours the
        /// whole field immediately. Size the system's <see cref="Capacity"/> to <paramref name="count"/> so a field
        /// owns its pool (the ring buffer would otherwise let a later burst overwrite field slots).
        /// </summary>
        /// <param name="cfg">The particle preset (lifetime, motion, size, colour, envelope).</param>
        /// <param name="region">The spawn/respawn bounds in the same space the particles are drawn in.</param>
        /// <param name="tint">Live colour multiplier for the whole field.</param>
        /// <param name="count">How many particles to fill the region with.</param>
        /// <param name="exitMargin">How far (pixels) a particle may drift outside <paramref name="region"/> before it respawns. Default 0 (respawn on leaving the region).</param>
        public int EmitField(in Particle2DEmitterConfig cfg, Rect region, Color tint, int count, float exitMargin = 0f)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (exitMargin < 0f) throw new ArgumentOutOfRangeException(nameof(exitMargin));

            int fieldId = _fields.Count;
            _fields.Add(new Field(cfg, region, tint, exitMargin));

            for (int i = 0; i < count; i++)
            {
                FillFieldParticle(ref _particles[_cursor], fieldId, initialFill: true);
                if (_particles[_cursor].Life > 0f) MarkLive(_cursor); else MarkDead(_cursor);
                _cursor = (_cursor + 1) % _particles.Length;
            }
            return fieldId;
        }

        /// <summary>
        /// Updates a field's live colour multiplier (from <see cref="EmitField(in Particle2DEmitterConfig, Rect, Color, int, float)"/>).
        /// Applies to every live particle in the field immediately, since the tint is multiplied at draw time.
        /// </summary>
        public void SetFieldTint(int fieldId, Color tint)
        {
            if (fieldId < 0 || fieldId >= _fields.Count) throw new ArgumentOutOfRangeException(nameof(fieldId));
            _fields[fieldId] = _fields[fieldId].WithTint(tint);
        }

        // Fills one slot with a field particle. Burst-parity RNG draws (speed/life/pos/angle/rotation/angularVel/
        // phase/sizeScale). On the initial fill the remaining life is randomized across the lifetime so the field
        // is pre-populated at mixed envelope phases; a respawn uses a full life so it fades in fresh.
        void FillFieldParticle(ref Particle p, int fieldId, bool initialFill)
        {
            Field f = _fields[fieldId];
            Particle2DEmitterConfig cfg = f.Cfg;
            float baseAngle = MathF.Atan2(cfg.Direction.Y, cfg.Direction.X);

            float speed = _rng.Range(cfg.MinSpeed, cfg.MaxSpeed);
            float life = _rng.Range(cfg.MinLife, cfg.MaxLife);
            float posX = _rng.Range(f.Region.X, f.Region.Right);
            float posY = _rng.Range(f.Region.Y, f.Region.Bottom);

            float angle;
            if (cfg.Emission == Particle2DEmission.Radial)
                angle = _rng.Range(0f, MathF.PI * 2f);
            else
                angle = baseAngle + (cfg.SpreadRadians > 0f ? _rng.Range(-cfg.SpreadRadians, cfg.SpreadRadians) : 0f);

            float rotation = cfg.RotationJitter > 0f ? _rng.Range(-cfg.RotationJitter, cfg.RotationJitter) : 0f;
            float angularVel = _rng.Range(cfg.MinAngularVelocity, cfg.MaxAngularVelocity);
            float phase = cfg.SwayAmplitude > 0f ? _rng.Range(0f, MathF.PI * 2f) : 0f;
            float sizeScale = SampleSizeScale(cfg);
            float startLife = initialFill ? life * _rng.NextFloat() : life;

            p.Pos = new Vector2(posX, posY);
            p.Vel = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed);
            p.Accel = cfg.Acceleration;
            p.Drag = cfg.Drag;
            p.Life = startLife;
            p.MaxLife = life;
            p.StartSize = cfg.StartSize * sizeScale;
            p.EndSize = cfg.EndSize * sizeScale;
            p.SwayFrequency = cfg.SwayFrequency;
            p.SwayAmplitude = cfg.SwayAmplitude;
            p.Phase = phase;
            p.Rotation = rotation;
            p.AngularVelocity = angularVel;
            // Field particles keep the preset's raw colours; the field tint is applied live at draw time.
            p.StartColor = cfg.StartColor;
            p.EndColor = cfg.EndColor;
            p.Blend = cfg.Blend;
            p.FadeIn = cfg.FadeInDuration;
            p.FadeOut = cfg.FadeOutDuration;
            p.FieldId = fieldId;
        }

        float SampleSizeScale(in Particle2DEmitterConfig cfg)
        {
            if (cfg.SizeJitter <= 0f) return 1f;
            float scale = 1f + _rng.Range(-cfg.SizeJitter, cfg.SizeJitter);
            return scale < 0f ? 0f : scale;
        }

        /// <summary>Advances all live particles by <paramref name="dt"/> seconds. Field particles that die or leave their region respawn in-region.</summary>
        public void Update(float dt)
        {
            // Iterate only the live slots (see the sparse-set fields above), not the full pool. A burst particle
            // that dies this frame is swap-removed from the live set; the loop index steps back by one so the
            // slot swapped into its place is still visited this same frame (standard swap-remove-during-iterate).
            for (int li = 0; li < _liveCount; li++)
            {
                int slot = _liveSlots[li];
                ref Particle p = ref _particles[slot];
                if (p.Life <= 0f) continue;

                p.Life -= dt;
                if (p.Life <= 0f)
                {
                    if (p.FieldId >= 0)
                    {
                        FillFieldParticle(ref p, p.FieldId, initialFill: false);
                    }
                    else
                    {
                        p.Life = 0f;
                        MarkDead(slot);
                        li--;
                    }
                    continue;
                }

                p.Vel += p.Accel * dt;
                if (p.Drag > 0f) p.Vel *= MathF.Max(0f, 1f - p.Drag * dt);
                p.Pos += p.Vel * dt;

                if (p.SwayAmplitude > 0f)
                {
                    float elapsed = p.MaxLife - p.Life;
                    p.Pos.X += MathF.Sin(elapsed * p.SwayFrequency + p.Phase) * p.SwayAmplitude * dt;
                }

                p.Rotation += p.AngularVelocity * dt;

                if (p.FieldId >= 0)
                {
                    Field f = _fields[p.FieldId];
                    if (!WithinRegion(f.Region, f.ExitMargin, p.Pos))
                        FillFieldParticle(ref p, p.FieldId, initialFill: false);
                }
            }
        }

        static bool WithinRegion(Rect region, float margin, Vector2 pos) =>
            pos.X >= region.X - margin && pos.X <= region.Right + margin &&
            pos.Y >= region.Y - margin && pos.Y <= region.Bottom + margin;

        /// <summary>Deactivates every particle and clears the registered ambient fields.</summary>
        public void Clear()
        {
            for (int i = 0; i < _particles.Length; i++) _particles[i].Life = 0f;
            _fields.Clear();
            _cursor = 0;
            for (int li = 0; li < _liveCount; li++) _liveSlotPos[_liveSlots[li]] = -1;
            _liveCount = 0;
        }

        /// <summary>Life fraction elapsed in [0,1]: 0 at spawn, 1 at death.</summary>
        static float ElapsedFraction(in Particle p) => p.MaxLife > 0f ? 1f - p.Life / p.MaxLife : 1f;

        static float CurrentSize(in Particle p)
        {
            float t = ElapsedFraction(p);
            return p.StartSize + (p.EndSize - p.StartSize) * t;
        }

        // Trapezoid alpha-envelope multiplier: fade in over FadeIn, hold, fade out over FadeOut. Both legs default
        // to 0 (disabled), so the multiplier is 1 and the particle's colour-lerp alpha is unchanged.
        static float Envelope(in Particle p)
        {
            float env = 1f;
            if (p.FadeIn > 0f)
            {
                float inT = (p.MaxLife - p.Life) / p.FadeIn;   // seconds since spawn / fade-in duration
                env *= inT < 0f ? 0f : inT > 1f ? 1f : inT;
            }
            if (p.FadeOut > 0f)
            {
                float outT = p.Life / p.FadeOut;               // remaining life / fade-out duration
                env *= outT < 0f ? 0f : outT > 1f ? 1f : outT;
            }
            return env;
        }

        Color CurrentColor(in Particle p)
        {
            Color c = Color.Lerp(p.StartColor, p.EndColor, ElapsedFraction(p));
            if (p.FieldId >= 0) c = MultiplyColor(c, _fields[p.FieldId].Tint);
            float env = Envelope(p);
            return env >= 1f ? c : new Color(c.R, c.G, c.B, c.A * env);
        }

        static Color MultiplyColor(Color a, Color b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

        /// <summary>
        /// Enumerates live particles as snapshots. For tests and occasional inspection; allocates an iterator per
        /// call, so prefer <see cref="Draw(SpriteBatch, Texture2D)"/> for per-frame rendering.
        /// </summary>
        public IEnumerable<Particle2DView> ActiveParticles()
        {
            for (int li = 0; li < _liveCount; li++)
            {
                Particle p = _particles[_liveSlots[li]];
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
            for (int li = 0; li < _liveCount; li++)
            {
                ref Particle p = ref _particles[_liveSlots[li]];
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
            for (int li = 0; li < _liveCount; li++)
            {
                ref Particle p = ref _particles[_liveSlots[li]];
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
