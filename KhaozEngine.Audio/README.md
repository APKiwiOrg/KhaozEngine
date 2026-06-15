# KhaozEngine.Audio (experimental, 5.x)

Game-agnostic background-music backend on the custom MonoGame-free stack.

- `AudioSystem` — track list, volume (master x music), enable/disable, automatic rotation (`PlayMode`),
  `TrackChanged` events. `LoadContent(directory)` then `Update()` once per frame.
- `IMusicBackend` — the backend seam (games/tests may supply their own).
- `OpenAlMusicBackend` — cross-platform OpenAL (Silk.NET.OpenAL) streaming backend, decoding **WAV / OGG
  (NVorbis) / MP3 (NLayer)**, one track at a time with queued-buffer streaming.

No MonoGame. Part of the post-MonoGame 5.x line (`docs/ROADMAP.md`). `OpenAlMusicBackend` needs an OpenAL
implementation at runtime (macOS ships one; bundle openal-soft for production). Music-only; SFX is a future
layer.
