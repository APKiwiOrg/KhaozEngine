using System;
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

    /// <summary>Track names whose load should fail, on top of the global <see cref="LoadSucceeds"/>.</summary>
    public HashSet<string> FailTracks { get; } = new();

    public string Name => "Fake";
    public int TrackCount => LoadedTracks.Count;
    private bool _isPlaying;

    /// <summary>When &gt; 0, the next read of <see cref="IsPlaying"/> throws and decrements this.</summary>
    public int ThrowOnNextIsPlayingReads { get; set; }

    public bool IsPlaying
    {
        get
        {
            if (ThrowOnNextIsPlayingReads > 0)
            {
                ThrowOnNextIsPlayingReads--;
                throw new InvalidOperationException("Transient IsPlaying read failure (test).");
            }
            return _isPlaying;
        }
        set => _isPlaying = value;
    }

    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName)
    {
        if (!LoadSucceeds || FailTracks.Contains(trackName)) return false;
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
