using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Serialization;

namespace KhaozEngine.Sfx;

/// <summary>
/// Parses a per-game <c>sfx.manifest.jsonc</c>. JSONC = System.Text.Json with comments skipped and trailing
/// commas allowed, so the list can carry comments. Applies defaults and validates each entry, throwing
/// <see cref="SfxManifestException"/> with a user-facing message on any problem.
/// </summary>
public static class SfxManifestParser
{
    // ElevenLabs sound-effects duration bounds (seconds); see the text-to-sound-effects API.
    const double MinDuration = 0.5, MaxDuration = 30.0;

    /// <summary>Parses manifest JSONC text into a validated <see cref="SfxManifest"/>.</summary>
    public static SfxManifest Parse(string json)
    {
        RawManifest? raw;
        try
        {
            // Route through the engine-wide JSONC read policy (comments + trailing commas + case-insensitive).
            raw = JsonSerializer.Deserialize<RawManifest>(json, Jsonc.Options);
        }
        catch (JsonException ex)
        {
            throw new SfxManifestException($"manifest is not valid JSON: {ex.Message}");
        }
        if (raw is null) throw new SfxManifestException("manifest is empty or null.");

        var entries = new List<SfxEntry>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        List<RawEntry> sounds = raw.Sounds ?? new List<RawEntry>();
        for (int i = 0; i < sounds.Count; i++)
        {
            entries.Add(MapEntry(sounds[i], i, seenKeys));
        }

        return new SfxManifest
        {
            Model = string.IsNullOrWhiteSpace(raw.Model) ? SfxDefaults.Model : raw.Model!,
            SourceFormat = string.IsNullOrWhiteSpace(raw.SourceFormat) ? SfxDefaults.SourceFormat : raw.SourceFormat!,
            Sounds = entries,
        };
    }

    static SfxEntry MapEntry(RawEntry e, int index, HashSet<string> seenKeys)
    {
        string where = $"sounds[{index}]";
        if (string.IsNullOrWhiteSpace(e.Key)) throw new SfxManifestException($"{where}: 'key' is required.");
        if (string.IsNullOrWhiteSpace(e.Prompt)) throw new SfxManifestException($"{where} ('{e.Key}'): 'prompt' is required.");
        if (string.IsNullOrWhiteSpace(e.Out)) throw new SfxManifestException($"{where} ('{e.Key}'): 'out' is required.");
        if (!seenKeys.Add(e.Key!)) throw new SfxManifestException($"{where}: duplicate key '{e.Key}'.");

        if (e.DurationSeconds is { } d && (d < MinDuration || d > MaxDuration))
            throw new SfxManifestException($"{where} ('{e.Key}'): durationSeconds {d} is outside the {MinDuration}-{MaxDuration}s range.");
        if (e.PromptInfluence is { } p && (p < 0.0 || p > 1.0))
            throw new SfxManifestException($"{where} ('{e.Key}'): promptInfluence {p} is outside the 0..1 range.");

        return new SfxEntry
        {
            Key = e.Key!,
            Prompt = e.Prompt!,
            DurationSeconds = e.DurationSeconds,
            PromptInfluence = e.PromptInfluence,
            Format = ParseFormat(e.Format, where, e.Key!),
            Channels = ParseChannels(e.Channels, where, e.Key!),
            Out = e.Out!,
        };
    }

    static SfxFormat ParseFormat(string? value, string where, string key) =>
        value is null ? SfxDefaults.Format : value.Trim().ToLowerInvariant() switch
        {
            "ogg" => SfxFormat.Ogg,
            "wav" => SfxFormat.Wav,
            _ => throw new SfxManifestException($"{where} ('{key}'): unknown format '{value}' (expected ogg or wav)."),
        };

    static SfxChannels ParseChannels(string? value, string where, string key) =>
        value is null ? SfxDefaults.Channels : value.Trim().ToLowerInvariant() switch
        {
            "mono" => SfxChannels.Mono,
            "stereo" => SfxChannels.Stereo,
            _ => throw new SfxManifestException($"{where} ('{key}'): unknown channels '{value}' (expected mono or stereo)."),
        };

    sealed class RawManifest
    {
        public string? Model { get; set; }
        public string? SourceFormat { get; set; }
        public List<RawEntry>? Sounds { get; set; }
    }

    sealed class RawEntry
    {
        public string? Key { get; set; }
        public string? Prompt { get; set; }
        public double? DurationSeconds { get; set; }
        public double? PromptInfluence { get; set; }
        public string? Format { get; set; }
        public string? Channels { get; set; }
        [JsonPropertyName("out")] public string? Out { get; set; }
    }
}
