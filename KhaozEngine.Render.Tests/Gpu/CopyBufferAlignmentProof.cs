using System;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Narrow source proofs for offsets that are validated at runtime or converted from uint words.</summary>
internal static class CopyBufferAlignmentProof
{
    internal static bool Accepts(string expression, string source, int copyIndex, string helper)
    {
        if (Regex.IsMatch(expression, @"^CoverageGpuLayout\.Bytes\([^;]+\)$")
            && BalancedArgument(expression))
            return Regex.Matches(helper, @"\buint\s+Bytes\s*\(\s*uint\s+words\s*\)\s*=>\s*"
                + @"checked\s*\(\s*words\s*\*\s*sizeof\s*\(\s*uint\s*\)\s*\)\s*;").Count == 1;
        if (!Regex.IsMatch(expression, @"^[A-Za-z_]\w*$")) return false;

        // Blank literals before reading braces or statements. SourceSweep already blanks comments for the sweep.
        string text = Regex.Replace(source, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", m => new string(' ', m.Length));
        const string methodPattern = @"\b[A-Za-z_][\w<>?,.\[\]]*\s+[A-Za-z_]\w*\s*\([^()]*\)\s*\{";
        foreach (Match method in Regex.Matches(text, methodPattern))
        {
            int start = method.Index + method.Length;
            if (start > copyIndex) break;
            string prefix = text[start..copyIndex];
            // Only a direct method-body copy is proved. A nested function or conditional validation cannot qualify.
            if (Depth(prefix) != 0 || Regex.IsMatch(prefix, @"\b(?:goto|try|catch|delegate)\b|=>")) continue;
            int end = start;
            int braces = 1;
            while (end < text.Length && braces != 0)
            {
                if (text[end] == '{') braces++;
                else if (text[end] == '}') braces--;
                end++;
            }
            string body = text[start..end];
            if (Regex.IsMatch(body, methodPattern + @"|=>|\bdelegate\b")) continue;
            foreach (Match validation in Regex.Matches(prefix,
                @"(?:^|[;{}])\s*(?<call>GpuCopyAlignment\.RequireAlignedOffset)\s*\(\s*"
                + Regex.Escape(expression) + @"\s*,[^;]*\)\s*;"))
            {
                if (Depth(prefix[..validation.Groups["call"].Index]) != 0) continue;
                string after = prefix[(validation.Index + validation.Length)..];
                string name = Regex.Escape(expression);
                // A validated value must stay unchanged, including by an alias handed to another method.
                if (Regex.IsMatch(body, @"\b(?:ref|out)\s+" + name + @"\b|&\s*\b" + name + @"\b")
                    || Regex.IsMatch(after, @"\b" + name + @"\s*(?:[+*/%&|^\-]?=(?!=)|<<=|>>>=|>>=|\+\+|--)|(?:\+\+|--)\s*\b" + name + @"\b"))
                    continue;
                if (Regex.IsMatch(after, @"\([^;{}]*\b" + name + @"\b[^;{}]*\)\s*=(?!=)")) continue;
                return true;
            }
        }
        return false;
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
