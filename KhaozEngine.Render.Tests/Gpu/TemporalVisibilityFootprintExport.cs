using System;
using KhaozEngine.Render3D.Internal;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Test-only metadata beside the production path export. Samples the whole geometric footprint before
/// depth rejection: all nine current texels, the fast path's full 4x4 history block, or the slow path's nine clamped
/// bilinear footprints. Equal R8 values compare exactly after UNorm conversion. D is evaluated on uniform pairs.
/// The signs of red, green and blue carry uniform, different and D-released flags through half storage as -x-1.
/// Decode restores the original path and all 32 change bits before the common path reader sees them.</summary>
internal static class TemporalVisibilityFootprintExport
{
    const string CallSite = "    if (visibilityReleased) lockValue = 0.0;";
    const string ColourEnd = "float(changeBitsForTests >> 22) + vUv.x * 1.0e-30);";

    internal static string Rewrite(string program)
    {
        if (!program.Contains(ShaderSources.TemporalVisibilityGlsl, StringComparison.Ordinal)) return program;
        string rewritten = TemporalVisibilityProductionPaths.Rewrite(
            AfterOnce(program, ShaderSources.TemporalVisibilityGlsl, FootprintGlsl));
        rewritten = AfterOnce(rewritten, "float visibilityChangeForTests = 0.0;", "\nuint footprintForTests = 0u;");
        rewritten = AfterOnce(rewritten, CallSite, @"
    if ((visibilityPath & (VisibilityFastPath | VisibilitySlowPath)) != 0u)
        footprintForTests = visibilityFootprintForTests(uv, centreTexel, maxTexel, previousUv, expectedDepth,
            visibilityPath);
");
        return AfterOnce(rewritten, ColourEnd, @"
    if ((footprintForTests & 1u) != 0u) oColor.r = -oColor.r - 1.0;
    if ((footprintForTests & 2u) != 0u) oColor.g = -oColor.g - 1.0;
    if ((footprintForTests & 4u) != 0u) oColor.b = -oColor.b - 1.0;
");
    }

    static string AfterOnce(string text, string anchor, string addition)
    {
        int at = text.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the footprint export expects one {anchor.Trim()}");
        return text.Insert(at + anchor.Length, addition);
    }

    internal static void ReportAndDecode(float[] colour, ITestOutputHelper output)
    {
        long checkedPixels = 0, uniform = 0, different = 0, dReleased = 0, equalReleased = 0;
        for (int p = 0; p < colour.Length / 4; p++)
        {
            bool both = Decode(colour, p * 4), differs = Decode(colour, p * 4 + 1), released = Decode(colour, p * 4 + 2);
            uint path = TemporalVisibilityProductionPaths.PathAt(colour, p);
            if ((path & (TemporalVisibilityProductionPaths.Fast | TemporalVisibilityProductionPaths.Slow)) == 0) continue;
            checkedPixels++;
            if (!both) continue;
            uniform++;
            if (differs) different++;
            if (released) { dReleased++; if (!differs) equalReleased++; }
        }
        string S(long n, long denominator) => TemporalVisibilityPathShareReadback.Share(n, denominator);
        output.WriteLine($"  whole R8 footprint among {checkedPixels} checked pixels: both uniform {uniform} "
            + $"({S(uniform, checkedPixels)}), same value {uniform - different} ({S(uniform - different, checkedPixels)}), "
            + $"different values {different} ({S(different, checkedPixels)})");
        output.WriteLine($"    D releases among uniform pairs {dReleased}/{uniform} ({S(dReleased, uniform)}), "
            + $"equal pairs {equalReleased}/{uniform - different} ({S(equalReleased, uniform - different)})");
    }

    static bool Decode(float[] colour, int at)
    {
        if (colour[at] >= 0) return false;
        colour[at] = -colour[at] - 1;
        return true;
    }

    // This is metadata only. The live decision, its mask, its state writes and its texture bindings stay unchanged.
    // Slow footprints retain D's position chain. The fast block retains the shipped fma chain at its centre.
    const string FootprintGlsl = @"
uint visibilityFootprintForTests(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv, float expectedDepth,
    uint path) {
    float currentLow = 1.0, currentHigh = 0.0, previousLow = 1.0, previousHigh = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = clamp(centre + ivec2(x, y), ivec2(0), last);
            float value = texelFetch(sampler2D(KeyShadowVisibility, LinearClamp), at, 0).r;
            currentLow = min(currentLow, value);
            currentHigh = max(currentHigh, value);
            if ((path & VisibilitySlowPath) != 0u) {
                vec2 historyUv = previousUv + (vec2(at) + 0.5 - Jitter.xy) / Sizes.xy
                    - uv + PreviousJitter.xy / Sizes.xy;
                vec2 position = historyUv * Sizes.xy - 0.5;
                ivec2 base = ivec2(floor(position));
                for (int j = 0; j < 2; j++) {
                    for (int i = 0; i < 2; i++) {
                        ivec2 corner = clamp(base + ivec2(i, j), ivec2(0), last);
                        float held = texelFetch(sampler2D(PreviousKeyShadowVisibility, LinearClamp), corner, 0).r;
                        previousLow = min(previousLow, held);
                        previousHigh = max(previousHigh, held);
                    }
                }
            }
        }
    }
    if ((path & VisibilityFastPath) != 0u) {
        vec2 historyUv1 = previousUv + (vec2(centre) + 0.5 - Jitter.xy) / Sizes.xy
            - uv + PreviousJitter.xy / Sizes.xy;
        ivec2 block = ivec2(floor(fma(historyUv1, Sizes.xy, vec2(-0.5)))) - 1;
        for (int y = 0; y < 4; y++) {
            for (int x = 0; x < 4; x++) {
                float held = texelFetch(sampler2D(PreviousKeyShadowVisibility, LinearClamp), block + ivec2(x, y), 0).r;
                previousLow = min(previousLow, held);
                previousHigh = max(previousHigh, held);
            }
        }
    }
    if (currentLow != currentHigh || previousLow != previousHigh) return 0u;
    float change;
    bool released = temporalVisibilityChange(uv, centre, last, previousUv, expectedDepth, change)
        && abs(change) > VisibilityReleaseThreshold;
    return 1u | (currentLow != previousLow ? 2u : 0u) | (released ? 4u : 0u);
}
";
}
