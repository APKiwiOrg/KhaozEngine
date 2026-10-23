using System;
using System.Text.RegularExpressions;

namespace KhaozEngine.Diagnostics;

/// <summary>Linear home-path matching with a boundary that remains available to adjacent paths.</summary>
internal sealed class ReportHomeRedaction(Regex? pattern, int pathLength, bool maskAll = false)
{
    const int MaximumPatternLength = 4096;

    static readonly ReportHomeRedaction Failed = new(null, 0, maskAll: true);

    internal Regex? Pattern => pattern;

    internal static ReportHomeRedaction Capture(string path, TimeSpan timeout)
    {
        if (path.Length > MaximumPatternLength) return Failed;
        try
        {
            string native = Regex.Escape(path);
            string alternate = Regex.Escape(SwapSeparators(path));
            string body = native == alternate ? native : "(?:" + native + "|" + alternate + ")";
            string expression = body + @"(?:$|[^A-Za-z0-9._-])";
            if (expression.Length > MaximumPatternLength) return Failed;
            return new ReportHomeRedaction(new Regex(expression,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, timeout), path.Length);
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
        if (pattern is null) return text;

        int length = text.Length;
        int search = 0;
        while (search < text.Length)
        {
            Regex.ValueMatchEnumerator matches = pattern.EnumerateMatches(text.AsSpan(search));
            if (!matches.MoveNext()) break;
            length -= pathLength - 1;
            // The regex consumes the boundary instead of using look-ahead. Resume at the path's end so
            // that boundary can also start the next home. Rejected siblings are skipped inside the matcher.
            search += matches.Current.Index + pathLength;
        }
        if (length == text.Length) return text;

        return string.Create(length, (Redactor: this, Text: text),
            static (destination, state) => state.Redactor.CopyRedacted(state.Text, destination));
    }

    void CopyRedacted(string text, Span<char> destination)
    {
        int copied = 0;
        int written = 0;
        while (copied < text.Length)
        {
            Regex.ValueMatchEnumerator matches = pattern!.EnumerateMatches(text.AsSpan(copied));
            if (!matches.MoveNext()) break;
            int index = copied + matches.Current.Index;
            ReadOnlySpan<char> unchanged = text.AsSpan(copied, index - copied);
            unchanged.CopyTo(destination[written..]);
            written += unchanged.Length;
            destination[written++] = '~';
            copied = index + pathLength;
        }
        text.AsSpan(copied).CopyTo(destination[written..]);
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
