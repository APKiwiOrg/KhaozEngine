using System;
using System.Text.RegularExpressions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Narrow source proofs for offsets that are validated at runtime or converted from uint words.</summary>
internal static class CopyBufferAlignmentProof
{
    const string ScalarType = @"(?:bool|byte|sbyte|char|short|ushort|int|uint|long|ulong|nint|nuint|float|double|decimal)";

    internal static bool Accepts(string expression, string source, int copyIndex, string helper)
    {
        if (Regex.IsMatch(expression, @"^CoverageGpuLayout\.Bytes\([^;]+\)$")
            && BalancedArgument(expression))
            return !helper.Contains('#', StringComparison.Ordinal)
                && Regex.Matches(helper, @"\b[A-Za-z_][\w<>?,.\[\]]*\s+Bytes\s*(?:<[^<>]*>)?\s*\(").Count == 1
                && Regex.Matches(helper, @"\buint\s+Bytes\s*\(\s*uint\s+words\s*\)\s*=>\s*"
                + @"checked\s*\(\s*words\s*\*\s*sizeof\s*\(\s*uint\s*\)\s*\)\s*;").Count == 1;
        if (!Regex.IsMatch(expression, @"^[A-Za-z_]\w*$")) return false;

        // Blank literals before reading braces or statements. SourceSweep already blanks comments for the sweep.
        string text = Regex.Replace(source, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", m => new string(' ', m.Length));
        const string methodPattern = @"\b[A-Za-z_][\w<>?,.\[\]]*\s+[A-Za-z_]\w*\s*\((?<parameters>[^()]*)\)\s*\{";
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
            if (Regex.IsMatch(body, methodPattern + @"|=>|#|\bdelegate\b")) continue;
            foreach (Match validation in Regex.Matches(prefix,
                @"(?:^|[;{}])\s*(?<call>GpuCopyAlignment\.RequireAlignedOffset)\s*\(\s*"
                + Regex.Escape(expression) + @"\s*,[^;]*\)\s*;"))
            {
                string before = prefix[..validation.Groups["call"].Index];
                if (Depth(before) != 0 || !IndependentScalar(expression, method.Groups["parameters"].Value, before)) continue;
                string after = prefix[(validation.Index + validation.Length)..];
                string name = Regex.Escape(expression);
                string value = @"(?:\(\s*)*\b" + name + @"\b(?:\s*\))*";
                // A validated value must stay unchanged, including by an alias handed to another method.
                if (Regex.IsMatch(body, @"\b(?:ref|out)\s+" + value + @"|&\s*" + value)
                    || Regex.IsMatch(after, value + @"\s*(?:[+*/%&|^\-]?=(?!=)|<<=|>>>=|>>=|\+\+|--)|(?:\+\+|--)\s*" + value))
                    continue;
                if (Regex.IsMatch(after, @"\([^;{}]*\b" + name + @"\b[^;{}]*\)\s*=(?!=)")) continue;
                return true;
            }
        }
        return false;
    }

    static bool IndependentScalar(string expression, string parameters, string before)
    {
        string name = Regex.Escape(expression);
        // A primitive passed by value owns its storage. Parameter modifiers must not be skipped.
        if (Regex.IsMatch(parameters, @"(?:^|,)\s*" + ScalarType + @"\s+" + name + @"\s*(?:=[^,]+)?(?:,|$)"))
            return true;

        // An explicit scalar declaration copies its initializer, even when that value came from mutable storage.
        // Require a direct declaration and a plain initializer so a ref local or an unknown var cannot qualify.
        foreach (Match local in Regex.Matches(before,
            @"(?:^|[;{}])\s*" + ScalarType + @"\s+" + name + @"\s*=\s*(?<value>[^;{}]+);"))
            if (Depth(before[..local.Index]) == 0 && !Regex.IsMatch(local.Groups["value"].Value, @"^\s*ref\b"))
                return true;
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
