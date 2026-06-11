using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Media;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>Default backend: plays MonoGame <see cref="Song"/> assets via <see cref="MediaPlayer"/>.</summary>
public sealed class MonoGameMusicBackend : IMusicBackend
{
    private readonly List<Song> _tracks = [];
    private readonly ILogger _logger;

    /// <summary>Creates the backend. <paramref name="logger"/> defaults to the ambient <c>Log</c> facade.</summary>
    public MonoGameMusicBackend(ILogger? logger = null)
    {
        _logger = logger ?? Log.For<MonoGameMusicBackend>();
    }

    /// <inheritdoc/>
    public string Name => "MonoGame MediaPlayer";

    /// <inheritdoc/>
    public int TrackCount => _tracks.Count;

    /// <inheritdoc/>
    public bool IsPlaying
    {
        get
        {
            MediaState state = MediaPlayer.State;
            return state != MediaState.Stopped;
        }
    }

    /// <inheritdoc/>
    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex)
    {
        try
        {
            _logger.Info($"Audio: loading track {trackName}");
            _tracks.Add(content.Load<Song>(trackName));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Audio: track {trackName} failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryPlayTrack(int trackIndex, float volume)
    {
        MediaPlayer.IsRepeating = false;
        MediaPlayer.Play(_tracks[trackIndex]);
        MediaPlayer.Volume = volume;
        return true;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        MediaPlayer.Stop();
    }

    /// <inheritdoc/>
    public void SetVolume(float volume)
    {
        MediaPlayer.Volume = volume;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            MediaPlayer.Stop();
        }
        catch
        {
            // Best-effort shutdown.
        }

        for (int i = 0; i < _tracks.Count; i++)
        {
            try
            {
                _tracks[i].Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }
        }
    }
}
