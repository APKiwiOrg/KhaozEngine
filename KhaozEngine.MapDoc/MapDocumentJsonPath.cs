using System;

namespace KhaozEngine.MapDoc;

/// <summary>Formats property segments like System.Text.Json's exception paths.</summary>
internal static class MapDocumentJsonPath
{
    // Matches JsonReaderHelper.ContainsSpecialCharacters in System.Text.Json.
    internal static string AppendProperty(string path, string name) =>
        name.AsSpan().IndexOfAny(". '/\"[]()\t\n\r\f\b\\\u0085\u2028\u2029") >= 0
            ? path + "['" + name + "']"
            : path + "." + name;
}
