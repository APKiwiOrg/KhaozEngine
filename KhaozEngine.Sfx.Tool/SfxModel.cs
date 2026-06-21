using System;
using System.Collections.Generic;

namespace KhaozEngine.Sfx;

/// <summary>Target container the baked effect is encoded to.</summary>
public enum SfxFormat
{
    /// <summary>OGG Vorbis (default): ~8-9x smaller than WAV at q5, the bulk-SFX default.</summary>
    Ogg,
    /// <summary>16-bit PCM WAV at 44.1 kHz (the only WAV the engine's WavDecoder accepts).</summary>
    Wav,
}

/// <summary>Channel layout of the baked effect.</summary>
public enum SfxChannels
{
    /// <summary>Mono (default): required for OpenAL 3D positioning - stereo sources skip spatialization.</summary>
    Mono,
    /// <summary>Stereo: for UI/ambient effects that are never positioned in 3D.</summary>
    Stereo,
}

/// <summary>
/// One sound effect in a game's <c>sfx.manifest.jsonc</c>. Defaults (format=ogg, channels=mono) are applied
/// during parse, so every field here is already resolved.
/// </summary>
public sealed record SfxEntry
{
    /// <summary>Logical id, also the default play key (e.g. <c>ui/confirm</c>).</summary>
    public required string Key { get; init; }
    /// <summary>The ElevenLabs sound-effect prompt.</summary>
    public required string Prompt { get; init; }
    /// <summary>Requested duration in seconds (0.5-30). Null lets the API auto-pick.</summary>
    public double? DurationSeconds { get; init; }
    /// <summary>Prompt influence (0..1). Null uses the API default (0.3).</summary>
    public double? PromptInfluence { get; init; }
    /// <summary>Target container. Defaults to <see cref="SfxFormat.Ogg"/>.</summary>
    public SfxFormat Format { get; init; } = SfxDefaults.Format;
    /// <summary>Target channel layout. Defaults to <see cref="SfxChannels.Mono"/>.</summary>
    public SfxChannels Channels { get; init; } = SfxDefaults.Channels;
    /// <summary>Output path, relative to the manifest file's directory.</summary>
    public required string Out { get; init; }
}

/// <summary>A parsed <c>sfx.manifest.jsonc</c>: global generation settings plus the list of effects.</summary>
public sealed record SfxManifest
{
    /// <summary>ElevenLabs model id. Defaults to <see cref="SfxDefaults.Model"/>.</summary>
    public string Model { get; init; } = SfxDefaults.Model;
    /// <summary>API source <c>output_format</c> requested before local encoding. Defaults to <see cref="SfxDefaults.SourceFormat"/>.</summary>
    public string SourceFormat { get; init; } = SfxDefaults.SourceFormat;
    /// <summary>The effects to bake.</summary>
    public IReadOnlyList<SfxEntry> Sounds { get; init; } = Array.Empty<SfxEntry>();
}

/// <summary>Built-in defaults for manifest fields and encoding, per the engine SFX format policy.</summary>
public static class SfxDefaults
{
    /// <summary>Default ElevenLabs sound-effects model.</summary>
    public const string Model = "eleven_text_to_sound_v2";
    /// <summary>Default API source format: highest-fidelity self-describing mp3 (single lossy step before encode).</summary>
    public const string SourceFormat = "mp3_44100_192";
    /// <summary>Default OGG Vorbis quality (~q5, ~12 KB/s mono).</summary>
    public const int OggQuality = 5;
    /// <summary>Sample rate the engine's WavDecoder expects for WAV output.</summary>
    public const int WavSampleRate = 44100;
    /// <summary>Default target container.</summary>
    public const SfxFormat Format = SfxFormat.Ogg;
    /// <summary>Default target channel layout.</summary>
    public const SfxChannels Channels = SfxChannels.Mono;
    /// <summary>Sidecar file suffix appended to each output path.</summary>
    public const string SidecarSuffix = ".sfxmeta";
}

/// <summary>Thrown when a manifest cannot be parsed or fails validation. Message is user-facing.</summary>
public sealed class SfxManifestException : Exception
{
    public SfxManifestException(string message) : base(message) { }
}
