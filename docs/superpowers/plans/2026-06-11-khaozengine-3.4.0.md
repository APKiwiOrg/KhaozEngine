# KhaozEngine 3.4.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship KhaozEngine 3.4.0 - a `SettingsManager<T>` sanitize-on-load hook and `AudioSystem` explicit/repeat playback + now-playing, plus four review nits, then run the release ritual up to (not including) the tag/push.

**Architecture:** All changes are additive and backward-compatible. Two gating features (Persistence settings hook, Audio playback control) unblock SpaceGame/Nullwake adoption; four nits harden existing code and docs. One version bump (3.3.0 -> 3.4.0) at the very end. TDD throughout: every behaviour ships with a headless xUnit test driven through `FakeMusicBackend` / in-memory storage. No `Mouse/Keyboard/GamePad/TouchPanel` statics.

**Tech Stack:** net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit. Build/test: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`.

Work happens in worktree `worktree-ke-3.4.0` (already created). Run all commands from the repo root of that worktree.

---

## File Structure

Created:
- `KhaozEngine.Audio/PlayMode.cs` - the `PlayMode` enum.
- `docs/superpowers/specs/2026-06-11-khaozengine-3.4.0-design.md` - already written/committed.

Modified:
- `KhaozEngine.Persistence/SettingsManager.cs` - add `sanitizeOnLoad` ctor param + apply in `Load()`.
- `KhaozEngine.Persistence/README.md` - document the `[JsonExtensionData]` downgrade-safe migration pattern.
- `KhaozEngine.Audio/AudioSystem.cs` - `PlayTrack`, `CurrentTrack`, `TrackChanged`, `PlayMode` prop, `AdvanceTrack`/`CommitPlayed` helpers, latch-scoped `Update`.
- `KhaozEngine.Ecs/DeterministicRng.cs` - guard the two `Next` overloads.
- `KhaozEngine.Tests/Audio/FakeMusicBackend.cs` - add `ThrowOnNextIsPlayingReads` to simulate a transient read error.
- `KhaozEngine.Tests/Audio/AudioSystemTests.cs` - new audio tests.
- `KhaozEngine.Tests/DeterministicRngTests.cs` - new guard tests.
- `KhaozEngine.Tests/ParticleSystemTests.cs` - strengthen `Emit_beyond_capacity_recycles_oldest_slots`.
- `KhaozEngine.Persistence/SettingsManagerTests.cs` (or existing settings test file - confirm at execution) - new hook tests.
- `docs/USING-KHAOZENGINE.md` - add Graphics/Camera2D section.
- `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md` - release bump.

---

## Task 1: SettingsManager<T> sanitize-on-load hook

**Files:**
- Modify: `KhaozEngine.Persistence/SettingsManager.cs`
- Test: `KhaozEngine.Tests/` settings test file (confirm exact name; e.g. `SettingsManagerTests.cs`). Look for `new SettingsManager<` in `KhaozEngine.Tests` to find it; if none exists, create `KhaozEngine.Tests/Persistence/SettingsManagerHookTests.cs` with `namespace KhaozEngine.Tests;`.

- [ ] **Step 1: Write the failing tests**

Add to the settings test file (these need a tiny in-memory storage + DTO; reuse the file's existing fake storage if present, otherwise add the helpers shown). DTO and fake:

```csharp
private sealed class Box { public int Value { get; set; } }

private sealed class InMemoryStorage : ISettingsStorage
{
    public object? Stored;
    public T? LoadSettings<T>() where T : new() => Stored is T t ? t : default;
    public void SaveSettings<T>(T settings) where T : new() => Stored = settings;
}
```

> Note: match the real `ISettingsStorage` signatures - confirm them in `KhaozEngine.Persistence/ISettingsStorage.cs` before writing the fake (the generic constraints/return shape must line up). If the test file already has a fake storage, use it instead of adding `InMemoryStorage`.

Tests:

```csharp
[Fact]
public void SanitizeOnLoad_RunsOnInitialCtorLoad()
{
    var storage = new InMemoryStorage { Stored = new Box { Value = 999 } };
    var mgr = new SettingsManager<Box>(storage, logger: null, sanitizeOnLoad: b => { b.Value = Math.Min(b.Value, 100); return b; });
    Assert.Equal(100, mgr.Settings.Value);   // clamped on the FIRST load, before any caller could subscribe
}

