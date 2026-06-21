namespace KhaozEngine.Sfx;

/// <summary>A single ElevenLabs sound-effects generation request.</summary>
public sealed record SfxGenRequest
{
    /// <summary>The sound-effect prompt.</summary>
    public required string Prompt { get; init; }
    /// <summary>Requested duration in seconds, or null to let the API auto-pick.</summary>
    public double? DurationSeconds { get; init; }
    /// <summary>Prompt influence (0..1), or null for the API default.</summary>
    public double? PromptInfluence { get; init; }
    /// <summary>ElevenLabs model id.</summary>
    public required string Model { get; init; }
    /// <summary>API <c>output_format</c> query value (e.g. <c>mp3_44100_192</c> or <c>pcm_44100</c>).</summary>
    public required string OutputFormat { get; init; }
}

/// <summary>
/// The network seam to the ElevenLabs text-to-sound-effects REST endpoint. Abstracted so the bake pipeline is
/// unit-testable without hitting the network or the paid API.
/// </summary>
public interface IElevenLabsSfxClient
{
    /// <summary>Generates one effect and returns the raw source audio bytes (mp3 or PCM per output format).</summary>
    byte[] Generate(SfxGenRequest request);
}
