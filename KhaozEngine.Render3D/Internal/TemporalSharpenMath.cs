using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The CPU mirror of <c>ShaderSources.TemporalSharpenFrag</c>: robust contrast adaptive sharpening (RCAS, after
    /// AMD FidelityFX Super Resolution 1) of one pixel against its north, west, east and south neighbours
    /// (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 4). Keep the noise term, the limiter and the weighted blend in sync
    /// with the shader. The input is display-referred colour in 0 to 1, after the tonemap.
    /// <para>
    /// The negative lobe applied to the four neighbours is solved per channel from the ring's minimum and maximum,
    /// each widened by the centre, so the output cannot leave 0 to 1. The most restrictive channel wins, and its
    /// bound is capped at <see cref="Limit"/> and scaled by the sharpness. A tap at exactly 0 or exactly 1 in any
    /// channel, in the ring or at the centre, bounds the range and leaves no room for a negative lobe, so such a
    /// pixel is left alone. The exception is a channel whose four ring taps all sit at the same extreme: that channel
    /// sets no bound on that side, as in FSR 1, so a clipped highlight or a pure primary still sharpens through its
    /// other channels. A centre standing alone against its ring is treated as noise and gets at most half the lobe,
    /// which is what keeps the pass from bringing back the shimmer the temporal resolve removed.
    /// </para>
    /// </summary>
    internal static class TemporalSharpenMath
    {
        /// <summary>The largest lobe magnitude RCAS allows, 0.25 minus 1/16.</summary>
        internal const float Limit = 0.1875f;

        /// <summary>The bound a channel takes on a side it sets no bound on. It lies past <see cref="Limit"/>, so it
        /// never restricts the other channels.</summary>
        internal const float NoBound = 0.25f;

        /// <summary>The sharpness the pass applies: <paramref name="sharpness"/> clamped to 0 to 1, with NaN as 0, so
        /// a NaN sharpness is off. The mirror, <see cref="Rendering.TemporalSharpenPass"/> and
        /// <see cref="TemporalSettings.ResolvedSharpness"/> all map through this, so they cannot drift apart.</summary>
        internal static float ResolvedSharpness(float sharpness) => sharpness > 0f ? MathF.Min(sharpness, 1f) : 0f;

        /// <summary>Twice the luma with the RCAS weights (half red, full green, half blue), for the noise
        /// term.</summary>
        internal static float Luma2(Vector3 c) => c.Z * 0.5f + (c.X * 0.5f + c.Y);

        /// <summary>1 for a centre that fits its ring, falling to 0.5 for a centre that stands alone against
        /// it.</summary>
        internal static float NoiseWeight(Vector3 b, Vector3 d, Vector3 e, Vector3 f, Vector3 h)
        {
            float bL = Luma2(b), dL = Luma2(d), eL = Luma2(e), fL = Luma2(f), hL = Luma2(h);
            float nz = 0.25f * (bL + dL + fL + hL) - eL;
            float range = MathF.Max(MathF.Max(MathF.Max(bL, dL), MathF.Max(eL, fL)), hL)
                        - MathF.Min(MathF.Min(MathF.Min(bL, dL), MathF.Min(eL, fL)), hL);
            nz = Math.Clamp(MathF.Abs(nz) / MathF.Max(range, 1e-5f), 0f, 1f);
            return 1f - 0.5f * nz;
        }

        /// <summary>The most negative lobe the ring and the centre allow in every channel, before the limit, the
        /// sharpness and the noise weight. Zero or above leaves no room for a negative lobe.</summary>
        internal static float RingLobe(Vector3 b, Vector3 d, Vector3 e, Vector3 f, Vector3 h)
        {
            Vector3 mn4 = Vector3.Min(Vector3.Min(b, d), Vector3.Min(f, h));
            Vector3 mx4 = Vector3.Max(Vector3.Max(b, d), Vector3.Max(f, h));
            Vector3 hitMin = Vector3.Min(mn4, e) / Vector3.Max(4f * mx4, new Vector3(1e-5f));
            Vector3 hitMax = (Vector3.One - Vector3.Max(mx4, e))
                           / Vector3.Min(4f * mn4 - new Vector3(4f), new Vector3(-1e-5f));
            // A channel whose four ring taps all sit at 0 sets no lower bound, and one whose four all sit at 1 no
            // upper bound. FSR 1 reaches the same through an infinite reciprocal that the maximum then ignores.
            hitMin = Where(mx4, 0f, NoBound, hitMin);
            hitMax = Where(mn4, 1f, -NoBound, hitMax);
            Vector3 lobeRgb = Vector3.Max(-hitMin, hitMax);
            return MathF.Max(lobeRgb.X, MathF.Max(lobeRgb.Y, lobeRgb.Z));
        }

        /// <summary>The lobe applied to the four neighbours: <see cref="RingLobe"/> capped at <see cref="Limit"/>,
        /// scaled by the sharpness and by <see cref="NoiseWeight"/>. A sharpness that is not above 0, NaN included,
        /// gives none.</summary>
        internal static float Lobe(Vector3 b, Vector3 d, Vector3 e, Vector3 f, Vector3 h, float sharpness)
        {
            float lobe = MathF.Max(-Limit, MathF.Min(RingLobe(b, d, e, f, h), 0f)) * ResolvedSharpness(sharpness);
            return lobe * NoiseWeight(b, d, e, f, h);
        }

        /// <summary>The sharpened centre before the shader's final clamp, which the limiter makes a no-op.</summary>
        internal static Vector3 SharpenUnclamped(Vector3 b, Vector3 d, Vector3 e, Vector3 f, Vector3 h, float sharpness)
        {
            float lobe = Lobe(b, d, e, f, h, sharpness);
            return (lobe * (b + d + f + h) + e) / (4f * lobe + 1f);
        }

        /// <summary>Sharpen <paramref name="e"/> against its four neighbours. <paramref name="sharpness"/> runs from 0
        /// (identity) to 1 (the full RCAS lobe). A NaN sharpness is off.</summary>
        public static Vector3 Sharpen(Vector3 b, Vector3 d, Vector3 e, Vector3 f, Vector3 h, float sharpness)
            => ResolvedSharpness(sharpness) == 0f
                ? e
                : Vector3.Clamp(SharpenUnclamped(b, d, e, f, h, sharpness), Vector3.Zero, Vector3.One);

        // Per channel: where the ring value equals the extreme, the channel takes the no-bound value.
        static Vector3 Where(Vector3 ring, float extreme, float none, Vector3 bound) => new(
            ring.X == extreme ? none : bound.X,
            ring.Y == extreme ? none : bound.Y,
            ring.Z == extreme ? none : bound.Z);
    }
}
