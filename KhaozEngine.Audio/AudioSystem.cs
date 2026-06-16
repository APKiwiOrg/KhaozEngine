using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// Manages background music playback and volume settings.
/// Uses a platform-specific music backend to provide volume control,
/// enable/disable behavior, and automatic track rotation.
/// </summary>
public sealed class AudioSystem : IDisposable
{
    private static readonly string[] SfxExtensions = { ".wav", ".ogg", ".mp3" };

    private readonly IMusicBackend _backend;
    private readonly ISfxBackend _sfxBackend;
    private readonly OpenAlContext? _context;   // shared OpenAL context (owned here); null when silent
    private readonly ILogger _logger;
    private readonly List<string> _trackNames;
    private readonly List<string> _sfxNames = new();
    private readonly Dictionary<string, int> _sfx = new();   // name -> backend handle
    private readonly HashSet<string> _warnedSfx = new();     // warn-once for unknown SFX
    private Random _rng = new();
    private float _masterVolume = 0.66f;
    private float _musicVolume = 0.4f;
    private float _sfxVolume = 0.7f;
    private int _lastTrackIndex = -1;
    private bool _available = true;
    private bool _loaded;
    private bool _started;
    private bool _musicEnabled = true;
    private string? _currentTrack;
    private string? _contentDirectory;

