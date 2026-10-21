using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Retains ordered scalar jobs, reads only the consumed basis, and publishes each word once.</summary>
internal static class CoverageCarryGatherShader
{
    internal static string MovementSource => CoverageGpuNormalWorkLayout.Source.Replace(
        "return !covEqual3(covWork3(covRow(w,4u)+vertex*16u),covWork3(covRow(w,8u)+vertex*16u));",
        "return CovWorkWords[covRow(w,44u)+vertex] != 0u;", StringComparison.Ordinal);

    internal static string Source
    {
        get
        {
            string source = CoverageGpuNormalGatherGlsl.Source.ReplaceLineEndings("\n")
                .Replace("void main() {", "void carryGather(out vec3 position) {\n    position = vec3(0.0);", StringComparison.Ordinal)
                .Replace("uint target = covRow(w,10u)+vertex*16u;", "position = covWork3(record);", StringComparison.Ordinal);
            int start = source.IndexOf("CovNormalBasis basis =", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Pinned normal gather basis changed.");
            int end = source.IndexOf("covNormalUnitBegin(transformed,unit);", start, StringComparison.Ordinal);
            if (end <= start) throw new InvalidOperationException("Pinned normal gather normalization changed.");
            source = source[..start] + Transform + "\n            " + source[end..];
            start = source.IndexOf("[[dont_unroll]] for (uint field = 0u; field < 16u; field++)", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Pinned normal gather publication changed.");
            return source[..start] + Publish + "\n}\n";
        }
    }

    const string Transform = """
        vec3 transformed;
                    if (direction == 1) transformed = covCombine(covWork3(p+9u),covDot3(covWork3(p+18u),original),
                        covWork3(p+12u),covDot3(covWork3(p+21u),original),covWork3(p+15u),covDot3(covWork3(p+24u),original));
                    else transformed = covCombine(covWork3(p+27u),covDot3(covWork3(p),original),
                        covWork3(p+30u),covDot3(covWork3(p+3u),original),covWork3(p+33u),covDot3(covWork3(p+6u),original));
        """;

    const string Publish = """
        uint current = CovFrameWords[f+2u]+vertex*16u, previous = CovFrameWords[f+3u]+vertex*16u;
            bool updateNormal = moved && !covEqual3(covWork3(record+3u),vec3(0.0));
            bool updateTangent = moved && !covEqual3(covWork3(record+12u),vec3(0.0));
            if (moved) position = covWork3(covRow(w,8u)+vertex*16u);
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++) {
                uint word = CovWorkWords[record+field];
                if (moved && field < 3u) word = floatBitsToUint(covF(field == 0u ? position.x : (field == 1u ? position.y : position.z)));
                else if (updateNormal && field >= 3u && field < 6u) word = floatBitsToUint(covF(field == 3u ? normal.x : (field == 4u ? normal.y : normal.z)));
                else if (updateTangent && field >= 12u && field < 15u) word = floatBitsToUint(covF(field == 12u ? tangent.x : (field == 13u ? tangent.y : tangent.z)));
                CovPreviousWords[previous+field] = CovCurrentWords[current+field];
                CovCurrentWords[current+field] = word;
            }
        """;
}
