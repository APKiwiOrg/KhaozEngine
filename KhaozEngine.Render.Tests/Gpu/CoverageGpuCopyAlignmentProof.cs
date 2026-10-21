using System;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Coverage word offsets are aligned only while the checked byte conversion stays exact.</summary>
internal static class CoverageGpuCopyAlignmentProof
{
    internal static bool Accepts(string expression, string helper)
    {
        return Regex.IsMatch(expression, @"^CoverageGpuLayout\.Bytes\([^;]+\)$")
            && BalancedArgument(expression)
            && !helper.Contains('#', StringComparison.Ordinal)
            && Regex.Matches(helper, @"\b[A-Za-z_][\w<>?,.\[\]]*\s+Bytes\s*(?:<[^<>]*>)?\s*\(").Count == 1
            && Regex.Matches(helper, @"\buint\s+Bytes\s*\(\s*uint\s+words\s*\)\s*=>\s*"
                + @"checked\s*\(\s*words\s*\*\s*sizeof\s*\(\s*uint\s*\)\s*\)\s*;").Count == 1;
    }

    static bool BalancedArgument(string expression)
    {
        int open = expression.IndexOf('(', StringComparison.Ordinal);
        return Depth(expression[(open + 1)..^1], '(', ')') == 0;
    }

    static int Depth(string text, char open = '{', char close = '}')
    {
        int depth = 0;
        foreach (char c in text)
        {
            if (c == open) depth++;
            else if (c == close && --depth < 0) return -1;
        }
        return depth;
    }
}
