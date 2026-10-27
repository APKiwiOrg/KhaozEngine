# KhaozEngine.Audio

Game-agnostic audio: streaming music, one-shot SFX, protected loops and 3D positional audio.

- `AudioSystem` - track list, volume (master x music), enable/disable, automatic rotation (`PlayMode`),
  `TrackChanged` events, `PlaySfx` / `PlaySfx3D` / `TryPlayLoop` / `SetListener` / `SfxVolume`. `LoadContent(directory)` then
  `Update()` once per frame.
- `SetRotationPool(trackNames)` limits random rotation to a subset, such as menu music, while `PlayTrack`
  can still play any registered track on demand. Null restores all tracks. Names resolve lazily, and a pool
  with no registered matches falls back to all tracks with a one-time warning.
- **Main-thread-only, enforced.** An `AudioSystem` belongs to the thread that constructed it (normally the
  thread running the frame loop). Its registries are plain dictionaries and lists with no locking, so every
  mutating entry point (register, load, play, tick, volume and bus setters) checks the calling thread and throws
  `InvalidOperationException` when it is the wrong one. The check is a thread-local int compare and is present in
  Release, so a background job firing an SFX fails on its first call instead of quietly corrupting a dictionary.
  Loop controls, `IsLoopActive` and `LoopCapacity` also enforce this check, before inspecting handles or
  disposed state. To trigger audio from a worker, record the request and issue the call from the main thread's
  next frame. `Dispose` is the one exemption, provided it does not race a live update or play call. It
  invalidates loop use before attempting SFX, music and context cleanup in that order. All three are attempted
  even if cleanup throws, and the first exception is rethrown.
- SFX buses - `DefineBus(id)` / `DefineBus(id, SfxAttenuation)` / `SetBusVolume(id, v)` / `GetBusVolume(id)`
  group sounds (UI, ambience, combat, ...)
  under one volume without game-side per-voice bookkeeping. Every `PlaySfx` / `PlaySfx3D` overload takes an optional
  `bus`; effective voice gain = `master x sfx x bus x volume`. No bus (or an unknown bus) = the default bus at
  1.0, so bus-less plays are byte-for-byte the old behavior. One-shots sample this mix at start, so a bus
  volume change applies to their next play. Protected loops update their live gain immediately when master,
  SFX, bus or local loop volume changes. A failed gain write remains dirty and gets at most one refresh per
  normal `Update`, including when no music plays. This never restarts or reallocates a loop. Loop mixing
  clamps finite global factors to 0..1 and treats non-finite factors as zero, without changing one-shot mixing.
  Positional plays on a configured bus carry its immutable inverse-distance curve to the backend. The default
  remains reference distance 1, rolloff 1, maximum distance 50. Non-positional sounds ignore attenuation.
  Construct curves with `new SfxAttenuation(referenceDistance, rolloffFactor, maxDistance)` or use
  `SfxAttenuation.Default`. The all-zero `default(SfxAttenuation)` value is invalid and `DefineBus` rejects it.
- SFX priority - `PlaySfx` / `PlaySfx3D` each have an overload taking an `SfxPriority` (`Low` / `Normal` /
  `High`) right after the name. The pool is small and fixed, so once every voice is busy a new sound can only be
  heard by taking one: with a priority the backend steals the LOWEST-priority voice still playing instead of
  whatever the round robin landed on, which is what stops a barrage of footsteps from cutting a boss cue. Equal
  priorities keep the old oldest-first rotation, a play that states nothing is `Normal`, and a play is never
  dropped (it takes the least valuable voice rather than going silent). `ISfxBackend.Play(..., priority)` is a
  default interface member forwarding to the priority-free overload, so a backend written before it keeps
  compiling untouched.
- SFX unload - `UnregisterSfx(name)` returns true when a loaded buffer is released. With `ISfxLoopBackend`,
  it uses outcome-bearing `TryUnloadSfx(handle)` before removing the name, loaded mapping and warning state.
  A false result or exception returns false and retains registration for deliberate retry. Partial cleanup
  expires confirmed stopped identities while preserving the remaining owners. `UnregisterSfxes(names)`
  counts successful releases only. Unknown or registered-but-unloaded names are removed and return false.
  Successful unregister allows later registration and reload. Legacy backends retain the void
  `ISfxBackend.Unload(handle)` path, whose default implementation is a no-op and cannot confirm release.
- Music crossfade - `MusicCrossfadeDuration` (seconds, default `0` = hard cut) makes every track change fade the
  old track out and the new one in; `CrossfadeTo(name/index, duration)` does a one-off fade. Single-stream
  (fade-out, switch, fade-in) because the `IMusicBackend` seam holds one active track; the fade factor multiplies
  the settings-derived `master x music` volume. Drive it with the `Update(float dt)` overload (`Update()` is
  `Update(0f)`; both identical when no fade is in flight).
- `IMusicBackend` / `ISfxBackend` - the existing backend seams (games/tests may supply their own).
  `ISfxLoopBackend` is the optional protected-loop companion, with admission, controls, ownership queries
  and confirmed unload. Existing `ISfxBackend` implementations need no changes. They report zero loop
  capacity and `Unsupported` for a valid loaded loop request, without substituting a one-shot.
