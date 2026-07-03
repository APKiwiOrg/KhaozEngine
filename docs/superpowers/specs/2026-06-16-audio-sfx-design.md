# SFX mixer + positional audio for KhaozEngine.Audio (5.34.0)

Add short-sound (SFX) playback with optional 3D positioning to `KhaozEngine.Audio`, alongside the existing
OpenAL streaming music. Today the package only does background music (`AudioSystem` + `IMusicBackend` +
`OpenAlMusicBackend`); games have no way to play one-shot effects (fire / hit / death). This adds that.

The hard constraint: **OpenAL has one current context per process.** `OpenAlMusicBackend` currently opens and
owns the device/context privately. SFX sources must live in the SAME context, so we extract a shared
`OpenAlContext` that both the music backend and a new SFX backend use, owned by `AudioSystem`.

User decisions for this release: synthesize placeholder SFX (ship a tiny WAV synth), and INCLUDE positional 3D
audio now (per-sound position + listener).

## Part A — shared OpenAL context (`KhaozEngine.Audio`, internal)

New `internal sealed unsafe class OpenAlContext : IDisposable`:
- Ctor opens the device + context exactly as `OpenAlMusicBackend` does today: `ALContext.GetApi(true)` /
  `AL.GetApi(true)` (the `true` = bundled openal-soft), `OpenDevice("")` (throw if null), `CreateContext`,
  `MakeContextCurrent`. Throws if no device (caller falls back to silence).
- Exposes `AL Al { get; }` and `ALContext Alc { get; }` (internal) for the backends to share.
- `Dispose`: `MakeContextCurrent(null)`, `DestroyContext`, `CloseDevice`, dispose `Al`/`Alc`.

Refactor `OpenAlMusicBackend` to use a shared context instead of owning one, WITHOUT breaking its existing
public ctor:
- Keep `public OpenAlMusicBackend(ILogger? logger = null)` working: it now creates its OWN `OpenAlContext`
  internally and owns it (back-compat for any direct consumer).
- Add `internal OpenAlMusicBackend(OpenAlContext context, ILogger? logger = null)`: uses the shared context,
  does NOT own it.
- Track ownership with a `bool _ownsContext`; `Dispose` only disposes the context it created. All the existing
  `_al` / device usage routes through the context's `Al`. (The streaming logic is unchanged.)

## Part B — SFX backend seam (`KhaozEngine.Audio`)

Mirror the `IMusicBackend` pattern.

New `public interface ISfxBackend : IDisposable`:
```csharp
string Name { get; }
/// Fully decode a file into one buffer; returns a handle (>=0), or -1 on failure.
int Load(string path);
/// Play a one-shot on a pooled voice. positional=false => attached to the listener (no attenuation).
void Play(int handle, float gain, float pitch, bool positional, System.Numerics.Vector3 position);
/// Set the 3D listener pose (positional sounds attenuate/pan relative to this).
void SetListener(System.Numerics.Vector3 position, System.Numerics.Vector3 forward, System.Numerics.Vector3 up);
void StopAll();
void Dispose();
```
(`System.Numerics` is BCL, fine for this package. No `Update()` needed: OpenAL one-shots are fire-and-forget;
voices are reclaimed by querying source state on the next `Play`.)

New `public sealed class NullSfxBackend : ISfxBackend` — `Name => "Null"`, `Load` returns a running counter
(>=0) so handles look valid, everything else no-ops. Headless-safe (no device).

New `internal sealed unsafe class OpenAlSfxBackend : ISfxBackend`:
- Ctor takes the shared `OpenAlContext` + `ILogger?`. Generates a pool of `VoiceCount = 16` sources up front
  (`GenSource` x16) and configures the distance model once (`_al.SetDistanceModel` is not in Silk's AL surface;
  instead set per-source `SourceFloat.ReferenceDistance`/`RolloffFactor`/`MaxDistance` at play time for
  positional sounds — reference 1, rolloff 1, max ~50; sane defaults).
- `Load(path)`: `PcmDecoders.Open(path)` (internal, same assembly), read ALL samples by looping `ReadSamples`
  into a growing `short[]` until 0, `GenBuffer`, `BufferData(format from Channels, data, sampleRate)`, store the
  buffer id in a `List<uint> _buffers`; return its index. On any exception: log + return -1. (Whole-file decode,
  not streamed — SFX are short.)
