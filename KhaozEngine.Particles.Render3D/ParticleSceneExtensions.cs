using System;
using System.Buffers;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

// See ParticleLook.cs for why the namespace is KhaozEngine.Particles, not .Render3D.
namespace KhaozEngine.Particles
{
    /// <summary>
    /// Turn-key glue between the render-free particle sim and Render3D's modern particle pass. Each live particle
    /// maps to one <see cref="ParticleSprite"/>, optional per-particle trails forward to <see cref="Scene3D.DrawTrail"/>,
    /// and, when the <see cref="ParticleLook"/> asks for it, the brightest particles link as budgeted point lights.
    /// Immediate-mode: call once per frame inside the 3D pass. Presentation only, mutates no sim state.
    /// </summary>
    public static class ParticleSceneExtensions
    {
        /// <summary>
        /// Queue every live particle in <paramref name="system"/> as a modern sprite, applying <paramref name="look"/>.
        /// When the look enables trails and the pool carries trail history, each particle's tail is drawn as a tapered
        /// ribbon. When the look enables light links, up to <paramref name="lightBudget"/> of the brightest particles
        /// are added as point lights (0 or fewer disables the links).
        /// </summary>
        public static void DrawParticles(this Scene3D scene, ParticleSystem system, in ParticleLook look, int lightBudget = 4)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(system);
            DrawSystem(scene, system, in look, lightBudget);
        }

        /// <summary>
        /// Draw every phase of a playing <paramref name="player"/>, one <see cref="ParticleLook"/> per phase.
        /// <paramref name="looks"/> must have exactly <see cref="ParticleEffectPlayer.PhaseCount"/> entries. A single
        /// <paramref name="lightBudget"/> is shared across the whole effect: it is spent phase by phase and the total
        /// number of linked lights never exceeds it.
        /// </summary>
        public static void DrawEffect(this Scene3D scene, ParticleEffectPlayer player, ReadOnlySpan<ParticleLook> looks, int lightBudget = 4)
        {
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(player);
            if (looks.Length != player.PhaseCount)
            {
                throw new ArgumentException(
                    $"Expected one look per phase ({player.PhaseCount}) but got {looks.Length}.", nameof(looks));
            }

            int remaining = lightBudget;
            for (int ph = 0; ph < player.PhaseCount; ph++)
            {
                int spent = DrawSystem(scene, player.PhaseSystem(ph), in looks[ph], remaining);
                remaining -= spent;
                if (remaining < 0)
                {
                    remaining = 0;
                }
            }
        }

        // Draw one system through one look. Returns the number of point lights it added (0 when the look has no
        // light link), so DrawEffect can spend a shared budget across phases.
        private static int DrawSystem(Scene3D scene, ParticleSystem system, in ParticleLook look, int lightBudget)
        {
            ReadOnlySpan<Particle> active = system.Active;
            if (active.Length == 0)
            {
                return 0;
            }

            // An active-distortion look warps the scene INSTEAD of drawing a visible sprite: emit one distortion
            // sprite per live particle (strength scaled by the particle's alpha so fields fade with life) and skip
            // the particle-sprite path entirely. The inactive default keeps every particle on the normal path below.
            if (look.Distortion.IsActive)
            {
                for (int i = 0; i < active.Length; i++)
                {
                    scene.DrawDistortion(BuildDistortionSprite(in look, in active[i]));
                }
            }
            else
            {
                // Flipbook timing is look-level and loop-invariant, so resolve the per-look constants once. When the
                // look has no atlas, flip stays false and every sprite keeps the procedural path.
                bool flip = look.Flipbook.IsActive;
                int frameCount = flip ? look.Flipbook.Columns * look.Flipbook.Rows : 0;
                float fps = look.FlipbookFps > 0f ? look.FlipbookFps : 12f;
                float effectTime = scene.EffectTimeSeconds;

                for (int i = 0; i < active.Length; i++)
                {
                    ref readonly Particle p = ref active[i];
                    ParticleSprite sprite = new()
                    {
                        Position = p.Position,
                        Velocity = p.Velocity,
                        Size = p.Size,
                        Rotation = p.Rotation,
                        Color = p.Color,
                        Shape = look.Shape,
                        ShapeParam = look.ShapeParam,
                        LifeNorm = p.Norm,
                        Seed = p.Seed,
                        Stretch = look.Stretch,
                        Blend = look.Blend,
                        Orientation = look.Orientation,
                        SoftFadeScale = look.SoftFadeScale,
                    };
                    if (flip)
                    {
                        sprite.Flipbook = look.Flipbook;
                        sprite.FlipbookFrame = ResolveFlipbookFrame(
                            look.FlipbookMode, p.Norm, p.Seed, effectTime, fps, frameCount, look.FlipbookRandomStart);
                    }
                    scene.DrawParticle(in sprite);
                }
            }

            if (look.Trails && system.TrailCapacity > 0)
            {
                ForwardTrails(scene, system, in look, active);
            }

            if (look.LightRadius > 0f && look.LightIntensity > 0f && lightBudget > 0)
            {
                return LinkLights(scene, in look, active, lightBudget);
            }

            return 0;
        }

