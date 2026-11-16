using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// Vary the local conversion rather than assuming a second backend's attachment rule.
internal static class TemporalDetailResultTestSource
{
    internal static string Nearest(string source)
    {
        if (!source.Contains("layout(location=5) out vec4 oChild3;", StringComparison.Ordinal)) return source;
        const string conversion = "return vec4(detailStoredHalf(v.x), detailStoredHalf(v.y), detailStoredHalf(v.z), detailStoredHalf(v.w));";
        if (!source.Contains(conversion, StringComparison.Ordinal))
            throw new InvalidOperationException("The rule variant requires the resolve's explicit result conversion.");
        return source.Replace(conversion, "return detailRound(v);", StringComparison.Ordinal);
    }

    // Model the other adjacent format value explicitly, then let the real target store that exact half.
    internal static string CopyUp(string source)
    {
        source = source.Replace("void main() {", ShaderSources.TemporalDetailStoredResultGlsl + UpperGlsl
            + "\nvoid main() {", StringComparison.Ordinal);
        return source.Insert(source.LastIndexOf('}'), "    oColor = copyUpper(oColor);\n");
    }

    const string UpperGlsl = @"
float copyUpperHalf(float v) {
    float lower = detailStoredHalf(v);
    if (!(v > lower)) return lower;
    return lower < 0.00006103515625 ? lower + 0.000000059604644775390625
        : uintBitsToFloat(floatBitsToUint(lower) + 0x2000u);
}
vec4 copyUpper(vec4 v) {
    return vec4(copyUpperHalf(v.x), copyUpperHalf(v.y), copyUpperHalf(v.z), copyUpperHalf(v.w));
}
";
}
