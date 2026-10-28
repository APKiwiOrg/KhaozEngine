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
    /// empty record wherever it used no history or the shading release dropped it. Included in both entry points beside
    /// <see cref="TemporalAccumulateGlsl"/>, after the <c>Resolve</c> block. <see cref="TemporalShimmer"/> is the C#
    /// mirror, and the jitter constants mirror <see cref="TemporalJitter"/>.
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
";
    }
}
