using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A separately compiled specialization of the shared accumulation. Only the visibility decision becomes
/// false. Its unused declarations and helpers are removed to match the compact layout, the shimmer score's shadow
/// evidence with them, so the score there reads the luma record alone. Every other release stays unchanged.</summary>
internal static class TemporalVisibilityPrograms
{
    internal static string WithoutDecision(string source)
    {
        const string first = "    bool visibilityReleased = ";
        const string last = "    if (visibilityReleased) lockValue = 0.0;";
        int start = source.IndexOf(first, StringComparison.Ordinal);
        if (start < 0) return source;
        int end = source.IndexOf(last, start, StringComparison.Ordinal);
        if (end < 0 || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The visibility specialization requires one complete decision.");
        string certified = source[..start] + "    bool visibilityReleased = false;\n" + source[end..];
        return certified.Replace(ShaderSources.TemporalResolveVisibilityBindingsGlsl, string.Empty, StringComparison.Ordinal)
            .Replace(ShaderSources.TemporalAccumulateVisibilityBindingsGlsl, string.Empty, StringComparison.Ordinal)
            .Replace(ShaderSources.TemporalVisibilityGlsl, string.Empty, StringComparison.Ordinal)
            .Replace(ShaderSources.TemporalShimmerVisibilityGlsl, string.Empty, StringComparison.Ordinal);
    }
}
