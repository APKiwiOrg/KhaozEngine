# KhaozEngine.Audio

Game-agnostic background music for MonoGame games. Works around MonoGame's broken
`Song`/`MediaPlayer` backend on macOS by using an `AVAudioPlayer` bridge (ObjC P/Invoke);
uses MonoGame `MediaPlayer` everywhere else. The backend is chosen automatically by OS.

## Quick start

```csharp
using KhaozEngine.Audio;

var audio = new AudioSystem(new[]
{
    "Music/track_one",
    "Music/track_two",
})
{
    MasterVolume = 0.66f,
    MusicVolume = 0.4f,
    MusicEnabled = true,
};

// register more tracks any time (idempotent; late ones eager-load after LoadContent):
audio.RegisterTrack("Music/track_three");

audio.LoadContent(Content);   // MonoGame ContentManager
// each frame:
audio.Update();               // advances to a new random track when the current ends
// on shutdown:
audio.Dispose();
```

## Notes

- Track names are content-pipeline asset names (no extension), e.g. `Music/foo`.
  The MonoGame backend loads `foo.xnb` via the pipeline; the macOS backend plays the
  raw `foo.mp3` from the content directory. Ship both for cross-platform builds.
- Registration is idempotent (re-registering a known track is a no-op) and works before
  or after `LoadContent`; tracks registered after load are eager-loaded immediately, so
  DLC / runtime track additions just work.
- Logging routes through `KhaozEngine.Diagnostics`. Pass an `ILogger` to the
  constructor, or leave it null to use the ambient `Log` facade (a no-op until the
  game calls `Log.Configure(...)`, then audio logs flow to the configured log).
- `IMusicBackend` and the concrete `MonoGameMusicBackend` / `MacOsMusicBackend` are
  public, so a custom platform (e.g. iOS) can supply or compose its own backend via the
  `AudioSystem(IMusicBackend, ...)` constructor.