[Fact]
public void SanitizeOnLoad_RunsOnReload()
{
    var storage = new InMemoryStorage { Stored = new Box { Value = 5 } };
    var mgr = new SettingsManager<Box>(storage, logger: null, sanitizeOnLoad: b => { b.Value += 1; return b; });
    Assert.Equal(6, mgr.Settings.Value);
    storage.Stored = new Box { Value = 50 };
    mgr.Load();
    Assert.Equal(51, mgr.Settings.Value);    // hook ran again on reload
}

[Fact]
public void SanitizeOnLoad_Null_IsPassthrough()
{
    var storage = new InMemoryStorage { Stored = new Box { Value = 7 } };
    var mgr = new SettingsManager<Box>(storage);   // no hook
    Assert.Equal(7, mgr.Settings.Value);
}

[Fact]
public void SanitizeOnLoad_ClampedValueIsWhatSettingsExposes()
{
    var storage = new InMemoryStorage { Stored = new Box { Value = -40 } };
    var mgr = new SettingsManager<Box>(storage, logger: null, sanitizeOnLoad: b => { b.Value = Math.Max(b.Value, 0); return b; });
    Assert.Equal(0, mgr.Settings.Value);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SanitizeOnLoad"`
Expected: FAIL to compile - `SettingsManager` ctor has no `sanitizeOnLoad` parameter.

- [ ] **Step 3: Implement the hook**

In `KhaozEngine.Persistence/SettingsManager.cs`:

Add a field after `private readonly ILogger? logger;`:

```csharp
    private readonly Func<T, T>? sanitizeOnLoad;
```

Change the constructor signature and body:

```csharp
    /// <summary>Creates a manager over <paramref name="storage"/> and immediately loads.</summary>
    /// <param name="storage">Backing storage.</param>
    /// <param name="logger">Optional logger for swallowed load/save failures.</param>
    /// <param name="sanitizeOnLoad">
    /// Optional hook applied to the deserialized value after EVERY load, including the initial load
    /// in this constructor (which fires before any caller can subscribe to <see cref="SettingsLoaded"/>).
    /// Clamp fields, migrate a schema-version field, etc.; return the sanitized object, which becomes
    /// <see cref="Settings"/>. A hook that throws is swallowed/logged and the unsanitized value is used.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null, Func<T, T>? sanitizeOnLoad = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.logger = logger;
        this.sanitizeOnLoad = sanitizeOnLoad;
        Load();
    }
```

Change `Load()`:

```csharp
    /// <summary>
    /// Loads settings, falling back to defaults on failure, then applies the optional sanitize hook.
    /// Always raises <see cref="SettingsLoaded"/>.
    /// </summary>
    public void Load()
    {
        T loaded;
        try
        {
            loaded = storage.LoadSettings<T>() ?? new T();
        }
        catch (Exception ex)
        {
            logger?.Error("Failed to load settings; using defaults.", ex);
            loaded = new T();
        }

        if (sanitizeOnLoad is not null)
        {
            try
            {
                loaded = sanitizeOnLoad(loaded) ?? loaded;
            }
            catch (Exception ex)
            {
                logger?.Error("sanitizeOnLoad threw; using unsanitized value.", ex);
            }
        }

        settings = loaded;
        SettingsLoaded?.Invoke(settings);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SanitizeOnLoad"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/SettingsManager.cs KhaozEngine.Tests
git commit -m "feat(Persistence): SettingsManager<T> sanitizeOnLoad hook (runs on initial ctor load)"
```

---

## Task 2: Persistence README - downgrade-safe migration pattern

**Files:**
- Modify: `KhaozEngine.Persistence/README.md` (append after the existing `## Settings` section)

- [ ] **Step 1: Add the doc section**

Append to `KhaozEngine.Persistence/README.md`:

```markdown
### Schema migration & downgrade safety

`SettingsManager<T>` takes an optional `sanitizeOnLoad` hook that runs on **every** load, including
the initial load in the constructor (the `SettingsLoaded` event can't help there - it fires inside
the ctor before a caller can subscribe). Use it to clamp fields and migrate an embedded schema version:

```csharp
var mgr = new SettingsManager<SaveData>(
    storage,
    Log.For<SettingsManager<SaveData>>(),
    sanitizeOnLoad: Migrate);

static SaveData Migrate(SaveData s)
{
    if (s.Version < 2) { /* fill new fields, rename, etc. */ s.Version = 2; }
    s.MasterVolume = Math.Clamp(s.MasterVolume, 0f, 1f);
    return s;
}
```

For forward-compatibility (a newer build wrote fields this older build doesn't know), give the DTO a
`[JsonExtensionData]` bag. `FileSettingsStorage` round-trips the live object through
`System.Text.Json`, so unknown fields survive a load + save and are not dropped on downgrade - no
engine code required, just the DTO shape:

```csharp
public sealed class SaveData
{
    public int Version { get; set; } = 2;
    public float MasterVolume { get; set; } = 1f;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
```
```

- [ ] **Step 2: Commit**

```bash
git add KhaozEngine.Persistence/README.md
git commit -m "docs(Persistence): document JsonExtensionData + sanitizeOnLoad migration pattern"
```

---

## Task 3: AudioSystem - PlayMode enum + extend FakeMusicBackend

**Files:**
- Create: `KhaozEngine.Audio/PlayMode.cs`
- Modify: `KhaozEngine.Tests/Audio/FakeMusicBackend.cs`

This task adds the supporting types only (no behaviour yet), so later audio tasks have them.

- [ ] **Step 1: Create the PlayMode enum**

`KhaozEngine.Audio/PlayMode.cs`:

```csharp
namespace KhaozEngine.Audio;

/// <summary>How <see cref="AudioSystem"/> chooses the next track when the current one ends.</summary>
public enum PlayMode
{
    /// <summary>Pick a random track, never the same one twice in a row (the default).</summary>
    RandomRotation,

    /// <summary>Replay the current track when it ends (set a specific track via <see cref="AudioSystem.PlayTrack(string)"/>).</summary>
    RepeatOne,
}
```

- [ ] **Step 2: Extend FakeMusicBackend to simulate a transient IsPlaying read error**

In `KhaozEngine.Tests/Audio/FakeMusicBackend.cs`, replace the auto-property:

```csharp
    public bool IsPlaying { get; set; }
```

with a backed property plus a throw counter:

```csharp
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
```

Add `using System;` at the top of the file if not already present.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: build succeeds (existing tests still set/read `IsPlaying` as before; no behaviour change yet).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Audio/PlayMode.cs KhaozEngine.Tests/Audio/FakeMusicBackend.cs
git commit -m "feat(Audio): add PlayMode enum; test backend can simulate transient IsPlaying error"
```

---

## Task 4: AudioSystem - PlayTrack + CurrentTrack + TrackChanged

**Files:**
- Modify: `KhaozEngine.Audio/AudioSystem.cs`
- Test: `KhaozEngine.Tests/Audio/AudioSystemTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `AudioSystemTests.cs` (the file already has `using System;`; add `using System.Collections.Generic;` if not present):

```csharp
[Fact]
public void PlayTrackByName_PlaysItAndSetsCurrentTrack()
{
    var (audio, backend) = NewLoaded("a", "b", "c");
    string? changed = "unset";
    audio.TrackChanged += name => changed = name;

    audio.PlayTrack("b");

    Assert.Equal(1, backend.PlayedIndices[^1]);   // "b" is index 1
    Assert.Equal("b", audio.CurrentTrack);
    Assert.Equal("b", changed);
}

[Fact]
public void PlayTrackByIndex_PlaysIt()
{
    var (audio, backend) = NewLoaded("a", "b", "c");
    audio.PlayTrack(2);
    Assert.Equal(2, backend.PlayedIndices[^1]);
    Assert.Equal("c", audio.CurrentTrack);
}

[Fact]
public void PlayTrackByName_Unknown_IsLoggedNoOp()
{
    var (audio, backend) = NewLoaded("a", "b");
    audio.PlayTrack("nope");
    Assert.Empty(backend.PlayedIndices);
    Assert.Null(audio.CurrentTrack);
}

[Fact]
public void PlayTrackByIndex_OutOfRange_IsNoOp()
{
    var (audio, backend) = NewLoaded("a", "b");
    audio.PlayTrack(5);
    audio.PlayTrack(-1);
    Assert.Empty(backend.PlayedIndices);
    Assert.Null(audio.CurrentTrack);
}

[Fact]
public void CurrentTrackIsNullBeforeAnyPlay()
{
    var (audio, _) = NewLoaded("a", "b");
    Assert.Null(audio.CurrentTrack);
}

[Fact]
public void DisablingMusicClearsCurrentTrack()
{
    var (audio, _) = NewLoaded("a", "b");
    string? last = "unset";
    audio.PlayTrack("a");
    audio.TrackChanged += n => last = n;
    audio.MusicEnabled = false;
    Assert.Null(audio.CurrentTrack);
    Assert.Null(last);                 // TrackChanged(null) fired on stop
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PlayTrack|FullyQualifiedName~CurrentTrack"`
Expected: FAIL to compile - no `PlayTrack`, `CurrentTrack`, or `TrackChanged` members.

- [ ] **Step 3: Implement explicit playback + now-playing**

In `KhaozEngine.Audio/AudioSystem.cs`:

Add fields after `private bool _musicEnabled = true;`:

```csharp
    private string? _currentTrack;
```

Add public members near the other properties (e.g. after the `MusicVolume` property):

```csharp
    /// <summary>Name of the track currently playing, or null when nothing is playing.</summary>
    public string? CurrentTrack => _currentTrack;

    /// <summary>Raised when <see cref="CurrentTrack"/> changes (including to null on stop).</summary>
    public event Action<string?>? TrackChanged;
```

Add a private helper that every successful play funnels through (place it near `PlayRandomTrack`):

```csharp
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
```

In `PlayRandomTrack`, replace the line `_lastTrackIndex = index;   // record only after a successful play` with:

```csharp
            CommitPlayed(index);   // record + now-playing state, only after a successful play
```

Add the two `PlayTrack` overloads after `PlayRandomTrack`:

```csharp
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
```

In the `MusicEnabled` setter, in the `else` branch, call `ClearCurrentTrack()` after `_backend.Stop();`:

```csharp
                else
                {
                    _backend.Stop();
                    ClearCurrentTrack();
                }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PlayTrack|FullyQualifiedName~CurrentTrack"`
Expected: PASS.

- [ ] **Step 5: Run the whole audio suite to confirm no regression**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AudioSystemTests"`
Expected: PASS (existing + new).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Audio/AudioSystem.cs KhaozEngine.Tests/Audio/AudioSystemTests.cs
git commit -m "feat(Audio): PlayTrack(index/name) + CurrentTrack + TrackChanged now-playing"
```

---

## Task 5: AudioSystem - PlayMode property + RepeatOne auto-advance

**Files:**
- Modify: `KhaozEngine.Audio/AudioSystem.cs`
- Test: `KhaozEngine.Tests/Audio/AudioSystemTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `AudioSystemTests.cs`:

```csharp
[Fact]
public void DefaultPlayModeIsRandomRotation()
{
    var (audio, _) = NewLoaded("a");
    Assert.Equal(PlayMode.RandomRotation, audio.PlayMode);
}

[Fact]
public void RepeatOne_ReplaysSameTrackOnAutoAdvance()
{
    var (audio, backend) = NewLoaded("a", "b", "c");
    audio.PlayTrack("b");                 // index 1; sets _lastTrackIndex = 1, _started = true
    audio.PlayMode = PlayMode.RepeatOne;

    backend.IsPlaying = false;
    audio.Update();                       // auto-advance under RepeatOne

    // Random rotation would AVOID index 1 (last played); RepeatOne replays exactly index 1.
    Assert.Equal(1, backend.PlayedIndices[^1]);
    Assert.Equal("b", audio.CurrentTrack);
}

[Fact]
public void TrackChanged_FiresOnChange_NotOnSameNameRepeat()
{
    var (audio, backend) = NewLoaded("a", "b", "c");
    var names = new List<string?>();
    audio.TrackChanged += n => names.Add(n);

    audio.PlayTrack("a");                 // change -> "a"
    audio.PlayTrack("b");                 // change -> "b"
    audio.PlayMode = PlayMode.RepeatOne;
    backend.IsPlaying = false;
    audio.Update();                       // RepeatOne replay of "b" -> NO new event

    Assert.Equal(new string?[] { "a", "b" }, names);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RepeatOne|FullyQualifiedName~PlayMode|FullyQualifiedName~TrackChanged"`
Expected: FAIL to compile - no `PlayMode` property.

- [ ] **Step 3: Implement PlayMode + AdvanceTrack**

In `KhaozEngine.Audio/AudioSystem.cs`:

Add the property near the other public properties:

```csharp
    /// <summary>How the next track is chosen when the current one ends. Default <see cref="PlayMode.RandomRotation"/>.</summary>
    public PlayMode PlayMode { get; set; } = PlayMode.RandomRotation;
```

Add the auto-advance helper next to `CommitPlayed`:

```csharp
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
```

In `Update()`, change the auto-advance call from `PlayRandomTrack();` to `AdvanceTrack();` inside the `if (!_backend.IsPlaying)` branch. (The exact `Update()` body is rewritten in Task 6; if Task 6 runs first, apply there instead. Whichever runs second only needs the single `PlayRandomTrack()` -> `AdvanceTrack()` substitution in the post-`_started` advance path. The deferred first-play `PlayRandomTrack()` stays as-is.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RepeatOne|FullyQualifiedName~PlayMode|FullyQualifiedName~TrackChanged"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Audio/AudioSystem.cs KhaozEngine.Tests/Audio/AudioSystemTests.cs
git commit -m "feat(Audio): PlayMode.RepeatOne auto-advance replays current track"
```

---

## Task 6: AudioSystem - scope the _available latch (nit 4)

**Files:**
- Modify: `KhaozEngine.Audio/AudioSystem.cs` (`Update()`)
- Test: `KhaozEngine.Tests/Audio/AudioSystemTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `AudioSystemTests.cs`:

```csharp
[Fact]
public void Update_TransientIsPlayingError_SkipsFrameAndStaysAvailable()
{
    var (audio, backend) = NewLoaded("a", "b");
    audio.Update();                              // deferred first play
    int playedBefore = backend.PlayedIndices.Count;

    backend.ThrowOnNextIsPlayingReads = 1;
    audio.Update();                              // IsPlaying read throws -> skip frame, do NOT latch
    Assert.Equal(playedBefore, backend.PlayedIndices.Count);   // no advance this frame

    backend.IsPlaying = false;
    audio.Update();                              // recovered -> audio still alive, advances
    Assert.Equal(playedBefore + 1, backend.PlayedIndices.Count);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Update_TransientIsPlayingError"`
Expected: FAIL - current `Update()` catch sets `_available = false`, so the third `Update()` is a no-op and `PlayedIndices.Count` stays at `playedBefore` (asserts `playedBefore + 1`).

- [ ] **Step 3: Rewrite Update() to scope the latch**

Replace the entire `Update()` method body in `KhaozEngine.Audio/AudioSystem.cs` with:

```csharp
    /// <summary>
    /// Call each frame to detect when the current track ends and queue the next.
    /// Defers first playback to the first Update call so the audio subsystem is ready.
    /// A transient failure reading <see cref="IMusicBackend.IsPlaying"/> skips the frame (logged) and
    /// recovers next frame; only real play/load failures permanently disable audio.
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

        bool isPlaying;
        try
        {
            isPlaying = _backend.IsPlaying;
        }
        catch (Exception ex)
        {
            _logger.Warn("Audio: failed to read IsPlaying; skipping frame.", ex);
            return;
        }

        if (!isPlaying)
        {
            AdvanceTrack();
        }
    }
```

> If Task 5 has not run yet, use `PlayRandomTrack();` in place of `AdvanceTrack();` and let Task 5 do the substitution. If Task 5 already ran, `AdvanceTrack()` exists - use it as shown.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Update_TransientIsPlayingError"`
Expected: PASS.

- [ ] **Step 5: Run the full audio suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AudioSystemTests"`
Expected: PASS (no regression in the existing latch/deferred-play tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Audio/AudioSystem.cs KhaozEngine.Tests/Audio/AudioSystemTests.cs
git commit -m "fix(Audio): transient IsPlaying read error skips a frame instead of killing audio"
```

---

## Task 7: DeterministicRng argument guards (nit 3)

**Files:**
- Modify: `KhaozEngine.Ecs/DeterministicRng.cs`
- Test: `KhaozEngine.Tests/DeterministicRngTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `DeterministicRngTests.cs` (match the file's existing `namespace`/`class`; it is `namespace KhaozEngine.Tests; public class DeterministicRngTests`):

```csharp
[Fact]
public void Next_NonPositiveMax_Throws()
{
    var rng = new DeterministicRng(1);
    Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(-5));
}

[Fact]
public void NextRange_MaxNotAboveMin_Throws()
{
    var rng = new DeterministicRng(1);
    Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(5, 5));
    Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(5, 3));
}

[Fact]
public void Next_ValidRanges_StillWork()
{
    var rng = new DeterministicRng(1);
    int a = rng.Next(10);
    Assert.InRange(a, 0, 9);
    int b = rng.Next(-3, 4);
    Assert.InRange(b, -3, 3);
}
```

Ensure `using System;` is present in the test file (for `ArgumentOutOfRangeException`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Next_NonPositiveMax|FullyQualifiedName~NextRange_MaxNotAboveMin"`
Expected: FAIL - currently `Next(0)` throws `DivideByZeroException` (not `ArgumentOutOfRangeException`), and `Next(5,3)` computes a negative modulo (no throw).

- [ ] **Step 3: Add the guards**

In `KhaozEngine.Ecs/DeterministicRng.cs`, replace the two `Next` methods:

```csharp
    /// <summary>An int in [0, <paramref name="maxExclusive"/>). Uses modulo (negligible bias for game ranges).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is &lt;= 0.</exception>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be positive.");
        return (int)(NextULong() % (ulong)maxExclusive);
    }

    /// <summary>An int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is &lt;= <paramref name="minInclusive"/>.</exception>
    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be greater than minInclusive.");
        return minInclusive + Next(maxExclusive - minInclusive);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DeterministicRngTests"`
Expected: PASS (new guards + existing rng tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Ecs/DeterministicRng.cs KhaozEngine.Tests/DeterministicRngTests.cs
git commit -m "fix(Ecs): guard DeterministicRng.Next against non-positive/empty ranges"
```

---

## Task 8: Strengthen the Effects recycle test (nit 5)

**Files:**
- Modify: `KhaozEngine.Tests/ParticleSystemTests.cs`

- [ ] **Step 1: Replace the weak test with a strengthened one**

In `KhaozEngine.Tests/ParticleSystemTests.cs`, replace the existing `Emit_beyond_capacity_recycles_oldest_slots` test (lines ~33-39) with:

```csharp
    [Fact]
    public void Emit_beyond_capacity_recycles_oldest_slots()
    {
        var sys = NewSystem(poolSize: 4);

        // First batch fills the pool at a far-away position.
        var oldPos = new Vector2(-10000, -10000);
        sys.Emit(ParticlePresets.Spark, oldPos, Color.Gray, 4);
        Assert.Equal(4, sys.ActiveCount);

        // Second batch (also pool-sized) at a distant position must overwrite the OLDEST slots.
        var newPos = new Vector2(10000, 10000);
        sys.Emit(ParticlePresets.Spark, newPos, Color.Gray, 4);

        Assert.Equal(4, sys.ActiveCount);

        // No surviving particle should be anywhere near the old emission point: the originals were
        // actually overwritten, not merely counted. Positions are 20000 apart, far beyond any preset
        // jitter, so a midpoint threshold cleanly separates old from new.
        foreach (var p in sys.ActiveParticles())
        {
            Assert.True(p.Position.X > 0 && p.Position.Y > 0,
                $"particle at {p.Position} survived from the old batch - oldest slots not recycled");
        }
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Emit_beyond_capacity_recycles_oldest_slots"`
Expected: PASS. (If it fails, the recycle logic is not overwriting oldest - investigate before forcing the test.)

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/ParticleSystemTests.cs
git commit -m "test(Effects): assert oldest particles are actually overwritten on recycle"
```

---

## Task 9: USING-KHAOZENGINE.md - Graphics / Camera2D section (nit 6)

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md`

- [ ] **Step 1: Add the section**

Insert a new section in `docs/USING-KHAOZENGINE.md` after the `## ECS layer (KhaozEngine.Ecs)` section and before `## Testing your game's screens headlessly` (keep the surrounding `---` separators consistent with the existing layout):

```markdown
## Graphics layer (`KhaozEngine.Graphics`)

Independent of input/screens. `Camera2D` is a game-agnostic 2D matrix camera. `Position` is the world
point shown at the center of the viewport; `Zoom` (> 0) and `Rotation` (radians, CCW) scale and roll
the view about that point. The core methods take an explicit `Viewport`, so the math is fully headless
(no `GraphicsDevice`); no-arg overloads use the settable `Viewport` property (set it once, refresh on
`Window.ClientSizeChanged`).

```csharp
var cam = new Camera2D { Viewport = GraphicsDevice.Viewport, Zoom = 2f };
cam.Position = player.WorldPosition;                              // follow

// Render world-space content through the view matrix:
spriteBatch.Begin(transformMatrix: cam.GetViewMatrix());
// ... draw world ...
spriteBatch.End();

Vector2 mouseWorld = cam.ScreenToWorld(mouseScreenPos);          // pick/aim in world space
```

- `WorldToScreen` / `ScreenToWorld` convert between spaces (inverse requires `Zoom` > 0; a non-positive
  zoom makes the matrix singular and yields NaN).
- `ClampPosition(desired, worldBounds[, viewport])` returns `desired` clamped so the visible world rect
  stays inside `worldBounds`, centering on any axis where the world is smaller than the view. It does
  not mutate `Position` - assign the result yourself. Exact when `Rotation` is 0 (the typical
  platformer/scroller case); approximate with a rotated camera.

---
```

- [ ] **Step 2: Commit**

```bash
git add docs/USING-KHAOZENGINE.md
git commit -m "docs: add Graphics/Camera2D section to USING-KHAOZENGINE"
```

---

## Task 10: Full suite green + clean Release build (pre-release gate)

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 0 failures. Baseline was 349 tests; expect ~349 + the new tests added above (~17 new). Record the exact count.

- [ ] **Step 2: Clean Release build of the whole solution**

Run: `dotnet build -c Release KhaozEngine.slnx`
Expected: build succeeds, 0 errors (pre-existing XML-doc warnings are acceptable).

---

## Task 11: Release 3.4.0 (version, changelog, consumers, pack) - STOP before tag/push

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>3.3.0</Version>` to `<Version>3.4.0</Version>`.

- [ ] **Step 2: Add the CHANGELOG entry**

Insert a newest-first entry at the top of `CHANGELOG.md` (immediately under the `# Changelog` preamble, above `## KhaozEngine 3.3.0`):

```markdown
## KhaozEngine 3.4.0

Additive feature pass unblocking SpaceGame/Nullwake adoption, plus review-nit fixes. No breaking changes.

- **KhaozEngine.Persistence** - `SettingsManager<T>` gains an optional `sanitizeOnLoad` constructor hook
  (`Func<T,T>`). It runs on every load, including the initial load inside the constructor (which the
  `SettingsLoaded` event can't reach), so callers can clamp fields / migrate a schema version on the
  first load. Null = passthrough; a throwing hook is swallowed/logged. README documents the
  `[JsonExtensionData]` + version-field downgrade-safe migration pattern.
- **KhaozEngine.Audio** - `AudioSystem` now supports explicit and repeating playback alongside random
  rotation: `PlayTrack(int)` / `PlayTrack(string)` (unknown name/index is a logged no-op), a settable
  `PlayMode { RandomRotation, RepeatOne }` (default `RandomRotation`), and now-playing state via
  `CurrentTrack` + the `TrackChanged` event.
- **KhaozEngine.Audio** - a transient exception while reading `IMusicBackend.IsPlaying` in `Update()`
  now skips the frame (logged) and recovers, instead of permanently disabling audio. The availability
  latch is reserved for real play/load failures.
- **KhaozEngine.Ecs** - `DeterministicRng.Next(maxExclusive)` and `Next(min, max)` now throw
  `ArgumentOutOfRangeException` on non-positive / empty ranges (previously a DivideByZero / negative-
  modulo trap).
- Docs/tests: `docs/USING-KHAOZENGINE.md` gains a `KhaozEngine.Graphics` / `Camera2D` section; the
  Effects pool-recycle test now asserts the oldest particles are actually overwritten.
```

- [ ] **Step 3: Update CONSUMERS.md engine version line**

In `docs/CONSUMERS.md`, change the line `**Engine current version:** \`3.3.0\` ...` to `3.4.0`. Leave per-consumer pinned versions unchanged (no consumer has adopted 3.4.0 yet).

- [ ] **Step 4: Re-run the full suite after the version bump**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 0 failures.

- [ ] **Step 5: Pack into the canonical local feed (cumulative)**

The canonical feed is `~/KhaozEngine/local-feed`, NOT the worktree's. Pack there so consumers see 3.4.0:

Run: `dotnet pack -c Release -o /Users/antonio/KhaozEngine/local-feed KhaozEngine.slnx`
Expected: succeeds; new `*.3.4.0.nupkg` files appear in `~/KhaozEngine/local-feed`. Do NOT delete older versions (consumers pin).

Verify: `ls /Users/antonio/KhaozEngine/local-feed | grep 3.4.0`

- [ ] **Step 6: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md
git commit -m "Release KhaozEngine 3.4.0 (Persistence sanitizeOnLoad + Audio playback control + nits)"
```

- [ ] **Step 7: STOP - confirm with the user before tagging/pushing**

Do NOT run `git tag v3.4.0` or push anything yet. Report what shipped (files changed, test count, packed versions) and the proposed merge/tag/push, and wait for explicit user approval. Tagging `v3.4.0` triggers CI to publish to GitHub Packages.

---

## Self-Review notes

- **Spec coverage:** item 1 -> Tasks 1-2; item 2 -> Tasks 3-5; item 3 -> Task 7; item 4 -> Task 6; item 5 -> Task 8; item 6 -> Task 9; release -> Tasks 10-11. All six spec items + release covered.
- **Type consistency:** `CommitPlayed(int)`, `ClearCurrentTrack()`, `AdvanceTrack()`, `CurrentTrack`, `TrackChanged` (`Action<string?>`), `PlayMode` enum + property, `ThrowOnNextIsPlayingReads`, `sanitizeOnLoad` (`Func<T,T>?`) are used identically across tasks.
- **Cross-task ordering:** Tasks 5 and 6 both touch `Update()`; each notes the `PlayRandomTrack()` <-> `AdvanceTrack()` substitution so whichever runs second stays correct. Recommended order is 3 -> 4 -> 5 -> 6.
```
