# KhaozEngine 3.4.0 - design

Small, focused feature pass unblocking the in-flight SpaceGame and Nullwake adoptions, plus four
review nits. All changes additive / backward-compatible. One version bump (3.3.0 -> 3.4.0) at the end.
Work isolated in worktree `worktree-ke-3.4.0` off `main`.

## Scope (six items)

### 1. `SettingsManager<T>` sanitize-on-load hook (gating: SpaceGame SaveData)

Add an optional `Func<T,T>? sanitizeOnLoad = null` as the last constructor parameter:

```csharp
public SettingsManager(ISettingsStorage storage, ILogger? logger = null, Func<T,T>? sanitizeOnLoad = null)
```

`Load()` applies the hook to the loaded-or-default value before assigning `settings` and before
raising `SettingsLoaded`:

```csharp
public void Load()
{
    T loaded;
    try { loaded = storage.LoadSettings<T>() ?? new T(); }
    catch (Exception ex) { logger?.Error("Failed to load settings; using defaults.", ex); loaded = new T(); }

    if (sanitizeOnLoad is not null)
    {
        try { loaded = sanitizeOnLoad(loaded) ?? loaded; }
        catch (Exception ex) { logger?.Error("sanitizeOnLoad threw; using unsanitized value.", ex); }
    }

    settings = loaded;
    SettingsLoaded?.Invoke(settings);
}
```

Key points:
- The hook runs on the **initial load in the constructor** (the whole point: `SettingsLoaded`
  fires in the ctor before a caller can subscribe, so the event can't sanitize the first load; the
  ctor-supplied delegate can).
- Runs on **every** subsequent `Load()` / reload too.
- Runs on the failure-fallback `new T()` path as well, so a migration always sees a consistent object.
- A hook that returns null is treated as "no change" (`?? loaded`) - defensive; the hook contract is
  to return the sanitized object.
