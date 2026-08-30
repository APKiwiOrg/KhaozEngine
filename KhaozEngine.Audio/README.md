# KhaozEngine.Audio

Game-agnostic audio on the custom MonoGame-free stack: streaming music + SFX + 3D positional audio.

- `AudioSystem` - track list, volume (master x music), enable/disable, automatic rotation (`PlayMode`),
  `TrackChanged` events; `PlaySfx` / `PlaySfx3D` / `SetListener` / `SfxVolume`. `LoadContent(directory)` then
  `Update()` once per frame.
- **Main-thread-only, enforced.** An `AudioSystem` belongs to the thread that constructed it (normally the
  thread running the frame loop). Its registries are plain dictionaries and lists with no locking, so every
  mutating entry point (register, load, play, tick, volume and bus setters) checks the calling thread and throws
  `InvalidOperationException` when it is the wrong one. The check is a thread-local int compare and is present in
  Release, so a background job firing an SFX fails on its first call instead of quietly corrupting a dictionary.
  To trigger audio from a worker, record the request and issue the call from the main thread's next frame.
  `Dispose` is the one exemption, so a shutdown path on another thread is not turned into a crash.
- SFX buses - `DefineBus(id)` / `SetBusVolume(id, v)` / `GetBusVolume(id)` group sounds (UI, ambience, combat, ...)
  under one volume without per-voice bookkeeping. Every `PlaySfx` / `PlaySfx3D` overload takes an optional
  `bus`; effective voice gain = `master x sfx x bus x volume`. No bus (or an unknown bus) = the default bus at
  1.0, so bus-less plays are byte-for-byte the old behavior. A bus volume change applies on the NEXT play on
  that bus (the `ISfxBackend` seam is fire-and-forget: no live per-voice re-gain).
- SFX priority - `PlaySfx` / `PlaySfx3D` each have an overload taking an `SfxPriority` (`Low` / `Normal` /
  `High`) right after the name. The pool is small and fixed, so once every voice is busy a new sound can only be
  heard by taking one: with a priority the backend steals the LOWEST-priority voice still playing instead of
  whatever the round robin landed on, which is what stops a barrage of footsteps from cutting a boss cue. Equal
  priorities keep the old oldest-first rotation, a play that states nothing is `Normal`, and a play is never
  dropped (it takes the least valuable voice rather than going silent). `ISfxBackend.Play(..., priority)` is a
  default interface member forwarding to the priority-free overload, so a backend written before it keeps
  compiling untouched.
- SFX unload - `UnregisterSfx(name)` / `UnregisterSfxes(names)` drop a sound from the registry and release its
  buffer through `ISfxBackend.Unload(handle)`, so a zone-scoped or level-scoped sound set can be freed instead
  of living for the whole process. The name can be registered again later, which reloads it. `Unload` is a
  default interface member (a no-op), so a backend written before it keeps compiling untouched.
- Music crossfade - `MusicCrossfadeDuration` (seconds, default `0` = hard cut) makes every track change fade the
  old track out and the new one in; `CrossfadeTo(name/index, duration)` does a one-off fade. Single-stream
  (fade-out, switch, fade-in) because the `IMusicBackend` seam holds one active track; the fade factor multiplies
  the settings-derived `master x music` volume. Drive it with the `Update(float dt)` overload (`Update()` is
  `Update(0f)`; both identical when no fade is in flight).
- `IMusicBackend` / `ISfxBackend` - the backend seams (games/tests may supply their own).
- `OpenAlMusicBackend` - cross-platform OpenAL (Silk.NET.OpenAL) streaming backend, decoding **WAV / OGG
  (NVorbis) / MP3 (NLayer)**, one track at a time with queued-buffer streaming.
- `OpenAlSfxBackend` + `SfxVoicePool` - a 16-voice one-shot SFX pool over a shared OpenAL context, with
  optional 3D positioning. `SfxVoicePool` is the pure (device-free) allocation policy: `Next()` is the round
  robin, `Steal(playing)` picks the lowest-priority voice with that rotation as the tie-break.

No MonoGame. OpenAL is bundled (openal-soft, Silk.NET.OpenAL.Soft
.Native) so no system OpenAL is required.
