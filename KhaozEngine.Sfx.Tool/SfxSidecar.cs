using System;
using System.Text.Json;
using KhaozEngine.Serialization;

namespace KhaozEngine.Sfx;

/// <summary>
/// Idempotency sidecar written next to each baked output (e.g. <c>confirm.ogg.sfxmeta</c>). Holds the hash of
/// the generation inputs so a re-run can skip entries whose inputs are unchanged.
/// </summary>
public sealed record SfxSidecar
{
    /// <summary>Hash of (prompt + duration + influence + format + channels + model + source format).</summary>
    public required string Hash { get; init; }
    /// <summary>The entry key this sidecar belongs to (human-readable provenance).</summary>
    public required string Key { get; init; }
    /// <summary>UTC timestamp the output was generated (ISO 8601), if recorded.</summary>
    public string? GeneratedUtc { get; init; }
    /// <summary>ElevenLabs model id used (provenance).</summary>
    public string? Model { get; init; }
    /// <summary>API source format used (provenance).</summary>
    public string? SourceFormat { get; init; }

    /// <summary>Serializes to indented JSON (a generated file, so plain JSON per the engine write policy).</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonDefaults.IndentedWrite);

    /// <summary>Parses a sidecar; returns null if the text is missing or unreadable (treated as "regenerate").</summary>
    public static SfxSidecar? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            SfxSidecar? s = JsonSerializer.Deserialize<SfxSidecar>(json, Jsonc.Options);
            return string.IsNullOrEmpty(s?.Hash) ? null : s;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
