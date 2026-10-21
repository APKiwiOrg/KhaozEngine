using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A separately compiled specialization of the shared accumulation. Only the visibility decision becomes
/// false. The compiler removes its unreachable helpers and footprint loads, leaving every other release unchanged.</summary>
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
        return source[..start] + "    bool visibilityReleased = false;\n" + source[end..];
    }
}