- A hook that throws is swallowed + logged (audio/settings swallow-don't-crash house style); the
  unsanitized value is used.
- `null` hook = passthrough. Backward compatible (param defaults to null).

Docs: add the downgrade-safety pattern to the Persistence README - a DTO with `[JsonExtensionData]`
(preserves unknown fields from a newer schema across a save round-trip) + a version field + a
`sanitizeOnLoad` that migrates. **No engine code** for ExtensionData: `FileSettingsStorage` already
round-trips the live object through `System.Text.Json`, preserving unknown fields.

Tests: hook runs on first load (ctor); runs on a reload; null hook = passthrough; a value the hook
clamps is what `Settings` exposes.

### 2. `AudioSystem` explicit + repeat playback + now-playing (gating: SpaceGame MusicPlaybackController)

All additive; default behaviour unchanged.

- `PlayTrack(int index)` and `PlayTrack(string name)` - explicit selection alongside
  `PlayRandomTrack`. Index resolves against `_trackNames` order (== backend load order). Unknown
  name or out-of-range index: `_logger.Warn` + no-op (swallow-don't-crash; do not throw). Honours the
  existing `_available` / `_musicEnabled` / `trackCount == 0` guards exactly like `PlayRandomTrack`.
- `enum PlayMode { RandomRotation, RepeatOne }` as a settable property, default `RandomRotation`. In
  `Update`'s auto-advance branch (`!_backend.IsPlaying`): `RandomRotation` -> `PlayRandomTrack`
  (current behaviour); `RepeatOne` -> replay the current track via its index.
- `CurrentTrack` (`string?` - the playing track's name, or null when nothing is playing) and an
  `event Action<string?>? TrackChanged` for a now-playing UI. Both updated centrally at the single
  point where `_lastTrackIndex` is committed after a successful play, plus cleared to null on `Stop`
  (MusicEnabled = false). `TrackChanged` fires only on an actual change of the current name.
- Keep `PlayRandomTrack`, idempotent registration, `MusicEnabled`, volume, the post-load eager-load
  fix, etc. all as-is. Drive everything through `FakeMusicBackend` in tests.

Implementation note: introduce a single private helper that commits a successful play
(`_lastTrackIndex = index; set CurrentTrack from _trackNames[index]; fire TrackChanged if changed`)
and call it from `PlayRandomTrack` and `PlayTrack`. `RepeatOne` auto-advance calls
`PlayTrack(_lastTrackIndex)` (or `PlayRandomTrack` when no track has played yet).

Decision: `TrackChanged` fires only when the current track NAME changes. A `RepeatOne` auto-advance
that replays the same track does NOT fire `TrackChanged` (the now-playing name is unchanged), even
though the backend restarts playback.

Tests: `PlayTrack(name)` plays it and sets `CurrentTrack`; `RepeatOne` replays the same track on
auto-advance (not a random other); `TrackChanged` fires when the name changes and not on a same-name
`RepeatOne` replay; unknown `PlayTrack` name is a logged no-op; default `PlayMode` preserves existing
random-rotation behaviour.

### 3. `DeterministicRng` argument guards (review nit; pre-existing bug)

`Next(int maxExclusive)` currently does `NextULong() % (ulong)maxExclusive` - a `maxExclusive <= 0`
is a DivideByZero / huge-cast trap. Guard both overloads:

- `Next(maxExclusive)`: throw `ArgumentOutOfRangeException` when `maxExclusive <= 0`.
- `Next(minInclusive, maxExclusive)`: throw `ArgumentOutOfRangeException` when `maxExclusive <= minInclusive`.

Tests: each overload throws on the bad boundary; valid ranges still work.

### 4. Audio `_available` latch scoping (review nit) - coordinator decision: option 1

The permanent `_available = false` latch stays **only for real play/load failures** (`TryPlayTrack`
returning false, or a throw during an actual play attempt). A throw while **reading
`_backend.IsPlaying`** in `Update()` no longer latches: it logs `Warn` and skips the frame, so audio
recovers next frame.

```csharp
public void Update()
{
    if (_backend.TrackCount == 0 || !_available || !_musicEnabled) return;
    if (!_started) { _started = true; PlayRandomTrack(); return; }

    bool isPlaying;
    try { isPlaying = _backend.IsPlaying; }
    catch (Exception ex) { _logger.Warn("Audio: IsPlaying read failed; skipping frame.", ex); return; }

    if (!isPlaying) AdvanceTrack();   // PlayRandomTrack or RepeatOne replay; these still latch on real failure
}
```

Not gold-plating: if a persistently-throwing `IsPlaying` ever spams Warn every frame in practice,
rate-limit later - not now.

Test: a backend that throws once on `IsPlaying` then recovers -> audio stays available and resumes
auto-advance on the following frame.

### 5. Strengthen `Emit_beyond_capacity_recycles_oldest_slots` (review nit)

Current test only asserts `ActiveCount == poolSize`. Strengthen to prove the OLDEST particles were
overwritten: poolSize 4, emit 4 at a far position A (fills the pool), emit 4 more at a distant
position B; assert all active particles are at B (none survive at A), in addition to
`ActiveCount == 4`. Use positions far enough apart that preset jitter cannot blur A into B.

### 6. `docs/USING-KHAOZENGINE.md` - Graphics / Camera2D section (review nit)

Add a `KhaozEngine.Graphics` / `Camera2D` section to the consumer-contract doc, mirroring the style of
the existing per-package sections (Input / Screens / UI / Ecs / Diagnostics). The package currently
has no entry there.

## Out of scope (tracked in docs/ROADMAP.md - do NOT start)

Camera follow layer, screen shake, particle unification, `PrimitiveRenderer.DrawCircle/DrawRing`, SFX
audio.

## Release ritual (3.4.0)

Per `CLAUDE.md`, in order:
1. Bump `<Version>` 3.3.0 -> 3.4.0 in `Directory.Build.props`.
2. Add a newest-first 3.4.0 `CHANGELOG.md` entry (SettingsManager hook + AudioSystem additions +
   the three nit fixes/doc).
3. Update the engine-version line in `docs/CONSUMERS.md` to 3.4.0.
4. `dotnet pack -c Release -o ./local-feed` against the canonical `~/KhaozEngine/local-feed`
   (cumulative; don't delete old versions).
5. Full suite green + clean Release build.
6. **STOP and confirm with the user** before `git tag v3.4.0` + pushing `main` + the tag (the tag
   triggers CI publish).

## Testing

net10.0, xUnit, headless. Every new behaviour ships with a test in `KhaozEngine.Tests`, driven
through `FakeMusicBackend` / in-memory storage. No `Mouse/Keyboard/GamePad/TouchPanel` statics.
