using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Exact, checked splicing of GLSL source, for a shader variant built from a shipped program instead of copied from it.
/// Every anchor must occur exactly once, so a base program edited out from under a variant fails when the variant is
/// first built instead of compiling a variant that quietly lost its insertion.
/// </summary>
internal static class ShaderText
{
    /// <summary>Insert <paramref name="insert"/> right after the one occurrence of <paramref name="anchor"/>.</summary>
    /// <exception cref="InvalidOperationException">The anchor is missing or occurs more than once.</exception>
    internal static string After(string source, string anchor, string insert)
    {
        int at = source.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException($"The shader anchor '{anchor}' is missing, so a variant built on it would lose its insertion.");
        if (source.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"The shader anchor '{anchor}' occurs more than once, so the insertion point is ambiguous.");
        int end = at + anchor.Length;
        return string.Concat(source.AsSpan(0, end), insert, source.AsSpan(end));
    }

    /// <summary>Insert <paramref name="insert"/> before the closing brace of <c>main</c>, which must be the last
    /// function in the source.</summary>
    /// <exception cref="InvalidOperationException">The source does not end with <c>main</c>.</exception>
    internal static string BeforeEndOfMain(string source, string insert)
    {
        int main = source.LastIndexOf("void main()", StringComparison.Ordinal);
        int open = main < 0 ? -1 : source.IndexOf('{', main);
        int close = open < 0 ? -1 : MatchingBrace(source, open);
        if (close < 0 || !source.AsSpan(close + 1).Trim().IsEmpty)
            throw new InvalidOperationException("The shader does not end with main, so there is no end of main to insert before.");
        return string.Concat(source.AsSpan(0, close), insert, source.AsSpan(close));
    }

    // The brace that closes the block opened at open, or -1 when the source ends first.
    static int MatchingBrace(string source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
