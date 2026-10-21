using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Test-only extraction of the real call site for the isolated lock probe. No production test seam.</summary>
internal static class TemporalLockZeroBypass
{
    internal static string CallSite
    {
        get
        {
            const string first = "    if (ridge) lockValue = 1.0;\n";
            const string last = "    // Preserve the colour release's carried-lock classification";
            string source = ShaderSources.TemporalAccumulateGlsl.Replace("\r\n", "\n", StringComparison.Ordinal);
            int start = source.IndexOf(first, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("the lock probe found no ridge classification");
            start += first.Length;
            int end = source.IndexOf(last, start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("the lock probe found no end to the lock release");
            return source[start..end];
        }
    }

    // The shader's exponent tests. MathF.IsFinite would not prove their bit boundaries.
    internal static bool Qualifies(float value, float motion, float reactive, float edge)
        => BitConverter.SingleToUInt32Bits(value) == 0u && Finite(motion) && Finite(reactive) && Finite(edge);

    static bool Finite(float v) => (BitConverter.SingleToUInt32Bits(v) & 0x7f800000u) != 0x7f800000u;
}
