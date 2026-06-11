using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Audio;

namespace KhaozEngine.Tests;

/// <summary>In-memory <see cref="IMusicBackend"/> recording calls for headless AudioSystem tests.</summary>
internal sealed class FakeMusicBackend : IMusicBackend
{
    public List<string> LoadedTracks { get; } = new();
    public List<int> PlayedIndices { get; } = new();
    public List<float> Volumes { get; } = new();
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool LoadSucceeds { get; set; } = true;
    public bool PlaySucceeds { get; set; } = true;

    public string Name => "Fake";
    public int TrackCount => LoadedTracks.Count;
    public bool IsPlaying { get; set; }

    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex)
    {
        if (!LoadSucceeds) return false;
        LoadedTracks.Add(trackName);
        return true;
    }

    public bool TryPlayTrack(int trackIndex, float volume)
    {
        if (!PlaySucceeds) return false;
        PlayedIndices.Add(trackIndex);
        Volumes.Add(volume);
        IsPlaying = true;
        return true;
    }

    public void Stop()
    {
        StopCount++;
        IsPlaying = false;
    }

    public void SetVolume(float volume) => Volumes.Add(volume);

    public void Dispose() => Disposed = true;
}
