using System.Globalization;
using System.Text;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

internal static class IssueTitle
{
    public static string Compose(string gameDisplayName, string? version, string description)
    {
        string title = "";
        foreach (string line in IssueMarkdown.NormaliseNewlines(description).Split('\n'))
        {
            title = Clean(line).Trim();
            if (title.Length > 0) break;
        }
        int[] elements = StringInfo.ParseCombiningCharacters(title);
        if (elements.Length > BugReportLimits.MaxTitleChars)
            title = title[..elements[BugReportLimits.MaxTitleChars - 3]] + "...";
        return "[" + Clean(gameDisplayName) + " " + Clean(version ?? "unknown") + "] " + title;
    }

    static string Clean(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text)
        {
            if (c is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')
                continue;
            result.Append(c == '\n' ? ' ' : c);
        }
        return IssueMarkdown.NeutralisePlainText(result.ToString());
    }
}
