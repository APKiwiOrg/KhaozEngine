namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Metal's half-precision entry writes RGBA16F attachments with truncation, unlike the nearest-even
    // rounding used inside the transport. Preserve both conversions where the old pass boundary applied them.
    internal const string TemporalDetailStoredResultGlsl = @"
float detailStoredHalf(float v) {
    if (abs(v) < 0.00006103515625)
        return trunc(v * 16777216.0) * 0.000000059604644775390625;
    return uintBitsToFloat(floatBitsToUint(v) & 0xffffe000u);
}
vec4 detailStoredResult(vec4 v) {
#ifdef DetailTruncateResult
    return vec4(detailStoredHalf(v.x), detailStoredHalf(v.y), detailStoredHalf(v.z), detailStoredHalf(v.w));
#else
    return detailRound(v);
#endif
}";
}
