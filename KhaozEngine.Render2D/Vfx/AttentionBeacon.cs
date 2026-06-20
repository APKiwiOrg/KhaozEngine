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
    }
}