- `OpenAlMusicBackend` - cross-platform OpenAL (Silk.NET.OpenAL) streaming backend, decoding **WAV / OGG
  (NVorbis) / MP3 (NLayer)**, one track at a time with queued-buffer streaming.
- `OpenAlSfxBackend` + `SfxVoicePool` - sixteen one-shot sources plus four separate protected-loop slots over
  a shared OpenAL context, with optional 3D positioning. Ordinary effect pressure cannot steal a loop source.
  `SfxVoicePool` is the unchanged pure one-shot allocation policy: `Next()` is the round
  robin, `Steal(playing)` picks the lowest-priority voice with that rotation as the tie-break.
- Output device following - the OpenAL device opens on the system default output and stays on it. `Update`
  checks once a second and reopens the device in place when the default output changes (speakers to headphones,
  a Bluetooth sink connecting) or the device reports it was lost. The reopen keeps the context, sources and
  buffers, so music resumes mid-track and loaded SFX stay loaded. A failed reopen is logged once and retried.
  Resource-preserving reopen also keeps loop identities and sources without rebuilding playback. Logical
  ownership does not prove audible output or uninterrupted sound through disconnects, which depends on the
  output and device extensions. Keep calling `Update` every frame, including in a game with no music.

## Protected loops

Register and load the sound through the same SFX registry as one-shots, then retain the returned handle:

```csharp
audio.DefineBus("ambience", new SfxAttenuation(2f, 0.5f, 90f));
audio.RegisterSfx("wind");
audio.LoadContent(contentDirectory);
var result = audio.TryPlayLoop(new SfxLoopRequest("wind", Bus: "ambience", Volume: 0.6f, Position: position));
if (result.Status == SfxLoopStartStatus.Started)
{
    SfxLoopHandle handle = result.Handle;
    audio.SetLoopPosition(handle, nextPosition);
    audio.SetLoopVolume(handle, 0.3f);
    bool ownsLoop = audio.IsLoopActive(handle);
    bool stopped = audio.StopLoop(handle);
}
```

Each successful start owns a distinct whole-buffer loop, even for the same key. No position means a flat,
listener-relative loop. A positional loop captures the bus attenuation profile at start. `SetLoopPosition`
returns false for flat loops. Request volume is finite and clamps to 0..1. Pitch is finite and clamps to a
minimum of 0.01. Blank keys and non-finite request values or positions throw argument errors.

`LoopCapacity` reports four for stock OpenAL and null backends. Once all slots are active, reserved,
quarantined or retired, a new request returns `Capacity` without changing existing loops or one-shots.
There is no borrowing, expansion, fallback, queued restart or hidden reacquisition. Other start outcomes
are `NotLoaded`, `Unsupported`, `BackendFailure` and `Disposed`. Only `Started` carries a handle. The
default result is failure with an empty handle. Missing keys and unknown buses share the one-shot
warn-once behavior, with unknown buses using the default mix.

Handles are opaque logical identities scoped to the admitting `AudioSystem`, with no retained system,
backend or native source. Copies refer to the same loop. Default, foreign and expired handles cannot
control another loop. Retaining or dropping a handle does not change playback, so stop it explicitly.
`IsLoopActive` reports logical ownership, including inert null-backend loops, rather
than audibility. The null backend enforces the same four-slot admission, cleanup and identity rules without
creating a native context. Disposed systems report zero capacity and reject loop operations.

`StopLoop` returns true only after confirmed stop and detach. Failure retains ownership for explicit retry.
An uncertain failed start publishes no handle and quarantines its slot instead of silently reusing it.
Explicit unload, `StopAllSfx` or disposal can recover quarantine. Unload stops and detaches referencing
loops and one-shots before deleting their buffer. `StopAllSfx` includes loops and leaves music alone.
Successful cleanup expires the old identity, including when a numeric buffer slot is later reused. Muting
keeps the loop running at zero gain and does not free capacity. No timer retries failed starts or rebuilds
stopped loops.

OpenAL is bundled (openal-soft, `Silk.NET.OpenAL.Soft.Native`) so no system OpenAL is required.

**Linux host natives, packed here as well as in `KhaozEngine.Foundation`
([#723](https://github.com/APKiwiOrg/KhaozEngine/issues/723)).** Silk.NET resolves its bindings itself rather
than through `[DllImport]`, and on Linux it probes `runtimes/<distro rid>/native`, which nothing writes, so the
OpenAL Soft natives never load until they sit flat beside the assemblies. This package carries a build rule that copies them
there on `build` and on a rid-agnostic `publish`, no-op on Windows, macOS and `publish -r <rid>`, opt out with
`<KhaozEngineFlattenHostNatives>false</KhaozEngineFlattenHostNatives>`. It ships here as well as in `Foundation`
because `Foundation` is not in this package's dependency closure, so a project that references this one and no
umbrella would otherwise get nothing. One physical rule file, so the two copies cannot drift.
