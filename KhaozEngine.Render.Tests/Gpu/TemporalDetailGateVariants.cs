using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

public enum TemporalDetailGate { Production, Narrower, Wider }

/// <summary>Diagnostic interventions in the test source seam. The shipped gate and setting stay unchanged.</summary>
internal static class TemporalDetailGateVariants
{
    internal static string Rewrite(string source, TemporalDetailGate gate)
    {
        if (gate == TemporalDetailGate.Production || !source.Contains(TemporalDetailMath.GateGlsl, StringComparison.Ordinal))
            return source;
        string replacement = gate switch
        {
            TemporalDetailGate.Narrower => TemporalDetailMath.GateGlsl
                .Replace("const uint distrustMask = 3u;", "const uint distrustMask = 1u;", StringComparison.Ordinal)
                .Replace(" && !(locked && motion <= ShimmerStillPixels)", "", StringComparison.Ordinal),
            TemporalDetailGate.Wider => TemporalDetailMath.GateGlsl
                .Replace("const uint distrustMask = 3u;", "const uint distrustMask = 31u;", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(gate)),
        };
        return source.Replace(TemporalDetailMath.GateGlsl, replacement, StringComparison.Ordinal);
    }
}
