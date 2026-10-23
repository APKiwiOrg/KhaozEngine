using System;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>Linear home-path matching with a boundary that remains available to adjacent paths.</summary>
internal sealed class ReportHomeRedaction(Regex? pattern, bool maskAll = false)
{
    const int MaximumPatternLength = 4096;

    static readonly ReportHomeRedaction Failed = new(null, maskAll: true);

    internal Regex? Pattern => pattern;

    internal static ReportHomeRedaction Capture(string path, TimeSpan timeout)
    {
        if (path.Length > MaximumPatternLength) return Failed;
        try
        {
            string native = Regex.Escape(path);
            string alternate = Regex.Escape(SwapSeparators(path));
            string body = native == alternate ? native : "(?:" + native + "|" + alternate + ")";
            string expression = "(" + body + @")(?:$|[^A-Za-z0-9._-])";
            if (expression.Length > MaximumPatternLength) return Failed;
            return new ReportHomeRedaction(new Regex(expression,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, timeout));
        }
        catch (Exception)
        {
            // A refused literal pattern must not expose the home directory.
            return Failed;
        }
    }

    internal string Redact(string text)
    {
        if (maskAll || text.Length > ReportRedactor.MaximumInputLength) return ReportRedactor.Mask;
        if (pattern is null || !pattern.IsMatch(text)) return text;
        Match match = pattern.Match(text);
        if (!match.Success) return text;

        var output = new StringBuilder(text.Length);
        int copied = 0;
        do
        {
            output.Append(text, copied, match.Index - copied).Append('~');
            copied = match.Index + match.Groups[1].Length;
            // The regex consumes the boundary instead of using look-ahead. Resume at the path's end so
            // that boundary can also start the next home. Rejected siblings are skipped inside the matcher.
            match = pattern.Match(text, copied);
        }
        while (match.Success);
        return output.Append(text, copied, text.Length - copied).ToString();
    }

    static string SwapSeparators(string path)
    {
        char[] chars = path.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '/') chars[i] = '\\';
            else if (chars[i] == '\\') chars[i] = '/';
        }
        return new string(chars);
    }
}
