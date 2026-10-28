namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The resolve's SHIMMER RECORD, per display pixel in the history state's blue and alpha
    /// (<see cref="TemporalFormats.HistoryConfidence"/>): drift, an exponential average of this frame's luma less its
    /// reprojected history, signed, and swing, the same average of its size. Luma is the luma-weighted YCoCg
    /// <c>.x</c> the clip works in, taken before the clip and the blend. A still feature the jitter shows on some
    /// frames and misses on others swings with no drift over a cycle, while a light or a shadow creeping over a
    /// surface keeps the change one-signed, so drift follows swing. <c>temporalAccumulatePixel</c> reads the record
    /// with the confidence's four state texels and bilinear weights, steps it by this frame's change, and stores an
    /// empty record wherever it used no history or the shading release dropped it. The score
    /// (<c>temporalShimmerScore</c>) read from the stepped record widens the final luma clip and raises the
    /// accumulation cap toward <see cref="TemporalResolveTuning.MaxStillAccumulation"/>. Included in both entry points
    /// beside <see cref="TemporalAccumulateGlsl"/>, after the <c>Resolve</c> block. <see cref="TemporalShimmer"/> is
    /// the C# mirror of the rate, and the jitter constants mirror <see cref="TemporalJitter"/>.
    /// </summary>
    internal static partial class ShaderSources
    {
        internal const string TemporalShimmerGlsl = @"
const float JitterNativePhaseCount = 8.0;
const float JitterMaxDisplayOverInternalRatio = 4.0;
const float JitterMaxPhaseCount = 128.0;
// TemporalShimmer.Rate(Jitter.z): one over TemporalJitter.PhaseCount for the display over internal ratio, so the record
// spans about one jitter cycle at every preset. A ratio that is not a number reads as native, as it does there.
float temporalShimmerRate() {
    float ratio = Jitter.z;
    if (!(ratio > 1.0)) return 1.0 / JitterNativePhaseCount;
    ratio = min(ratio, JitterMaxDisplayOverInternalRatio);
    return 1.0 / min(JitterMaxPhaseCount, ceil(JitterNativePhaseCount * ratio * ratio));
}
// One frame's step of a reprojected record (drift, swing) toward this frame's luma change and its size.
vec2 temporalShimmerStep(vec2 record, float lumaChange) {
    float rate = temporalShimmerRate();
    return vec2(mix(record.x, lumaChange, rate), mix(record.y, abs(lumaChange), rate));
}
// The shimmer score from the stepped record and this frame's luma change: near 1 where the pixel swings about a steady
// mean, as a still feature the jitter shows and misses does, and falling toward 0 as drift follows swing under a
// one-way change. None below ShimmerFloor of swing (noise), on a pixel moving ShimmerStillPixels or more, on reactive
// content, and on a frame whose change exceeds ShimmerChangeBand swings, which is real however much the pixel
// shimmers. Its inputs come from a history and record that passed the finite check, so none is NaN. The negated
// comparisons would score a NaN none on most compilers, but FXC may fold them, so they are no guarantee of it.
float temporalShimmerScore(float drift, float swing, float lumaChange, float motionPixels, float reactive) {
    if (!(swing >= ShimmerFloor) || !(motionPixels < ShimmerStillPixels) || !(reactive <= 0.0)
        || !(abs(lumaChange) <= ShimmerChangeBand * swing)) return 0.0;
    return clamp(1.0 - abs(drift) / max(swing, ShimmerFloor), 0.0, 1.0);
}
";

        /// <summary>
        /// The shimmer score's shadow evidence, in the programs that read exported key-shadow visibility, spliced in
        /// after <see cref="TemporalVisibilityGlsl"/>. A shadow creeping over a surface that already shimmers can keep
        /// the luma change alternating, so the record alone may read the passing shadow as shimmer and hold the old
        /// shading.
        /// The visibility the scene exports moves one way there. It defines <c>TemporalShimmerVisibility</c>, so
        /// <see cref="TemporalVisibilityPrograms.WithoutDecision"/>, which removes this text with the other visibility
        /// helpers, leaves the score as it is in the fully lit programs.
        /// </summary>
        internal const string TemporalShimmerVisibilityGlsl = @"
#define TemporalShimmerVisibility
// Whether the visibility exported at the centre texel lies more than VisibilityShimmerGate from last frame's at the
// same surface point, the centre texel's own term of temporalVisibilityChange: one texel fetch and one bilinear sample,
// with no depth qualification, since a miss only keeps a score the luma record allowed. False until a previous
// visibility exists (PreviousJitter.z). The R8 visibility targets cannot hold a NaN.
bool temporalShimmerVisibilityChanged(vec2 uv, ivec2 centre, vec2 previousUv) {
    if (!(PreviousJitter.z > 0.5)) return false;
    float current = texelFetch(sampler2D(KeyShadowVisibility, LinearClamp), centre, 0).r;
    vec2 historyUv = previousUv + (vec2(centre) + 0.5 - Jitter.xy) / Sizes.xy - uv + PreviousJitter.xy / Sizes.xy;
    float previous = textureLod(sampler2D(PreviousKeyShadowVisibility, LinearClamp), historyUv, 0.0).r;
    return !(abs(current - previous) <= VisibilityShimmerGate);
}
";
    }
}
