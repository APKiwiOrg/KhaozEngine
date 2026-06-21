using System.Text.Json;

namespace KhaozEngine.Serialization;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> baselines so every package serializes JSON the same way.
/// Each property returns a single shared, effectively read-only instance (System.Text.Json freezes
/// options on first use), suitable as a fallback default. Callers that need converters or other
/// tweaks should construct their own options and pass them through the relevant API instead.
///
/// <para>JSONC (JSON with comments and trailing commas) is the engine standard for hand-authored config,
/// manifests, settings, and saves. The canonical read policy lives in <see cref="Jsonc"/>; <see cref="TolerantRead"/>
/// is the same instance under its historical name. Writing stays plain JSON (<see cref="IndentedWrite"/>) because
/// System.Text.Json cannot emit comments.</para>
/// </summary>
public static class JsonDefaults
{
    /// <summary>Tolerant reader for loading config: case-insensitive property names, comments skipped, and
    /// trailing commas allowed. This is the engine JSONC read policy; it is the same instance as
    /// <see cref="Jsonc.Options"/>, kept under this name for back-compat. Used by config/content loading.</summary>
    public static JsonSerializerOptions TolerantRead => Jsonc.Options;

    /// <summary>Human-readable writer for files a person might open: <see cref="JsonSerializerOptions.WriteIndented"/>.
    /// System.Text.Json cannot emit comments, so this writes plain JSON; JSONC is a read-time convenience only.
    /// Used by saves/settings persistence.</summary>
    public static JsonSerializerOptions IndentedWrite { get; } = new()
    {
        WriteIndented = true,
    };

    /// <summary>Round-trips public fields, not just properties (<see cref="JsonSerializerOptions.IncludeFields"/>).
    /// Used by the ECS world serializer, whose component structs expose fields. Add converters by passing
    /// your own options for value types that don't round-trip by default (e.g. a struct serialized via a custom JsonConverter).</summary>
    public static JsonSerializerOptions IncludeFields { get; } = new()
    {
        IncludeFields = true,
    };
}
