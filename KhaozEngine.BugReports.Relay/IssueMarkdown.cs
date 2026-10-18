using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.BugReports.Relay;

internal static class IssueMarkdown
{
    public static string Neutralise(string text)
    {
        string value = NeutralisePlainText(text);
        var result = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
                result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }

    public static string Quote(string text)
    {
        string[] lines = Neutralise(NormaliseNewlines(text)).Split('\n');
        var result = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.Append('\n');
            result.Append('>');
            if (lines[i].Length > 0) result.Append(' ').Append(lines[i]);
        }
        return result.ToString();
    }

    public static string TableCell(string text) => FlattenLines(Neutralise(NormaliseNewlines(text)));

    public static string InlineCode(string text)
    {
        string value = FlattenLines(NormaliseNewlines(text)).Replace("|", "\\|", StringComparison.Ordinal);
        string fence = new('`', LongestBacktickRun(value) + 1);
        bool pad = value.StartsWith('`') || value.EndsWith('`')
            || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' ') && value.Trim(' ').Length > 0);
        return pad ? fence + " " + value + " " + fence : fence + value + fence;
    }

    public static string Fenced(IReadOnlyList<string> lines)
    {
        int longest = 0;
        foreach (string line in lines) longest = Math.Max(longest, LongestBacktickRun(line));
        string fence = new('`', Math.Max(3, longest + 1));
        var result = new StringBuilder(fence).Append("text\n");
        foreach (string line in lines) result.Append(line).Append('\n');
        return result.Append(fence).ToString();
    }

    internal static string NormaliseNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static string NeutralisePlainText(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsControl(c) || c is '\n' or '\t') clean.Append(c);
        text = clean.ToString();
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '@' || (c == '#' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
                result.Append(c).Append('\u200b');
            else if (i + 3 < text.Length && text.AsSpan(i, 3).Equals("gh-", StringComparison.OrdinalIgnoreCase)
                && char.IsAsciiDigit(text[i + 3]))
            {
                result.Append(text.AsSpan(i, 2)).Append("\u200b-");
                i += 2;
            }
            else if (i + 10 <= text.Length && text.AsSpan(i, 10).Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                result.Append(text.AsSpan(i, 6)).Append("\u200b.com");
                i += 9;
            }
            else if (text.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || text.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                int schemeLength = text[i + 4] is 's' or 'S' ? 5 : 4;
                result.Append(text.AsSpan(i, schemeLength)).Append('\u200b');
                i += schemeLength - 1;
            }
            else if (text.AsSpan(i).StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                result.Append(text.AsSpan(i, 3)).Append('\u200b');
                i += 2;
            }
            else result.Append(c);
        }
        return result.ToString();
    }

    static string FlattenLines(string text) => Regex.Replace(text, "\n+", " ");

    static int LongestBacktickRun(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }
}
