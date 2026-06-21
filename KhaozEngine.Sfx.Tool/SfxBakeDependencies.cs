using System;

namespace KhaozEngine.Sfx;

/// <summary>
/// Injectable seams for <see cref="SfxBakeCommands.Run(string[], System.IO.TextWriter, System.IO.TextWriter, SfxBakeDependencies)"/>.
/// The default <c>Run</c> overload wires the real implementations; tests supply fakes so no network, API, or
/// audio device is touched.
/// </summary>
public sealed class SfxBakeDependencies
{
    /// <summary>Filesystem seam (read manifest, idempotency, write outputs).</summary>
    public required ISfxFileSystem Fs { get; init; }
    /// <summary>ElevenLabs network seam.</summary>
    public required IElevenLabsSfxClient Client { get; init; }
    /// <summary>Encoder seam (ffmpeg / oggenc).</summary>
    public required IAudioEncoder Encoder { get; init; }
    /// <summary>Vorbis preflight probe.</summary>
    public required Func<VorbisProbeResult> ProbeVorbis { get; init; }
    /// <summary>The ElevenLabs API key (from ELEVENLABS_API_KEY), or null/empty if unset.</summary>
    public required string? ApiKey { get; init; }
    /// <summary>Current UTC timestamp as ISO 8601, for sidecar provenance (injectable for deterministic tests).</summary>
    public required Func<string> UtcNowIso { get; init; }
}
