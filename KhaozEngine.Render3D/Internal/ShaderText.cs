using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Exact, checked splicing of GLSL source, for a shader variant built from a shipped program instead of copied from it.
/// Every anchor must occur exactly once, so a base program edited out from under a variant fails when the variant is
/// first built instead of compiling a variant that quietly lost its insertion. A variant held in a static field of
/// <see cref="ShaderSources"/> is built by that type's initializer, so a failed splice surfaces as a
/// <see cref="TypeInitializationException"/> whose inner exception names the anchor or quotes the source.
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
        // Search again from the next character, so an anchor that overlaps its own repeat is still caught.
        if (source.IndexOf(anchor, at + 1, StringComparison.Ordinal) >= 0)
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
            throw new InvalidOperationException(
                $"The shader does not end with main, so there is no end of main to insert before. It begins '{Excerpt(source)}'.");
        return string.Concat(source.AsSpan(0, close), insert, source.AsSpan(close));
    }

    // The first line after the version directive, enough to tell which program a failed splice was built on.
    static string Excerpt(string source)
    {
        string body = source.StartsWith("#version", StringComparison.Ordinal) && source.IndexOf('\n') is var nl and >= 0
            ? source[(nl + 1)..]
            : source;
        int end = body.IndexOf('\n');
        string line = (end < 0 ? body : body[..end]).Trim();
        return line.Length <= 80 ? line : line[..80];
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
