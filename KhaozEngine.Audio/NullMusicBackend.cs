namespace KhaozEngine.Audio;

/// <summary>
/// No-op backend used when no audio device / OpenAL implementation is available (headless servers, CI,
/// machines without sound). Keeps <see cref="AudioSystem"/> usable and silent instead of throwing.
/// </summary>
internal sealed class NullMusicBackend : IMusicBackend
{
    public string Name => "Null (no audio)";
    public int TrackCount => 0;
    public bool IsPlaying => false;
    public bool TryLoadTrack(string contentDirectory, string trackName) => false;
    public bool TryPlayTrack(int trackIndex, float volume) => false;
    public void Stop() { }
    public void SetVolume(float volume) { }
    public void Update() { }
    public void Dispose() { }
}
