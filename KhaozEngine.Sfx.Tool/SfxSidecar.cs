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
    /// <summary>Hash of the generation inputs, computed by <see cref="SfxHasher"/> under <see cref="HashVersion"/>.</summary>
    public required string Hash { get; init; }
    /// <summary>
    /// Hash scheme of <see cref="Hash"/>. Missing on sidecars written before scheme 2, whose hash also covered
    /// <see cref="Model"/>.
    /// </summary>
    public int? HashVersion { get; init; }
    /// <summary>The entry key this sidecar belongs to (human-readable provenance).</summary>
    public required string Key { get; init; }
    /// <summary>UTC timestamp the output was generated (ISO 8601), if recorded.</summary>
    public string? GeneratedUtc { get; init; }
    /// <summary>ElevenLabs model id used (provenance).</summary>
    public string? Model { get; init; }
    /// <summary>API source format used (provenance).</summary>
    public string? SourceFormat { get; init; }

    /// <summary>
    /// True when this sidecar was written for <paramref name="entry"/>'s current inputs. <paramref name="hash"/> is
    /// the current-scheme hash. A scheme 1 sidecar is checked against the model it recorded and the scheme 1
    /// default, never the manifest's current model, so a model change does not invalidate it. An unknown scheme
    /// never matches.
    /// </summary>
    public bool Matches(SfxEntry entry, string sourceFormat, string hash) => HashVersion switch
    {
        SfxHasher.HashVersion => Hash == hash,
        null => (Model is not null && Hash == SfxHasher.ComputeLegacy(entry, Model, sourceFormat))
            || Hash == SfxHasher.ComputeLegacy(entry, SfxHasher.LegacyDefaultModel, sourceFormat),
        _ => false,
    };

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
