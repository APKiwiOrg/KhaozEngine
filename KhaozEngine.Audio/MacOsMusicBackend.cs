using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>macOS music backend: plays raw <c>.mp3</c> files via an AVAudioPlayer bridge.</summary>
public sealed class MacOsMusicBackend : IMusicBackend
{
    private readonly List<string> _trackPaths = [];
    private readonly ILogger _logger;
    private readonly MacOsMusicPlayer _player;

    /// <summary>Creates the backend. <paramref name="logger"/> defaults to the ambient <c>Log</c> facade.</summary>
    public MacOsMusicBackend(ILogger? logger = null)
    {
        _logger = logger ?? Log.For<MacOsMusicBackend>();
        _player = new MacOsMusicPlayer(_logger);
    }

    /// <inheritdoc/>
    public string Name => "macOS AVAudioPlayer";

    /// <inheritdoc/>
    public int TrackCount => _trackPaths.Count;

    /// <inheritdoc/>
    public bool IsPlaying => _player.IsPlaying;

    /// <inheritdoc/>
    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex)
    {
        string mp3Path = Path.Combine(contentDirectory, trackName + ".mp3");
        _logger.Info($"Audio: loading track {trackIndex}: {mp3Path}");

        if (!File.Exists(mp3Path))
        {
            _logger.Warn($"Audio: track {trackIndex} not found at {mp3Path}");
            return false;
        }

        _trackPaths.Add(mp3Path);
        return true;
    }

    /// <inheritdoc/>
    public bool TryPlayTrack(int trackIndex, float volume)
    {
        return _player.Play(_trackPaths[trackIndex], volume);
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _player.Stop();
    }

    /// <inheritdoc/>
    public void SetVolume(float volume)
    {
        _player.SetVolume(volume);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _player.Dispose();
    }
}
