# KhaozEngine.Audio — Design (Batch 2, Item 7)

Promote Nullwake's music/audio backend into a new game-agnostic package
`KhaozEngine.Audio`. Source: `Nullwake.Core/Systems/{IMusicBackend,AudioSystem,
MonoGameMusicBackend,MacOsMusicBackend,MacOsMusicPlayer}.cs`.

## Why

Every MonoGame game on macOS hits MonoGame's broken `Song`/`MediaPlayer` backend.
Nullwake worked around it with an `AVAudioPlayer` bridge via ObjC P/Invoke and an
OS-selected backend. That whole stack is game-agnostic except a hardcoded track list,
so it belongs in the shared engine.

## Re-verification (done before design)

- All 5 source files still exist in `Nullwake.Core/Systems/` and match the brief.
- `IMusicBackend` is `internal`; `AudioSystem` is `public sealed`; `MacOsMusicPlayer`
  is the ObjC/AVAudioPlayer P/Invoke bridge.
- The engine has **no** `KhaozEngine.Audio` package and nothing audio-related elsewhere
  (`.Content`, `.App`). Not already covered by Batch 1.
- The five files log via Nullwake's static `Engine.GameLogger`. Batch 1 shipped
  `KhaozEngine.Diagnostics` (`ILogger`); `SaveEncoder` set the convention of a
  constructor-injected `ILogger`.

## Coordinator decisions (locked)

1. **Package:** new standalone `KhaozEngine.Audio`.
2. **Track registration:** both — constructor seed list **and** additive `RegisterTracks`.
3. **Test seam:** `IMusicBackend` made **public** + an injectable-backend `AudioSystem`
   constructor.
4. **Logging:** constructor-injected `KhaozEngine.Diagnostics.ILogger`.

## Package

- `KhaozEngine.Audio`, net10.0, `Nullable=enable`, `ImplicitUsings=disable` (matches
  `Directory.Build.props`).
- References: `MonoGame.Framework.DesktopGL 3.8.*` (for `ContentManager`, `Song`,
  `MediaPlayer`, `MathHelper`) and `ProjectReference` to `KhaozEngine.Diagnostics`
  (for `ILogger` / `Log`).
- Ships `README.md` packed like the other packages.
- Namespace: `KhaozEngine.Audio`.

## Files (5 lifted + README)

| File | Visibility | Change from Nullwake |
|---|---|---|
| `IMusicBackend.cs` | **public** (was internal) | public test/extensibility seam; members unchanged |
| `AudioSystem.cs` | `public sealed` | track list parameterized; `ILogger` injected; backend injectable; static `TrackNames` deleted |
| `MonoGameMusicBackend.cs` | internal | `GameLogger` → injected `ILogger` |
| `MacOsMusicBackend.cs` | internal | `GameLogger` → injected `ILogger`; passes logger to player |
| `MacOsMusicPlayer.cs` | internal | `GameLogger` → injected `ILogger`; P/Invoke unchanged |

`IMusicBackend` members (unchanged): `Name`, `TrackCount`, `IsPlaying`,
`TryLoadTrack(ContentManager, string contentDirectory, string trackName, int trackIndex)`,
`TryPlayTrack(int, float)`, `Stop()`, `SetVolume(float)`, plus `IDisposable`.

## `AudioSystem` public API

```csharp
// default: OS-selected backend (macOS -> AVAudioPlayer, else MonoGame MediaPlayer)
public AudioSystem(IEnumerable<string>? trackNames = null, ILogger? logger = null);
// injected backend: tests (FakeMusicBackend) or a custom platform backend
public AudioSystem(IMusicBackend backend, IEnumerable<string>? trackNames = null, ILogger? logger = null);

public void RegisterTrack(string trackName);               // additive, before LoadContent
public void RegisterTracks(IEnumerable<string> trackNames); // throws InvalidOperationException after LoadContent
public void SetRng(Random rng);
public void LoadContent(ContentManager content);
public void PlayRandomTrack();
public void Update();
public void Dispose();
public float MasterVolume { get; set; }   // 0.66 default
public float MusicVolume  { get; set; }   // 0.4 default
public bool  MusicEnabled { get; set; }   // true default
```

