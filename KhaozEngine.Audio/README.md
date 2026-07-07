# KhaozEngine.Audio

Game-agnostic audio on the custom MonoGame-free stack: streaming music + SFX + 3D positional audio.

- `AudioSystem` - track list, volume (master x music), enable/disable, automatic rotation (`PlayMode`),
  `TrackChanged` events; `PlaySfx` / `PlaySfx3D` / `SetListener` / `SfxVolume`. `LoadContent(directory)` then
  `Update()` once per frame.
- Music crossfade - `MusicCrossfadeDuration` (seconds, default `0` = hard cut) makes every track change fade the
  old track out and the new one in; `CrossfadeTo(name/index, duration)` does a one-off fade. Single-stream
  (fade-out, switch, fade-in) because the `IMusicBackend` seam holds one active track; the fade factor multiplies
  the settings-derived `master x music` volume. Drive it with the `Update(float dt)` overload (`Update()` is
  `Update(0f)`; both identical when no fade is in flight).
- `IMusicBackend` / `ISfxBackend` - the backend seams (games/tests may supply their own).
- `OpenAlMusicBackend` - cross-platform OpenAL (Silk.NET.OpenAL) streaming backend, decoding **WAV / OGG
  (NVorbis) / MP3 (NLayer)**, one track at a time with queued-buffer streaming.
- `OpenAlSfxBackend` + `SfxVoicePool` - a 16-voice one-shot SFX pool over a shared OpenAL context, with
  optional 3D positioning.

No MonoGame. OpenAL is bundled (openal-soft, Silk.NET.OpenAL.Soft
.Native) so no system OpenAL is required.
