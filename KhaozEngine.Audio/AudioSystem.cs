using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;

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
    private readonly HashSet<string> _warnedSfxLists = new(); // warn-once for an all-unloaded candidate list (keyed on the joined list)
    private List<string>? _rotationPoolNames;                // null = random rotation draws from ALL tracks
    private readonly HashSet<string> _warnedPoolNames = new(); // debug-once for pool names not registered
    private bool _warnedEmptyPool;                           // warn-once for an empty resolved pool fallback
    private DeterministicRng _rng = new(0);
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
    private float _musicCrossfadeDuration;   // seconds; 0 = hard cut (today's behavior)
    private MusicFade _fade;                  // pure dt-driven single-stream crossfade state

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

    /// <summary>Replaces the track-shuffle RNG with a seeded deterministic instance.</summary>
    public void SetRng(DeterministicRng rng) { _rng = rng; }

    /// <summary>
    /// Scopes which registered tracks <see cref="PlayRandomTrack"/> is allowed to pick from (the random
    /// boot first-play, <see cref="MusicEnabled"/> resume, and end-of-track auto-advance under
    /// <see cref="PlayMode.RandomRotation"/>). Lets a game register every track (so <see cref="PlayTrack(string)"/>
    /// can play any on demand) while keeping random selection on, say, a menu subset.
    /// </summary>
    /// <param name="trackNames">
    /// The names eligible for random rotation. <c>null</c> (the default / unset state) restores rotation over
    /// ALL registered tracks. Names not registered are ignored. Names resolve lazily, so this is safe to call
    /// before or after <see cref="LoadContent"/> and before or after the tracks are registered.
    /// </param>
    /// <remarks>
    /// <see cref="PlayTrack(string)"/> / <see cref="PlayTrack(int)"/> are unaffected - any registered track still
    /// plays on demand regardless of the pool. The "don't repeat the same track twice in a row" rule operates
    /// within the pool. A pool of size 1 plays that track every time. If the pool resolves to no registered
    /// tracks (e.g. names not yet loaded) rotation falls back to ALL tracks with a one-time warning, so a
    /// misconfigured pool never silences music.
    /// </remarks>
    public void SetRotationPool(IEnumerable<string>? trackNames)
    {
        _rotationPoolNames = trackNames is null ? null : new List<string>(trackNames);
        _warnedPoolNames.Clear();
        _warnedEmptyPool = false;
    }

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
                    _fade.Reset();   // cancel any in-flight crossfade so a stale factor can't scale a later apply
                    _backend.Stop();
                    ClearCurrentTrack();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("music backend failed toggling MusicEnabled; disabling audio.", ex);
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
    /// SFX volume (0.0 - 1.0). Scaled by master volume. Applied per <see cref="PlaySfx(string, float, float)"/> (no eager apply,
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
    /// Default crossfade duration in seconds applied when a track change happens (via <see cref="PlayTrack(string)"/>,
    /// <see cref="PlayTrack(int)"/>, <see cref="PlayRandomTrack"/>, or end-of-track auto-advance). Default <c>0</c>
    /// preserves the historical hard-cut behavior byte-for-byte. When &gt; 0 the old track fades out and the new one
    /// fades in over this duration (half fade-out, half fade-in) through the single music stream. The fade is driven
    /// by <see cref="Update(float)"/> and only progresses when the game passes a real <c>dt</c>; a game that never
    /// calls <see cref="Update(float)"/> (or leaves this at 0) sees no behavioral change. Negative values clamp to 0.
    /// </summary>
    public float MusicCrossfadeDuration
    {
        get => _musicCrossfadeDuration;
        set => _musicCrossfadeDuration = value < 0f ? 0f : value;
    }

    /// <summary>
    /// Crossfades to the registered track named <paramref name="name"/> over <paramref name="duration"/> seconds,
    /// independent of <see cref="MusicCrossfadeDuration"/>. Duration <c>0</c> is an immediate hard cut identical to
    /// <see cref="PlayTrack(string)"/>. An unknown name logs a warning and is a no-op (no throw). Retargeting while a
    /// fade is already running restarts the fade toward this newest track (see <see cref="Update(float)"/>).
    /// </summary>
    public void CrossfadeTo(string name, float duration)
    {
        int index = _trackNames.IndexOf(name);
        if (index < 0)
        {
            _logger.Warn($"CrossfadeTo unknown track '{name}'; ignoring.");
            return;
        }

        CrossfadeTo(index, duration);
    }

    /// <summary>
    /// Crossfades to the registered track at <paramref name="index"/> over <paramref name="duration"/> seconds,
    /// independent of <see cref="MusicCrossfadeDuration"/>. Duration <c>0</c> is an immediate hard cut identical to
    /// <see cref="PlayTrack(int)"/>. Out-of-range index logs a warning and is a no-op. Honours
    /// <see cref="MusicEnabled"/> and the availability latch.
    /// </summary>
    public void CrossfadeTo(int index, float duration)
        => RequestTrack(index, duration < 0f ? 0f : duration);

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

        int index = PickRandomIndex(trackCount);
        if (index < 0) return;
        RequestTrack(index, _musicCrossfadeDuration);
    }

    // Picks the next random rotation index (never the same one twice in a row within the pool), or -1 if the
    // pool is empty / cannot pick. Pure selection: no playback side effects.
    private int PickRandomIndex(int trackCount)
    {
        List<int> pool = ResolveRotationIndices(trackCount);
        if (pool.Count == 0) return -1;
        if (pool.Count == 1) return pool[0];

        int index;
        do
        {
            index = pool[_rng.Next(pool.Count)];
        } while (index == _lastTrackIndex);
        return index;
    }

    // Resolves the rotation pool to a list of distinct, in-range backend track indices that PlayRandomTrack
    // may select from. A null pool (default) -> every registered track (byte-for-byte the pre-pool behaviour).
    // A configured pool -> only its names that are registered; names not registered are skipped (debug-once).
    // If the pool resolves to nothing, falls back to ALL tracks (warn-once) so music is never silenced.
    private List<int> ResolveRotationIndices(int trackCount)
    {
        if (_rotationPoolNames is null)
        {
            return AllIndices(trackCount);
        }

        var indices = new List<int>(_rotationPoolNames.Count);
        foreach (string name in _rotationPoolNames)
        {
            int idx = _trackNames.IndexOf(name);
            if (idx >= 0 && idx < trackCount)
            {
                if (!indices.Contains(idx)) indices.Add(idx);
            }
            else if (_warnedPoolNames.Add(name))
            {
                _logger.Debug($"rotation pool track '{name}' is not registered; ignoring.");
            }
        }

        if (indices.Count == 0)
        {
            if (!_warnedEmptyPool)
            {
                _warnedEmptyPool = true;
                _logger.Warn("rotation pool resolved to no registered tracks; falling back to all tracks.");
            }
            return AllIndices(trackCount);
        }

        return indices;
    }

    private static List<int> AllIndices(int trackCount)
    {
        var all = new List<int>(trackCount);
        for (int i = 0; i < trackCount; i++) all.Add(i);
        return all;
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

        RequestTrack(index, _musicCrossfadeDuration);
    }

    // Central track-change entry: either an immediate hard cut (duration 0, byte-for-byte today's behavior) or
    // a dt-driven single-stream crossfade (duration > 0). Guards (track count / availability / enabled) match the
    // old play paths. Out-of-range indices are treated as no-ops here (callers validate their own ranges first).
    private void RequestTrack(int index, float duration)
    {
        int trackCount = _backend.TrackCount;
        if (trackCount == 0 || !_available || !_musicEnabled) return;
        if (index < 0 || index >= trackCount) return;

        if (duration <= 0f)
        {
            // Hard cut: identical to the pre-crossfade path. Cancel any in-flight fade so a stray fade factor
            // can't linger and scale the next volume apply.
            _fade.Reset();
            PlayTrackImmediate(index);
            return;
        }

        // Crossfade: start (or retarget) the fade toward this track. The actual backend switch fires from
        // Update(dt) when the fade-out half reaches 0. Nothing plays here if a track is already sounding.
        // Mark playback started so Update()'s deferred first-play can't fire a random track over the fade.
        _started = true;
        _fade.Start(index, duration, hasCurrentTrack: HasSoundingTrack());
    }

    // Plays a track through the backend right now at the current fade-composed volume, committing now-playing
    // state on success and latching audio off on failure. Shared by the hard-cut path and the fade's switch point.
    private void PlayTrackImmediate(int index)
    {
        try
        {
            if (!_backend.TryPlayTrack(index, CurrentMusicGain()))
            {
                _available = false;
                return;
            }

            CommitPlayed(index);
        }
        catch (Exception ex)
        {
            _logger.Debug("music backend failed playing track; disabling audio.", ex);
            _available = false;
        }
    }

    // The volume handed to the backend: settings-derived master*music scaled by the crossfade factor (1 when no
    // fade is active). The fade MULTIPLIES the user volume, never replaces it, so changing MusicVolume mid-fade
    // still takes effect on the next ApplyVolume / play.
    private float CurrentMusicGain() => _masterVolume * _musicVolume * _fade.Factor;

    // Whether a track is currently audible (so a crossfade should fade it OUT before switching). A transient
    // IsPlaying throw is treated as "not sounding" (fade-in only): safe, never propagates out of a play call.
    private bool HasSoundingTrack()
    {
        if (_currentTrack is null) return false;
        try { return _backend.IsPlaying; }
        catch (Exception ex)
        {
            _logger.Debug("failed to read IsPlaying while starting a crossfade; treating as no current track.", ex);
            return false;
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
    public void Update() => Update(0f);

    /// <summary>
    /// Call each frame with the elapsed seconds <paramref name="dt"/>. Same as the no-arg <see cref="Update()"/>
    /// (which passes <c>dt = 0</c>) plus it drives the music crossfade: a game using <see cref="MusicCrossfadeDuration"/>
    /// or <see cref="CrossfadeTo(string,float)"/> must call THIS overload with a real <paramref name="dt"/> so the fade
    /// progresses. With no active fade (or <c>dt = 0</c>) behavior is identical to the historical no-arg update.
    /// Detects end-of-track and queues the next; a transient <see cref="IMusicBackend.IsPlaying"/> read failure skips
    /// the frame (logged) and recovers next frame.
    /// </summary>
    public void Update(float dt)
    {
        if (_backend.TrackCount == 0 || !_available || !_musicEnabled) return;

        _backend.Update();   // pump the streaming backend (refill buffers, detect end-of-track)

        if (!_started)
        {
            _started = true;
            PlayRandomTrack();
            return;
        }

        // Advance any in-progress crossfade first. This may fire the mid-fade stream switch (fade-out reached 0)
        // and, either way, re-applies the live fade-scaled volume so the ramp is heard. While a fade is active the
        // end-of-track auto-advance below is suppressed (the source is mid-transition, not naturally finished).
        if (_fade.Active)
        {
            if (_fade.Advance(dt, out int switchTo))
            {
                PlayTrackImmediate(switchTo);   // fade-out hit 0: switch the single stream to the new track
            }
            ApplyVolume();                      // push the current fade factor into the backend gain
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
    /// guard behavior as <see cref="PlaySfx(string, float, float)"/>.
    /// </summary>
    public void PlaySfx3D(string name, Vector3 position, float volume = 1f, float pitch = 1f)
        => PlaySfxInternal(name, volume, pitch, positional: true, position);

    /// <summary>
    /// Whether <paramref name="name"/> resolves to a loaded SFX buffer (so a subsequent <see cref="PlaySfx(string,float,float)"/>
    /// will be heard). A name that was registered but whose file was missing / failed to load returns <c>false</c>.
    /// Lets a game pick among variant keys without triggering the unknown-name warn-once.
    /// </summary>
    public bool IsSfxLoaded(string name) => _sfx.ContainsKey(name);

    /// <summary>
    /// Plays the first loaded SFX in <paramref name="candidateKeys"/> (priority order) as a non-positional one-shot,
    /// returning <c>true</c> when one was played. The engine is convention-agnostic: the game builds the candidate
    /// list (e.g. a per-entity variant followed by a shared fallback). A <c>null</c> / empty list is a no-op
    /// returning <c>false</c>. If none of the candidates is loaded it warns once (deduped on the joined list) and
    /// returns <c>false</c>. Same gain / guard behavior as <see cref="PlaySfx(string,float,float)"/>.
    /// </summary>
    public bool PlaySfx(IReadOnlyList<string> candidateKeys, float volume = 1f, float pitch = 1f)
        => PlayFirstAvailable(candidateKeys, volume, pitch, positional: false, default);

    /// <summary>
    /// Plays the first loaded SFX in <paramref name="candidateKeys"/> (priority order) as a positional one-shot at
    /// <paramref name="position"/>, returning <c>true</c> when one was played. Same first-available / null-empty /
    /// warn-once semantics as <see cref="PlaySfx(IReadOnlyList{string},float,float)"/>; same positional behavior as
    /// <see cref="PlaySfx3D(string,Vector3,float,float)"/>.
    /// </summary>
    public bool PlaySfx3D(IReadOnlyList<string> candidateKeys, Vector3 position, float volume = 1f, float pitch = 1f)
        => PlayFirstAvailable(candidateKeys, volume, pitch, positional: true, position);

    /// <summary>
    /// Stops every currently-playing SFX voice immediately (music is unaffected). Useful on a scene / screen
    /// transition or pause so lingering one-shots do not bleed across the cut. A no-op when nothing is playing.
    /// </summary>
    public void StopAllSfx() => _sfxBackend.StopAll();

    // Plays the first candidate that resolves to a loaded buffer (reusing PlaySfxInternal's gain math + guard), or
    // warns once on the joined list and returns false if none load. Null / empty list is a silent no-op (false).
    private bool PlayFirstAvailable(IReadOnlyList<string> candidateKeys, float volume, float pitch, bool positional, Vector3 position)
    {
        if (candidateKeys is null || candidateKeys.Count == 0) return false;

        for (int i = 0; i < candidateKeys.Count; i++)
        {
            string name = candidateKeys[i];
            if (IsSfxLoaded(name))
            {
                PlaySfxInternal(name, volume, pitch, positional, position);
                return true;
            }
        }

        string joined = string.Join(",", candidateKeys);
        if (_warnedSfxLists.Add(joined)) _logger.Warn($"PlaySfx: none of [{joined}] is a loaded SFX; ignoring.");
        return false;
    }

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
    /// Sets the 3D listener pose used by <see cref="PlaySfx3D(string, System.Numerics.Vector3, float, float)"/> for attenuation / panning. No-op for
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
            _backend.SetVolume(CurrentMusicGain());
        }
        catch (Exception ex)
        {
            _logger.Debug("music backend failed applying volume; disabling audio.", ex);
            _available = false;
        }
    }
}