Behaviour notes:

- `logger` defaults (when null) to `Log.For<AudioSystem>()` — a no-op when the game
  hasn't configured `KhaozEngine.Diagnostics`, real logs when it has. (`NullLogger`
  is internal to Diagnostics, so the `Log` facade is the public equivalent of the
  "NullLogger default".) The resolved logger is threaded into any backend the default
  factory constructs; an injected backend brings its own logger.
- Parameterless `new AudioSystem()` still compiles, so the existing object-initializer
  consumer pattern (`new AudioSystem { MasterVolume = ... }`) survives. The two
  constructors are unambiguous because the injected-backend overload requires a
  non-optional `IMusicBackend` first argument.
- The hardcoded `TrackNames` array is deleted; the caller registers its tracks via the
  constructor seed and/or `RegisterTracks`. Registration after `LoadContent` throws
  `InvalidOperationException` (otherwise it would be a silent no-op).
- The rotation / volume-scaling (`MasterVolume * MusicVolume`) / `MusicEnabled` toggle /
  `_available` / `_loaded` / `_started` state machine is lifted verbatim; only the
  logger and track source change.
- `CreateBackend` stays lazy — it only instantiates the chosen backend, so the macOS
  path never forces the MonoGame Media types to load.

## Test seam (KhaozEngine.Tests)

A `FakeMusicBackend : IMusicBackend` in the test project: records `TryPlayTrack`/
`SetVolume`/`Stop` calls, exposes a settable `IsPlaying`, and has configurable
load/play success. `AudioSystem` is constructed with the injected-backend overload.

Headless tests (`KhaozEngine.Tests`):

1. Ctor-seed tracks **and** `RegisterTracks` both contribute; `LoadContent` loads all;
   `TrackCount` reflects loaded.
2. `RegisterTrack`/`RegisterTracks` after `LoadContent` throws `InvalidOperationException`.
3. Rotation with multiple tracks never repeats the previous index (seeded RNG, many
   draws); single track always index 0.
4. Volume scaling: `MasterVolume * MusicVolume` reaches `TryPlayTrack`'s volume arg and
   `SetVolume`; values clamped to `[0,1]`.
5. `MusicEnabled = false` → `Stop`; `= true` → plays. Toggle is a no-op when unchanged.
6. `Update` defers the first play to the first call (`_started`), then plays the next
   track only when `!IsPlaying`.
7. `TryPlayTrack` returning false flips `_available` off; no further play attempts.
8. `SetRng` makes rotation deterministic.
9. `Dispose` disposes the backend.

`LoadContent` requires a `ContentManager`; tests pass an empty
`new ContentManager(stubServiceProvider)` (the fake backend ignores it — it never calls
`content.Load`). The real MonoGame and macOS backends stay untested: P/Invoke and
MonoGame `Song` loading aren't unit-testable, which is exactly why the seam exists.

## Wiring (one-line additions only, per Batch-2 rules)

- Add `KhaozEngine.Audio/KhaozEngine.Audio.csproj` to `KhaozEngine.slnx`.
- Add a `ProjectReference` to `KhaozEngine.Audio` in `KhaozEngine.Tests.csproj`.

No `<Version>` bump, no `CHANGELOG.md` entry, no `dotnet pack` into the shared
`local-feed`. The coordinating chat owns the batched 3.3.0 release.

## Out of scope (stays consumer-side)

- The `[MethodImpl(NoInlining)]` JIT-isolation wrappers in `NullwakeGame` that defer
  loading `Microsoft.Xna.Framework.Media` — a game concern.
- `ServiceLocator` registration of the `AudioSystem`.
- Content-pipeline packaging (`.xnb` for non-mac, raw `.mp3` for the macOS file path
  backend) — the consumer ships the assets; the package only consumes track names.
  Noted in the package README.

## Open questions for the coordinator

- Confirm the `AudioSystem(IMusicBackend, ...)` ctor should be **public** (the brief's
  "inject" choice implies public; it also lets a game supply a custom backend, e.g.
  iOS). If a smaller surface is preferred, it can be `internal` + `InternalsVisibleTo`
  the test project instead.
