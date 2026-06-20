using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Draws an additive "attention" pulse at a point: expanding sonar-ping rings under a configurable number of
    /// twinkling glints. Time-driven and stateless - the caller passes the elapsed time in seconds, so the same
    /// time always renders the same frame (pass an unscaled real-time accumulator to keep it animating regardless
    /// of any game time-scale). Composited additively regardless of the batch's current <see cref="BlendMode"/>
    /// (set and restored around the draw), so it reads as a bright pulse on a dark scene.
    /// </summary>
    public static class AttentionBeacon
    {
        // The radial-glow texture's bright ring band sits at this fraction of its half-extent (the BakeRing default
        // innerRadius01 0.55 + half of thickness01 0.25 = 0.675); placing a band at radius r needs side 2r/0.675.
        internal const float BandCenterFraction = 0.675f;

        static readonly Vector2 Centered = new(0.5f, 0.5f);

        /// <summary>
        /// Draws the attention pulse centered at <paramref name="center"/> (screen-space) on <paramref name="batch"/>.
        /// <paramref name="ring"/> is the soft annulus texture for the sonar rings (a null ring skips the rings);
        /// <paramref name="glow"/> is the radial-glow texture for the glints (a null glow skips the glints).
        /// <paramref name="timeSeconds"/> drives the ring expansion and glint twinkle. Composited additively (the
        /// batch's blend mode is restored afterwards).
        /// </summary>
        public static void Draw(SpriteBatch batch, Texture2D? ring, Texture2D? glow,
            Vector2 center, in AttentionBeaconParams p, float timeSeconds)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (p.Intensity <= 0f) return;

            BlendMode prev = batch.BlendMode;
            batch.BlendMode = BlendMode.Additive;

            DrawRings(batch, ring, center, p, timeSeconds);
            DrawGlints(batch, glow, center, p, timeSeconds);

            batch.BlendMode = prev;
        }

        static void DrawRings(SpriteBatch batch, Texture2D? ring, Vector2 center, in AttentionBeaconParams p, float time)
        {
            if (ring == null || p.RingCount <= 0) return;
            for (int i = 0; i < p.RingCount; i++)
            {
                float phase = RingPhase(i, p.RingCount, time, p.RingPeriod);
                float radius = RingRadius(phase, p.InnerRadius, p.MaxRadius);
                float alpha = RingAlpha(phase) * p.Intensity;
                if (alpha <= 0f) continue;
                float d = RingDiameter(radius, p.RingThickness, BandCenterFraction);
                if (d <= 0f) continue;
                batch.Draw(ring, center, new Vector2(d, d), Centered, 0f, PrimitiveRenderer.FullUV, p.Color * alpha);
            }
        }

        static void DrawGlints(SpriteBatch batch, Texture2D? glow, Vector2 center, in AttentionBeaconParams p, float time)
        {
            if (glow == null || p.GlintCount <= 0 || p.GlintSize <= 0f) return;
            for (int j = 0; j < p.GlintCount; j++)
            {
                float alpha = GlintAlpha(j, time, p.TwinkleRate) * p.Intensity;
                if (alpha <= 0f) continue;

                float angle = GlintAngle(j);
                float dist = p.GlintRadius * GlintRadiusFactor(j);
                Vector2 pos = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;
                Color tint = p.Color * alpha;

                if (p.GlintStyle == GlintStyle.Star)
                {
                    // Two crossed soft quads stretched from the radial glow = a tiny 4-point sparkle.
                    float arm = p.GlintSize, thin = p.GlintSize * 0.28f;
                    batch.Draw(glow, pos, new Vector2(arm, thin), Centered, 0f, PrimitiveRenderer.FullUV, tint);
                    batch.Draw(glow, pos, new Vector2(thin, arm), Centered, 0f, PrimitiveRenderer.FullUV, tint);
                }
                else
                {
                    batch.Draw(glow, pos, new Vector2(p.GlintSize, p.GlintSize), Centered, 0f, PrimitiveRenderer.FullUV, tint);
                }
            }
        }

        /// <summary>
        /// Phase in [0,1) of ring <paramref name="index"/> of <paramref name="ringCount"/> at
        /// <paramref name="time"/> seconds with period <paramref name="period"/> seconds. Advances
        /// <c>time/period</c> and is staggered by <c>index/ringCount</c> so the rings are evenly spaced. Pure.
        /// </summary>
        internal static float RingPhase(int index, int ringCount, float time, float period)
        {
            if (period <= 0f || ringCount <= 0) return 0f;
            float phase = time / period + (float)index / ringCount;
            phase -= MathF.Floor(phase);
            return phase;
        }

        /// <summary>Ring radius (pixels) at <paramref name="phase"/>: lerp from <paramref name="inner"/> to <paramref name="max"/>. Pure.</summary>
        internal static float RingRadius(float phase, float inner, float max) => inner + (max - inner) * phase;

        /// <summary>Ring alpha at <paramref name="phase"/>: 1 at the inner radius (phase 0), 0 at the max radius (phase 1). Pure.</summary>
        internal static float RingAlpha(float phase) => Math.Clamp(1f - phase, 0f, 1f);

        /// <summary>
        /// Side (pixels) of the centered square quad for a soft ring whose bright band should sit at
        /// <paramref name="bandRadius"/>: <c>2 * bandRadius * ringThickness / bandCenterFraction</c>.
        /// <paramref name="ringThickness"/> scales the quad (1 = the texture's native band). Pure.
        /// </summary>
        internal static float RingDiameter(float bandRadius, float ringThickness, float bandCenterFraction) =>
            2f * bandRadius * ringThickness / bandCenterFraction;

        // Golden angle / golden-ratio conjugate give stable, well-spread, RNG-free per-index offsets.
        internal const float GoldenAngle = 2.39996323f;       // radians
        const float GoldenRatioConj = 0.61803399f;

        /// <summary>Fractional part of <paramref name="v"/> in [0,1). Pure.</summary>
        static float Frac(float v) => v - MathF.Floor(v);

        /// <summary>Angle (radians) of glint <paramref name="index"/>: golden-angle spacing, stable and well spread. Pure.</summary>
        internal static float GlintAngle(int index) => index * GoldenAngle;

        /// <summary>Per-index radius factor in [0.6, 1.0] from a golden-ratio hash, so glints sit at varied radii. Pure.</summary>
        internal static float GlintRadiusFactor(int index) => 0.6f + 0.4f * Frac((index + 1) * GoldenRatioConj);

        /// <summary>
        /// Twinkle alpha in [0,1] of glint <paramref name="index"/> at <paramref name="time"/> seconds twinkling at
        /// <paramref name="twinkleRate"/> rad/s, on an index-derived phase so glints pulse out of step. Pure.
        /// </summary>
        internal static float GlintAlpha(int index, float time, float twinkleRate)
        {
            float phase = Frac(index * GoldenRatioConj) * MathF.Tau;
            return 0.5f + 0.5f * MathF.Sin(time * twinkleRate + phase);
        }
    }
}
