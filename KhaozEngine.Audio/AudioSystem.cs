using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// Manages background music playback and volume settings.
/// Uses a platform-specific music backend to provide volume control,
/// enable/disable behavior, and automatic track rotation.
/// </summary>
public sealed class AudioSystem : IDisposable
{
    private readonly IMusicBackend _backend;
    private readonly ILogger _logger;
    private readonly List<string> _trackNames;
    private Random _rng = new();
    private float _masterVolume = 0.66f;
    private float _musicVolume = 0.4f;
    private int _lastTrackIndex = -1;
    private bool _available = true;
    private bool _loaded;
    private bool _started;
    private bool _musicEnabled = true;
    private string? _currentTrack;
    private ContentManager? _content;
    private string? _contentDirectory;

    /// <summary>
    /// Creates an audio system using the backend for the current OS
    /// (macOS AVAudioPlayer, otherwise MonoGame MediaPlayer).
    /// </summary>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IEnumerable<string>? trackNames = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<AudioSystem>();
        _backend = CreateBackend(_logger);
        _trackNames = trackNames is null ? new List<string>() : new List<string>(trackNames);
    }

    /// <summary>
    /// Creates an audio system with a caller-supplied backend (tests or custom platforms).
    /// </summary>
    /// <param name="backend">The music backend to drive.</param>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IMusicBackend backend, IEnumerable<string>? trackNames = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<AudioSystem>();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _trackNames = trackNames is null ? new List<string>() : new List<string>(trackNames);
    }

    /// <summary>
    /// Adds a track to the rotation. Idempotent: re-registering a known track is a no-op.
    /// Safe before or after <see cref="LoadContent"/> — a track registered after load is
    /// eager-loaded immediately via the backend (for DLC / runtime additions).
    /// </summary>
    public void RegisterTrack(string trackName)
    {
        if (_trackNames.Contains(trackName))
        {
            return;
        }

        if (_loaded && _content is not null)
        {
            // Post-load (DLC / runtime add): only commit the track if it actually loads, so a
            // missing file doesn't leave a phantom name that the dedup guard then blocks from reloading.
            if (_backend.TryLoadTrack(_content, _contentDirectory!, trackName))
            {
                _trackNames.Add(trackName);
            }
            return;
        }

        _trackNames.Add(trackName);   // pre-load: loaded later in LoadContent
    }

    /// <summary>Adds several tracks via <see cref="RegisterTrack"/> (idempotent, pre- or post-load).</summary>
    public void RegisterTracks(IEnumerable<string> trackNames)
    {
        foreach (string trackName in trackNames)
        {
            RegisterTrack(trackName);
        }
    }

    /// <summary>Replaces the track-shuffle RNG with a seeded instance.</summary>
    public void SetRng(Random rng) { _rng = rng; }

    /// <summary>Master volume (0.0 - 1.0). Scales all audio output.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = MathHelper.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>Whether background music is enabled. Toggling stops/starts playback without changing volume.</summary>
    public bool MusicEnabled
    {
        get => _musicEnabled;
        set
        {
            if (_musicEnabled == value) return;
            _musicEnabled = value;
            if (!_available || !_loaded) return;
            try
            {
                if (_musicEnabled)
                {
                    PlayRandomTrack();
                    // Toggling on initiated playback, so the deferred first-play in Update()
                    // must not start a second track.
                    _started = true;
                }
                else
                {
                    _backend.Stop();
                    ClearCurrentTrack();
                }
            }
            catch (Exception)
            {
                _available = false;
            }
        }
    }

    /// <summary>Music volume (0.0 - 1.0). Scaled by master volume.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = MathHelper.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>Name of the track currently playing, or null when nothing is playing.</summary>
    public string? CurrentTrack => _currentTrack;

    /// <summary>Raised when <see cref="CurrentTrack"/> changes (including to null on stop).</summary>
    public event Action<string?>? TrackChanged;

    /// <summary>How the next track is chosen when the current one ends. Default <see cref="PlayMode.RandomRotation"/>.</summary>
    public PlayMode PlayMode { get; set; } = PlayMode.RandomRotation;

    /// <summary>
    /// Loads all registered music tracks for the active platform backend.
    /// </summary>
    public void LoadContent(ContentManager content)
    {
        _content = content;
        _contentDirectory = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, content.RootDirectory);
        _logger.Info($"Audio: using {_backend.Name} backend");

        for (int i = 0; i < _trackNames.Count; i++)
        {
            _backend.TryLoadTrack(content, _contentDirectory, _trackNames[i]);
        }

        _logger.Info($"Audio: {_backend.TrackCount}/{_trackNames.Count} tracks loaded");
        _loaded = true;

        // Apply volume that was set during construction (before native audio was ready)
        ApplyVolume();
    }

    /// <summary>
    /// Plays a random track, avoiding the same track twice in a row.
    /// </summary>
    public void PlayRandomTrack()
    {
        int trackCount = _backend.TrackCount;
        if (trackCount == 0 || !_available || !_musicEnabled) return;

        try
        {
            int index;
            if (trackCount == 1)
            {
                index = 0;
            }
            else
            {
                do
                {
                    index = _rng.Next(trackCount);
                } while (index == _lastTrackIndex);
            }

            if (!_backend.TryPlayTrack(index, _masterVolume * _musicVolume))
            {
                _available = false;
                return;
            }

            CommitPlayed(index);   // record + now-playing state, only after a successful play
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    // Records a just-played track index and updates the now-playing state. Marks playback as started
    // so Update()'s deferred first-play does not fire a second track. Fires TrackChanged only when the
    // current track NAME actually changes (a RepeatOne replay of the same track does not re-fire).
    private void CommitPlayed(int index)
    {
        _lastTrackIndex = index;
        _started = true;
        string? name = (index >= 0 && index < _trackNames.Count) ? _trackNames[index] : null;
        if (_currentTrack != name)
        {
            _currentTrack = name;
            TrackChanged?.Invoke(name);
        }
    }

    private void ClearCurrentTrack()
    {
        if (_currentTrack is not null)
        {
            _currentTrack = null;
            TrackChanged?.Invoke(null);
        }
    }

    // Chooses the next track when the current one ends, per PlayMode.
    private void AdvanceTrack()
    {
        if (PlayMode == PlayMode.RepeatOne && _lastTrackIndex >= 0)
        {
            PlayTrack(_lastTrackIndex);
        }
        else
        {
            PlayRandomTrack();
        }
    }

    /// <summary>
    /// Plays the registered track at <paramref name="index"/> (index into the registration order).
    /// Out-of-range index logs a warning and is a no-op. Honours <see cref="MusicEnabled"/> and the
    /// availability latch, like <see cref="PlayRandomTrack"/>.
    /// </summary>
    public void PlayTrack(int index)
    {
        int trackCount = _backend.TrackCount;
        if (trackCount == 0 || !_available || !_musicEnabled) return;

        if (index < 0 || index >= trackCount)
        {
            _logger.Warn($"Audio: PlayTrack index {index} out of range (0..{trackCount - 1}); ignoring.");
            return;
        }

        try
        {
            if (!_backend.TryPlayTrack(index, _masterVolume * _musicVolume))
            {
                _available = false;
                return;
            }

            CommitPlayed(index);
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    /// <summary>
    /// Plays the registered track named <paramref name="name"/>. An unknown name logs a warning and
    /// is a no-op (no throw).
    /// </summary>
    public void PlayTrack(string name)
    {
        int index = _trackNames.IndexOf(name);
        if (index < 0)
        {
            _logger.Warn($"Audio: PlayTrack unknown track '{name}'; ignoring.");
            return;
        }

        PlayTrack(index);
    }

    /// <summary>
    /// Call each frame to detect when the current track ends and queue the next.
    /// Defers first playback to the first Update call so the audio subsystem is ready.
    /// </summary>
    public void Update()
    {
        if (_backend.TrackCount == 0 || !_available || !_musicEnabled) return;

        if (!_started)
        {
            _started = true;
            PlayRandomTrack();
            return;
        }

        try
        {
            if (!_backend.IsPlaying)
            {
                AdvanceTrack();
            }
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _backend.Dispose();
    }

    private static IMusicBackend CreateBackend(ILogger logger)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsMusicBackend(logger);
        }

        return new MonoGameMusicBackend(logger);
    }

    private void ApplyVolume()
    {
        if (!_available || !_loaded) return;
        try
        {
            _backend.SetVolume(_masterVolume * _musicVolume);
        }
        catch (Exception)
        {
            _available = false;
        }
    }
}
