using System;
using Microsoft.Xna.Framework.Content;

namespace KhaozEngine.Audio;

/// <summary>
/// A platform music backend: loads named tracks and plays one at a time with volume control.
/// Implemented by the bundled MonoGame and macOS backends; games or tests may supply their own.
/// </summary>
public interface IMusicBackend : IDisposable
{
    /// <summary>Human-readable backend name (used in logs).</summary>
    string Name { get; }

    /// <summary>Number of tracks successfully loaded.</summary>
    int TrackCount { get; }

    /// <summary>True while a track is currently playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Attempts to load one track. Returns false if it could not be loaded.</summary>
    bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex);

    /// <summary>Attempts to start the track at <paramref name="trackIndex"/> at the given volume.</summary>
    bool TryPlayTrack(int trackIndex, float volume);

    /// <summary>Stops playback.</summary>
    void Stop();

    /// <summary>Sets output volume (0.0 - 1.0).</summary>
    void SetVolume(float volume);
}
