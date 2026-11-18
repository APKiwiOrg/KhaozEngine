namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Choose a representable result for detail-bearing pixels. Every attachment stores it unchanged,
    // independently of its conversion rule. Retention keeps its separate nearest-even projection.
    internal const string TemporalDetailStoredResultGlsl = @"
float detailStoredHalf(float v) {
    if (abs(v) < 0.00006103515625)
        return trunc(v * 16777216.0) * 0.000000059604644775390625;
    return uintBitsToFloat(floatBitsToUint(v) & 0xffffe000u);
}
vec4 detailStoredResult(vec4 v) {
    return vec4(detailStoredHalf(v.x), detailStoredHalf(v.y), detailStoredHalf(v.z), detailStoredHalf(v.w));
}";
}
