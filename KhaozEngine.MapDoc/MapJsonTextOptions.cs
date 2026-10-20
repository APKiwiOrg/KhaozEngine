using System.Text.Json;
using KhaozEngine.Serialization;

namespace KhaozEngine.MapDoc;

internal static class MapJsonTextOptions
{
    // Only the newline follows the engine convention, keeping saves and hashes byte-identical across platforms.
    internal static JsonWriterOptions IndentedWriter => new()
    {
        Indented = true,
        NewLine = JsonDefaults.IndentedWrite.NewLine,
    };

    internal static JsonSerializerOptions CreateSerializer(bool indented = true) => new()
    {
        WriteIndented = indented,
        NewLine = JsonDefaults.IndentedWrite.NewLine,
    };
}
