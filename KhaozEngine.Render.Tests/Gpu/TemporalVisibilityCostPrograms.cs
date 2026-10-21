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

    internal static string WithoutVisibilityOrPrecise(string source)
    {
        string rewritten = WithoutVisibility(source).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string first = "float temporalReleaseLock(", last = "    return result;\n}";
        int start = rewritten.IndexOf(first, StringComparison.Ordinal);
        if (start < 0) return rewritten;
        int end = rewritten.IndexOf(last, start, StringComparison.Ordinal);
        if (end < 0 || rewritten.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("the precise cost rewrite expects one complete lock-release function");
        end += last.Length;
        string release = rewritten[start..end];
        if (Regex.Matches(release, @"\bprecise\b").Count != 2)
            throw new InvalidOperationException("the precise cost rewrite expects two lock-release markings");
        return rewritten[..start] + release.Replace("precise float", "float", StringComparison.Ordinal) + rewritten[end..];
    }
}
