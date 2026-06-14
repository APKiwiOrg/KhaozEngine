using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// macOS music backend: plays the built track file via an AVAudioPlayer bridge.
/// Tracks are located in the content directory by probing the formats the content pipeline emits:
/// the DesktopGL pipeline transcodes music to <c>.ogg</c> (the <c>.xnb</c> is only a header that
/// references it), so <c>.ogg</c> is preferred; raw <c>.mp3</c> and other AVAudioPlayer-decodable
/// formats are accepted as fallbacks.
/// </summary>
public sealed class MacOsMusicBackend : IMusicBackend
{
    // Built-track file extensions, in priority order. DesktopGL emits .ogg, so it is tried first;
    // AVAudioPlayer on macOS decodes all of these.
    private static readonly string[] AudioExtensions = [".ogg", ".mp3", ".m4a", ".wav", ".aiff", ".caf"];

    private readonly List<string> _trackPaths = [];
    private readonly ILogger _logger;
    private MacOsMusicPlayer? _player;

    /// <summary>Creates the backend. <paramref name="logger"/> defaults to the ambient <c>Log</c> facade.</summary>
    public MacOsMusicBackend(ILogger? logger = null)
    {
        _logger = logger ?? Log.For<MacOsMusicBackend>();
    }

    /// <inheritdoc/>
    public string Name => "macOS AVAudioPlayer";

    /// <inheritdoc/>
    public int TrackCount => _trackPaths.Count;

    /// <inheritdoc/>
    public bool IsPlaying => _player?.IsPlaying ?? false;

    /// <inheritdoc/>
    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName)
    {
        foreach (string extension in AudioExtensions)
        {
            string path = Path.Combine(contentDirectory, trackName + extension);
            if (File.Exists(path))
            {
                _logger.Info($"loading track {trackName} ({path})");
                _trackPaths.Add(path);
                return true;
            }
        }

        _logger.Warn($"track {trackName} not found in {contentDirectory} (tried: {string.Join(", ", AudioExtensions)})");
        return false;
    }

    /// <inheritdoc/>
    public bool TryPlayTrack(int trackIndex, float volume)
    {
        // The native player is created on first playback so track loading stays headless-safe
        // (its Objective-C bridge P/Invokes only resolve on macOS).
        _player ??= new MacOsMusicPlayer(_logger);
        return _player.Play(_trackPaths[trackIndex], volume);
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _player?.Stop();
    }

    /// <inheritdoc/>
    public void SetVolume(float volume)
    {
        _player?.SetVolume(volume);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _player?.Dispose();
    }
}
