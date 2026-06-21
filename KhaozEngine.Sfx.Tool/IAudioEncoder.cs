namespace KhaozEngine.Sfx;

/// <summary>
/// The encoder seam: turns source bytes on disk into the target OGG/WAV. Abstracted so the bake pipeline is
/// unit-testable without running ffmpeg/oggenc.
/// </summary>
public interface IAudioEncoder
{
    /// <summary>Encodes per <paramref name="request"/>, using <paramref name="vorbisBackend"/> for OGG output. Throws on failure.</summary>
    void Encode(EncodeRequest request, VorbisBackend vorbisBackend);
}