    /// <summary>
    /// Creates an audio system using the OpenAL streaming backend.
    /// </summary>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IEnumerable<string>? trackNames = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<AudioSystem>();
        // Build ONE shared OpenAL context for both music and SFX (OpenAL has one context per process). On
        // failure (no device) fall back to silent Null backends with no context, preserving today's behavior.
        try
        {
            _context = new OpenAlContext();
            _backend = new OpenAlMusicBackend(_context, _logger);
            _sfxBackend = new OpenAlSfxBackend(_context, _logger);
        }
        catch (Exception ex)
        {
            // No OpenAL implementation / audio device (headless CI, server, no sound card): stay silent
            // rather than crash. A real device on the player's machine still gets the OpenAL backends.
            _logger.Warn("audio unavailable; using silent backends.", ex);
            _context?.Dispose();
            _context = null;
            _backend = new NullMusicBackend();
            _sfxBackend = new NullSfxBackend();
        }
        _trackNames = trackNames is null ? new List<string>() : new List<string>(trackNames);
    }

    /// <summary>
    /// Creates an audio system with a caller-supplied music backend (tests or custom platforms). SFX use a
    /// silent <see cref="NullSfxBackend"/> (no shared context) so existing music-only construction is
    /// unaffected.
    /// </summary>
    /// <param name="backend">The music backend to drive.</param>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IMusicBackend backend, IEnumerable<string>? trackNames = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<AudioSystem>();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _sfxBackend = new NullSfxBackend();
        _trackNames = trackNames is null ? new List<string>() : new List<string>(trackNames);
    }

    /// <summary>
    /// Creates an audio system with caller-supplied music AND SFX backends (tests or custom platforms). No
    /// shared OpenAL context is created or owned.
    /// </summary>
    /// <param name="music">The music backend to drive.</param>
    /// <param name="sfx">The SFX backend to drive.</param>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IMusicBackend music, ISfxBackend sfx, IEnumerable<string>? trackNames = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<AudioSystem>();
        _backend = music ?? throw new ArgumentNullException(nameof(music));
        _sfxBackend = sfx ?? throw new ArgumentNullException(nameof(sfx));
        _trackNames = trackNames is null ? new List<string>() : new List<string>(trackNames);
    }

    /// <summary>
    /// Adds a track to the rotation. Idempotent: re-registering a known track is a no-op.
    /// Safe before or after <see cref="LoadContent"/> - a track registered after load is
    /// eager-loaded immediately via the backend (for DLC / runtime additions).
    /// </summary>
    public void RegisterTrack(string trackName)
    {
        if (_trackNames.Contains(trackName))
        {
            return;
        }

        if (_loaded && _contentDirectory is not null)
        {
            // Post-load (DLC / runtime add): only commit the track if it actually loads, so a
            // missing file doesn't leave a phantom name that the dedup guard then blocks from reloading.
            if (_backend.TryLoadTrack(_contentDirectory!, trackName))
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
            _masterVolume = Math.Clamp(value, 0f, 1f);
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
            _musicVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>
    /// SFX volume (0.0 - 1.0). Scaled by master volume. Applied per <see cref="PlaySfx"/> (no eager apply,
    /// since SFX are fire-and-forget one-shots).
    /// </summary>
    public float SfxVolume
    {
        get => _sfxVolume;
        set => _sfxVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Name of the track currently playing, or null when nothing is playing.</summary>
    public string? CurrentTrack => _currentTrack;

    /// <summary>Raised when <see cref="CurrentTrack"/> changes (including to null on stop).</summary>
    public event Action<string?>? TrackChanged;

    /// <summary>How the next track is chosen when the current one ends. Default <see cref="PlayMode.RandomRotation"/>.</summary>
    public PlayMode PlayMode { get; set; } = PlayMode.RandomRotation;

    /// <summary>
    /// Loads all registered music tracks from <paramref name="contentDirectory"/> (the folder holding the
    /// WAV/OGG/MP3 files). Names are the file names without extension.
    /// </summary>
    public void LoadContent(string contentDirectory)
    {
        _contentDirectory = contentDirectory;
        _logger.Info($"using {_backend.Name} backend");

        // Keep _trackNames aligned with the backend's compact track list: drop any that fail to load,
        // so name lookups (CurrentTrack, PlayTrack(name)) resolve to the right track even after a
        // partial-load failure.
        int requested = _trackNames.Count;
        int kept = 0;
        for (int i = 0; i < _trackNames.Count; i++)
        {
            if (_backend.TryLoadTrack(_contentDirectory, _trackNames[i]))
            {
                _trackNames[kept++] = _trackNames[i];
            }
        }
        _trackNames.RemoveRange(kept, _trackNames.Count - kept);

        _logger.Info($"{_backend.TrackCount}/{requested} tracks loaded");

        // Load all registered SFX from the same content dir (name + .wav/.ogg/.mp3 -> handle).
        foreach (string name in _sfxNames) LoadSfx(name);

        _loaded = true;

        // Apply volume that was set during construction (before native audio was ready)
        ApplyVolume();
    }

    /// <summary>
    /// Registers a one-shot SFX by content name (no extension). If content is already loaded, it is
    /// eager-loaded now (mirrors <see cref="RegisterTrack"/>); otherwise it loads in <see cref="LoadContent"/>.
    /// Idempotent.
    /// </summary>
    public void RegisterSfx(string name)
    {
        if (_sfxNames.Contains(name)) return;
        _sfxNames.Add(name);
        if (_loaded && _contentDirectory is not null) LoadSfx(name);
    }

    /// <summary>Registers several SFX via <see cref="RegisterSfx"/> (idempotent, pre- or post-load).</summary>
    public void RegisterSfxes(IEnumerable<string> names)
    {
        foreach (string name in names) RegisterSfx(name);
    }

    // Looks for name + .wav/.ogg/.mp3 in the content dir and maps name -> backend handle. Skips + warns on
    // a missing file or a backend load failure (-1).
    private void LoadSfx(string name)
    {
        if (_contentDirectory is null || _sfx.ContainsKey(name)) return;

        foreach (string ext in SfxExtensions)
        {
            string path = Path.Combine(_contentDirectory, name + ext);
            if (!File.Exists(path)) continue;

            int handle = _sfxBackend.Load(path);
            if (handle >= 0) { _sfx[name] = handle; }
            else _logger.Warn($"SFX '{name}' failed to load from '{path}'.");
            return;
        }
        _logger.Warn($"SFX: no WAV/OGG/MP3 file found for '{name}' in '{_contentDirectory}'.");
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
            _logger.Warn($"PlayTrack index {index} out of range (0..{trackCount - 1}); ignoring.");
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
            _logger.Warn($"PlayTrack unknown track '{name}'; ignoring.");
            return;
        }

        PlayTrack(index);
    }

    /// <summary>
    /// Call each frame to detect when the current track ends and queue the next.
    /// Defers first playback to the first Update call so the audio subsystem is ready.
    /// A transient failure reading <see cref="IMusicBackend.IsPlaying"/> skips the frame (logged) and
    /// recovers next frame; only real play/load failures permanently disable audio.
    /// </summary>
    public void Update()
    {
        if (_backend.TrackCount == 0 || !_available || !_musicEnabled) return;

        _backend.Update();   // pump the streaming backend (refill buffers, detect end-of-track)

        if (!_started)
        {
            _started = true;
            PlayRandomTrack();
            return;
        }

        bool isPlaying;
        try
        {
            isPlaying = _backend.IsPlaying;
        }
        catch (Exception ex)
        {
            _logger.Warn("failed to read IsPlaying; skipping frame.", ex);
            return;
        }

        if (!isPlaying)
        {
            AdvanceTrack();
        }
    }

    /// <summary>
    /// Plays a registered SFX as a non-positional one-shot (heard at full gain). Gain =
    /// <see cref="MasterVolume"/> * <see cref="SfxVolume"/> * clamp01(<paramref name="volume"/>). An unknown
    /// name warns once and is a no-op. An SFX hiccup is logged and swallowed (never disables music).
    /// </summary>
    public void PlaySfx(string name, float volume = 1f, float pitch = 1f)
        => PlaySfxInternal(name, volume, pitch, positional: false, default);

    /// <summary>
    /// Plays a registered SFX as a positional one-shot at <paramref name="position"/> in world space
    /// (attenuates / pans relative to the listener; see <see cref="SetListener"/>). Same gain / unknown-name /
    /// guard behavior as <see cref="PlaySfx"/>.
    /// </summary>
    public void PlaySfx3D(string name, Vector3 position, float volume = 1f, float pitch = 1f)
        => PlaySfxInternal(name, volume, pitch, positional: true, position);

    private void PlaySfxInternal(string name, float volume, float pitch, bool positional, Vector3 position)
    {
        if (!_sfx.TryGetValue(name, out int handle))
        {
            if (_warnedSfx.Add(name)) _logger.Warn($"PlaySfx unknown SFX '{name}'; ignoring.");
            return;
        }

        float gain = _masterVolume * _sfxVolume * Math.Clamp(volume, 0f, 1f);
        try
        {
            _sfxBackend.Play(handle, gain, pitch, positional, position);
        }
        catch (Exception ex)
        {
            // An SFX hiccup must never disable music: log and carry on (do not flip the availability latch).
            _logger.Warn($"PlaySfx '{name}' failed.", ex);
        }
    }

    /// <summary>
    /// Sets the 3D listener pose used by <see cref="PlaySfx3D"/> for attenuation / panning. No-op for
    /// non-positional SFX.
    /// </summary>
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        try
        {
            _sfxBackend.SetListener(position, forward, up);
        }
        catch (Exception ex)
        {
            _logger.Warn("SetListener failed.", ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Dispose order: SFX backend, music backend, then the shared context LAST (the backends borrow it).
        _sfxBackend.Dispose();
        _backend.Dispose();
        _context?.Dispose();
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
