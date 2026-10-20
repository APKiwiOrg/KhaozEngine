using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// The cost experiment changes only programs supplied through fragmentSourceForTests. V0 uses no rewrite.
internal static class TemporalVisibilityCostPrograms
{
    internal static string WithoutCheck(string source)
    {
        const string first = "    bool visibilityReleased = ";
        const string last = "    if (visibilityReleased) lockValue = 0.0;";
        int start = source.IndexOf(first, StringComparison.Ordinal);
        if (start < 0) return source;
        int end = source.IndexOf(last, start, StringComparison.Ordinal);
        if (end < 0 || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("the cost rewrite found an incomplete or repeated visibility call site");
        // No short circuit or helper evaluation. The mask construction and store are still shipped text.
        return source[..start] + "    bool visibilityReleased = false;\n" + source[end..];
    }

    internal static string WithoutCheckOrMask(string source)
    {
        string uncheckedSource = WithoutCheck(source);
        // The unused decision helper names the mask constants, so remove it before restoring the pre-7f.3 surface.
        uncheckedSource = uncheckedSource.Replace(ShaderSources.TemporalVisibilityGlsl, "", StringComparison.Ordinal);
        return TemporalVisibilityFrozenDMask.Restore(uncheckedSource);
    }
}
