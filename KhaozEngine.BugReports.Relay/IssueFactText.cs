using System.Globalization;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Bounds fact text in both graphemes and UTF-16 units without splitting a text element.</summary>
internal static class IssueFactText
{
    public static string Limit(string text)
    {
        int[] elements = StringInfo.ParseCombiningCharacters(text);
        int maxUnits = 2 * IssueLimits.MaxFactChars;
        if (elements.Length <= IssueLimits.MaxFactChars && text.Length <= maxUnits) return text;

        int length = 0;
        for (int i = 0; i < elements.Length && i < IssueLimits.MaxFactChars - 3; i++)
        {
            int end = i + 1 < elements.Length ? elements[i + 1] : text.Length;
            if (end > maxUnits - 3) break;
            length = end;
        }
        return text[..length] + "...";
    }
}
