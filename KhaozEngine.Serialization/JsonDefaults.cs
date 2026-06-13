using System.Text.Json;

namespace KhaozEngine.Serialization;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> baselines so every package serializes JSON the same way.
/// Each property returns a single shared, effectively read-only instance (System.Text.Json freezes
/// options on first use), suitable as a fallback default. Callers that need converters or other
/// tweaks should construct their own options and pass them through the relevant API instead.
/// </summary>
public static class JsonDefaults
{
    /// <summary>Tolerant reader for loading config: case-insensitive property names, <c>//</c> comments
    /// skipped, and trailing commas allowed. Used by config/content loading.</summary>
    public static JsonSerializerOptions TolerantRead { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Human-readable writer for files a person might open: <see cref="JsonSerializerOptions.WriteIndented"/>.
    /// Used by saves/settings persistence.</summary>
    public static JsonSerializerOptions IndentedWrite { get; } = new()
    {
        WriteIndented = true,
    };

    /// <summary>Round-trips public fields, not just properties (<see cref="JsonSerializerOptions.IncludeFields"/>).
    /// Used by the ECS world serializer, whose component structs expose fields. Add converters by passing
    /// your own options for value types that don't round-trip by default (e.g. MonoGame Color).</summary>
    public static JsonSerializerOptions IncludeFields { get; } = new()
    {
        IncludeFields = true,
    };
}
