using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Opt-in threshold + separable-blur bloom: a bright-pass extracts pixels above <see cref="Threshold"/> into
    /// a half-resolution target, blurs them (horizontal then vertical, <see cref="Radius"/> taps per side), and adds
    /// the result back onto the full-resolution image at <see cref="Intensity"/> strength. Reachable as
    /// <see cref="PixelPostProcessSettings.Bloom"/>, following the <see cref="SkySettings"/>/<c>ShadowSettings</c>
    /// precedent: a plain settings bag with sensible defaults, the math is pure and headless-tested
    /// (<see cref="BloomMath"/>). Default <see cref="Enabled"/> == false, so existing scenes are byte-stable
    /// (no extra passes, no half-res targets allocated) until a game opts in.
    /// <para>
    /// Bloom is HDR-aware through <see cref="HdrSettings"/>. In HDR mode (the default) the bright-pass runs
    /// PRE-tonemap on the float16 (<c>R16G16B16A16Float</c>) scene, so <see cref="Threshold"/> and <see cref="Knee"/>
    /// operate on pre-tonemap luma that CAN exceed 1.0. A threshold at or above 1.0 there extracts only genuinely
    /// over-range content (a <c>new Color(4f, 2f, 1f)</c> emissive, a hot beam core), so a surface that is merely
    /// well-lit white does not halo. In legacy mode (<c>Post.Hdr.Enabled = false</c>) the target is
    /// <c>R8G8B8A8UNorm</c> (see <c>docs/CROSS-PLATFORM.md</c>) with no over-1.0 headroom, so the bright-pass
    /// thresholds the already-clamped-to-[0,1] lit colour and the historical LDR semantics hold (tune
    /// <see cref="Threshold"/> down for a softer cutoff). Either way this reads as a convincing glow on
    /// beams/emissive materials/bright billboards, the "A"-tier semi-realistic target this shipped for.
    /// </para>
    /// </summary>
    public sealed class BloomSettings
    {
        /// <summary>Run the bloom pass. Default <c>false</c> (no bright-pass/blur/composite passes, no half-res
        /// targets allocated, existing goldens byte-stable). Set <c>true</c> to turn it on.</summary>
        public bool Enabled = false;

        /// <summary>Luma above which a pixel starts contributing to the bloom, per <see cref="BloomMath.KneeWeight"/>.
        /// Default <c>0.7</c>, which on the legacy [0,1] target catches beams/emissive/near-white highlights while
        /// leaving mid-tones dark. Lower it (e.g. 0.5) for a softer/more-pervasive glow, raise it (e.g. 0.85-0.9) so
        /// only the brightest highlights bloom. In HDR mode luma is the PRE-tonemap value (which can exceed 1.0, see
        /// the class summary), so raise it to at or above 1.0 to bloom only over-range content.</summary>
        public float Threshold = 0.7f;

        /// <summary>Smoothstep ramp half-width (luma units) around <see cref="Threshold"/>, so the bright-pass cutoff
        /// is a soft curve instead of a hard edge (avoids a banded/aliased bloom silhouette). Default <c>0.15</c>;
        /// <c>0</c> is a hard threshold (also acceptable per the design brief). Kept well below <see cref="Threshold"/>
        /// by default so it does not reach into typical mid-tone albedo.</summary>
        public float Knee = 0.15f;

        /// <summary>Additive strength of the blurred bright-pass when composited back onto the full-resolution
        /// image (0 = invisible, matching off; 1 = the blurred bright colour is added at full strength). Default
        /// <c>0.6</c>. Raise for a stronger halo, lower for a subtle glow.</summary>
        public float Intensity = 0.6f;

        /// <summary>Gaussian blur radius in taps per side (so <c>2*Radius+1</c> total taps per axis), applied
        /// separably (horizontal pass then vertical pass) at half resolution. Default <c>4</c> - a soft, cheap
        /// halo. Larger values widen and soften the glow at a roughly linear extra sampling cost per pass; clamped
        /// to <c>[0, BloomMath.MaxRadius]</c> when the pass runs (<c>0</c> = the bright-pass composites with no blur,
        /// a sharp glow matching the thresholded shape exactly).</summary>
        public int Radius = 4;
    }
}