        // Resolve one particle's continuous flipbook frame from the look's timing mode. LifeOneShot sweeps the sheet
        // across the particle's life. TimeLoop advances at fps and (optionally) staggers the starting frame per
        // particle by seed. The render side (ParticleRenderer.ResolveFrames) turns this into the two integer frames
        // plus a blend, wrapping or clamping per ParticleFlipbook.Loop, so timing here stays policy-only.
        internal static float ResolveFlipbookFrame(ParticleFlipbookMode mode, float lifeNorm, float seed,
            float timeSeconds, float fps, int frameCount, bool randomStart)
        {
            if (frameCount <= 0)
            {
                return 0f;
            }
            if (mode == ParticleFlipbookMode.TimeLoop)
            {
                float start = randomStart ? seed * frameCount : 0f;
                return timeSeconds * fps + start;
            }
            // LifeOneShot: sweep 0..frameCount as the particle ages. The one-shot resolve clamps the final cell, so
            // the last authored frame shows at full life instead of wrapping past the sheet.
            return Math.Clamp(lifeNorm, 0f, 1f) * frameCount;
        }

        // Map one live particle onto a distortion sprite through an active DistortionLook. The authored strength is
        // scaled by the particle's current alpha so the offset field fades with the particle's life. Pure and
        // internal so the field mapping is headless-testable (like ResolveFlipbookFrame).
        internal static DistortionSprite BuildDistortionSprite(in ParticleLook look, in Particle p) => new()
        {
            Position = p.Position,
            Size = p.Size,
            Rotation = p.Rotation,
            Shape = look.Distortion.Shape,
            ShapeParam = look.Distortion.ShapeParam,
            Strength = look.Distortion.Strength * p.Color.A,
            LifeNorm = p.Norm,
            Seed = p.Seed,
            Orientation = look.Orientation,
            SoftFadeScale = look.Distortion.SoftFadeScale,
        };

        // Forward each particle's motion history to DrawTrail. One point buffer and one sample buffer are rented
        // from the shared pool and reused across every particle, returned in a finally.
        private static void ForwardTrails(Scene3D scene, ParticleSystem system, in ParticleLook look, ReadOnlySpan<Particle> active)
        {
            int cap = system.TrailCapacity;
            float widthScale = look.TrailWidthScale <= 0f ? 0.5f : look.TrailWidthScale;
            ParticleTrailPoint[] points = ArrayPool<ParticleTrailPoint>.Shared.Rent(cap);
            TrailSample[] samples = ArrayPool<TrailSample>.Shared.Rent(cap);
            try
            {
                for (int i = 0; i < active.Length; i++)
                {
                    int count = system.GetTrail(i, points.AsSpan(0, cap));
                    if (count < 2)
                    {
                        continue;
                    }

                    ref readonly Particle p = ref active[i];
                    for (int j = 0; j < count; j++)
                    {
                        // Oldest-to-newest: progress rises to 1 at the head so the tail is faintest and thinnest.
                        float progress = (j + 1) / (float)count;
                        float halfWidth = p.Size * widthScale * progress;
                        float alpha = p.Color.A * progress;
                        samples[j] = new TrailSample(points[j].Position, halfWidth, alpha);
                    }

                    scene.DrawTrail(samples.AsSpan(0, count), look.TrailStyle);
                }
            }
            finally
            {
                ArrayPool<ParticleTrailPoint>.Shared.Return(points);
                ArrayPool<TrailSample>.Shared.Return(samples);
            }
        }

        // Add the top-K brightest live particles (by alpha) as point lights, K = min(lightBudget, live, cap). A
        // small-K partial selection kept in a stack buffer sorted weakest-first, no heap allocation, no LINQ.
        private static int LinkLights(Scene3D scene, in ParticleLook look, ReadOnlySpan<Particle> active, int lightBudget)
        {
            int k = lightBudget;
            if (k > active.Length)
            {
                k = active.Length;
            }
            // The renderer only uploads the first MaxPointLights, so never select more than that.
            if (k > Scene3D.MaxPointLights)
            {
                k = Scene3D.MaxPointLights;
            }
            if (k <= 0)
            {
                return 0;
            }

            Span<int> sel = stackalloc int[k];
            int selCount = 0;
            for (int i = 0; i < active.Length; i++)
            {
                float a = active[i].Color.A;
                if (selCount < k)
                {
                    // Insert keeping sel sorted ascending by alpha (sel[0] = weakest).
                    int pos = selCount++;
                    while (pos > 0 && active[sel[pos - 1]].Color.A > a)
                    {
                        sel[pos] = sel[pos - 1];
                        pos--;
                    }
                    sel[pos] = i;
                }
                else if (a > active[sel[0]].Color.A)
                {
                    // Evict the weakest, then sift the new index down to its sorted slot.
                    int pos = 0;
                    while (pos + 1 < k && active[sel[pos + 1]].Color.A < a)
                    {
                        sel[pos] = sel[pos + 1];
                        pos++;
                    }
                    sel[pos] = i;
                }
            }

            for (int i = 0; i < selCount; i++)
            {
                ref readonly Particle p = ref active[sel[i]];
                scene.AddLight(p.Position, p.Color, look.LightRadius, look.LightIntensity * p.Color.A);
            }
            return selCount;
        }
    }
}