- `Play(handle, gain, pitch, positional, position)`: bounds-check handle; pick a voice via the pure
  `SfxVoicePool` (see Part C) BUT prefer a genuinely idle source first (query `GetSourceInteger.SourceState !=
  Playing`); detach+attach the buffer (`SourceInteger.Buffer = bufferId`), set `SourceFloat.Gain = gain`,
  `SourceFloat.Pitch = max(0.01, pitch)`. If positional: `SourceBoolean.SourceRelative = false`, set
  `SourceVector3.Position = position`, reference/rolloff/max as above. If not positional: `SourceRelative =
  true`, `SourceVector3.Position = (0,0,0)` (heard at full gain regardless of listener). Then `SourcePlay`.
- `SetListener(pos, fwd, up)`: `SetListenerProperty(ListenerVector3.Position, pos)` and the 6-float orientation
  (`ListenerFloatArray.Orientation` = {fwd.X,fwd.Y,fwd.Z, up.X,up.Y,up.Z}`).
- `StopAll`: `SourceStop` every voice.
- `Dispose`: stop + `DeleteSource` all voices, `DeleteBuffer` all buffers. (Does NOT dispose the shared context.)

## Part C — pure voice-allocation policy (`KhaozEngine.Audio`, testable)

New `internal sealed class SfxVoicePool` (no OpenAL dependency, headless-testable):
- Ctor `SfxVoicePool(int count)`.
- `int Next()`: round-robin cursor over `[0, count)`, advancing each call (steals in rotation when all busy).
- This is the fallback policy; the OpenAL backend prefers a truly-idle source and only falls back to `Next()`.
- Test: `Next()` cycles 0,1,...,count-1,0,... deterministically; `count<=0` guarded.

## Part D — `AudioSystem` SFX API (`KhaozEngine.Audio`, public)

Extend `AudioSystem` (do not break music):
- Construct the shared context once. Replace the `CreateBackend` music-only path with a combined setup: try to
  create one `OpenAlContext`; on success build `new OpenAlMusicBackend(context)` + `new OpenAlSfxBackend(context)`
  and remember to dispose the context LAST; on failure (no device) use `NullMusicBackend` + `NullSfxBackend` and
  no context (silent, no crash) — preserve today's fallback behavior + logging.
  - Keep BOTH existing public ctors working. The `AudioSystem(IMusicBackend backend, ...)` test ctor keeps a
    `NullSfxBackend` (no shared context) so existing music-only construction is unaffected. Add an overload /
    optional param so a test can inject an `ISfxBackend` too: `AudioSystem(IMusicBackend music, ISfxBackend sfx,
    IEnumerable<string>? trackNames = null, ILogger? logger = null)`.
- New state: `float _sfxVolume = 0.7f`; `Dictionary<string,int> _sfx` (name -> handle); reuse `_contentDirectory`.
- `public float SfxVolume { get; set; }` (clamp 0..1; no eager apply — SFX gain is computed per play).
- `public void RegisterSfx(string name)` / `RegisterSfxes(IEnumerable<string>)`: record names; if already loaded,
  eager-load now (mirror `RegisterTrack`); else load in `LoadContent`. Loading looks for `name + .wav/.ogg/.mp3`
  in the content dir and maps `name -> _sfxBackend.Load(path)` (skip + warn on -1 / missing file).
- Hook `LoadContent` to also load all registered SFX (same content dir as music).
- `public void PlaySfx(string name, float volume = 1f, float pitch = 1f)`: if available + name known, gain =
  `MasterVolume * SfxVolume * clamp01(volume)`, call `_sfxBackend.Play(handle, gain, pitch, positional:false,
  default)`. Unknown name = warn-once + no-op. Guard exceptions like the music path (don't permanently disable
  music on an SFX hiccup; just log).
- `public void PlaySfx3D(string name, System.Numerics.Vector3 position, float volume = 1f, float pitch = 1f)`:
  as above with `positional:true, position`.
- `public void SetListener(System.Numerics.Vector3 position, System.Numerics.Vector3 forward,
  System.Numerics.Vector3 up)`: forward to `_sfxBackend.SetListener`.
- `Dispose`: dispose `_sfxBackend`, then `_backend` (music), then the shared `OpenAlContext` (if owned).

## Part E — placeholder WAV synth (`KhaozEngine.Audio`, public)

New `public static class WavSynth` — writes mono 16-bit PCM RIFF/WAVE files for placeholder SFX:
- `public static void WriteTone(string path, float frequencyHz, float seconds, Waveform waveform = Waveform.Sine,
  float amplitude = 0.6f, int sampleRate = 44100, float attack = 0.005f, float release = 0.05f)` — a tone with a
  short linear attack + release envelope (avoids clicks).
- `public static void WriteNoise(string path, float seconds, float amplitude = 0.5f, int sampleRate = 44100,
  float attack = 0.001f, float release = 0.08f)` — white noise burst (deterministic xorshift seed, so output is
  reproducible) for thuds / explosions.
- `public enum Waveform { Sine, Square, Saw }`.
- Writes a standard 44-byte header + PCM data. Used by samples / games to generate audible placeholders.

## Part F — sample (live smoke)

Add SFX to an existing sample so it can be run on this Mac to prove the OpenAL SFX path loads + plays without
crashing. Simplest: extend `MiniGame` or `WindowingSample` to, at startup, `WavSynth.WriteTone` a couple of
sounds into a temp dir, `RegisterSfx` + `LoadContent` them, and `PlaySfx` on a key press (e.g. Space) and a
`PlaySfx3D` on another. Honor `KE_MAX_FRAMES` for a clean exit. (We can't hear it here; a clean multi-frame run
through the real OpenAL SFX path is the proof, matching how music was verified.)

## Tests (headless, KhaozEngine.Tests)

All must pass WITHOUT an audio device (no OpenAL):
1. `SfxVoicePool.Next()` round-robins deterministically; guards `count<=0`.
2. `AudioSystem` SFX routing via a **fake `ISfxBackend`** (records Play/SetListener calls):
   - `PlaySfx("x", 0.5f)` with Master=0.66, Sfx=0.7 => Play called with gain == 0.66*0.7*0.5 (within 1e-4),
     positional == false.
   - `PlaySfx3D("x", pos, 1f)` => positional == true, position == pos.
   - `SetListener(...)` forwards verbatim.
   - Unknown SFX name => no Play call (no throw).
   - `SfxVolume` clamps to [0,1].
   - Use the new `AudioSystem(music, sfx, ...)` ctor with `NullMusicBackend` + the fake, and a content dir;
     `RegisterSfx` + `LoadContent` must map the name (fake `Load` returns a valid handle).
3. `WavSynth.WriteTone` / `WriteNoise` produce a valid RIFF/WAVE: assert the header bytes ("RIFF","WAVE","fmt ",
   "data"), channels==1, the declared sample rate, and a data chunk length == expected sample count * 2. (Header
   parse in the test; no decoder internals needed.) `WriteNoise` is reproducible for a fixed seed.

## Release

- Bump `<KhaozEngineVersion>` 5.33.0 -> 5.34.0; CHANGELOG entry (newest-first); update the Audio package
  `<Description>` to mention SFX + positional. Pack the 8 5.x packages to `local-feed`. Merge --no-ff, run the
  suite on main, pack canonical, tag `v5.34.0`, push main + tag.
- (Hardpoint adoption — wire `PlaySfx`/`PlaySfx3D` at the existing CombatVfx muzzle/hit/death hooks + generate
  placeholder sounds with `WavSynth`, set the listener from the iso camera — is a SEPARATE follow-up after the
  engine ships, in the Hardpoint repo.)

## Verification
- `dotnet build KhaozEngine.slnx` clean.
- `dotnet test KhaozEngine.Tests` green (report count); the new headless SFX tests pass with no device.
- `KE_GPU_TESTS=1 dotnet test --filter Golden` still green (untouched).
- Sample smoke: `KE_MAX_FRAMES=120 dotnet run --project <sample>` runs the OpenAL SFX path + exits 0 (report).
- `grep` confirms no public Veldrid/OpenAL-context leak beyond the package (OpenAlContext stays internal).
