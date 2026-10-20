using System;
using System.Text.Json;

namespace KhaozEngine.BugReports.Contracts;

// Whether every string value and property name in a JSON document decodes to valid UTF-16. Syntactically valid
// JSON can still hold invalid UTF-8 or an escaped lone surrogate such as "\uD800", which only fails when a
// reader decodes the string. Uploaded documents are checked here so that no later reader meets one.
internal static class JsonStrings
{
    // A maxDepth of 0 is the reader default of 64.
    public static bool AllDecode(ReadOnlySpan<byte> json, int maxDepth)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        try
        {
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) reader.GetString();
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
