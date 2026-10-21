using System;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// Fragment rewrites for the cost experiment only. V0 uses no rewrite.
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
        return source[..start] + "    bool visibilityReleased = false;\n" + source[end..];
    }

    internal static string WithoutVisibility(string source)
    {
        if (!source.Contains(ShaderSources.TemporalVisibilityGlsl, StringComparison.Ordinal)) return source;
        string rewritten = WithoutCheck(source).Replace(ShaderSources.TemporalVisibilityGlsl, "", StringComparison.Ordinal);
        return Regex.Replace(rewritten,
            @"^layout\(set=0, binding=\d+\) uniform (texture2D (KeyShadowVisibility|PreviousKeyShadowVisibility);|VisibilityHistory \{[^\r\n]*\};)\r?\n",
            "", RegexOptions.Multiline);
    }

    /// <summary>The lock release barrier as production writes it.</summary>
    internal const string Barrier =
        "float temporalOrdered(float v) { return uintBitsToFloat(floatBitsToUint(v) | (floatBitsToUint(Sizes.x) >> 31u)); }";
    /// <summary>The barrier as V4 writes it, the identity, so a compiler may fuse and reorder the release arithmetic
    /// again.</summary>
    internal const string NoBarrier = "float temporalOrdered(float v) { return v; }";

    /// <summary>Production's lock release with its barriers made the identity. A program without the release comes
    /// back unchanged, and one holding the barrier more than once, or the release without it, throws.</summary>
    internal static string WithoutBarriers(string source)
    {
        int barriers = Regex.Matches(source, Regex.Escape(Barrier)).Count;
        if (barriers == 0 && !source.Contains("float temporalReleaseLock(", StringComparison.Ordinal)) return source;
        if (barriers != 1)
            throw new InvalidOperationException($"the barrier cost rewrite found {barriers} lock release barriers, one expected");
        return source.Replace(Barrier, NoBarrier, StringComparison.Ordinal);
    }

    /// <summary>V4: V5's program with the lock release barriers removed.</summary>
    internal static string WithoutVisibilityOrBarriers(string source) => WithoutBarriers(WithoutVisibility(source));
}
