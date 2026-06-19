# KhaozEngine.Primitives leaf + cleanup batch (6.0.0)

Status: approved design, pre-implementation
Date: 2026-06-19
Target release: `6.0.0` (first 6.x line, breaking)

## Motivation

A full engine audit (5 parallel subsystem sweeps) found the engine is structurally
clean (layering enforced, input rule held, only 2 hack markers, both correctly-parked
Metal clip-space TODOs). The real inefficiency is **cross-cutting duplication of
primitives**, rooted in one structural gap: there is no zero-dependency leaf package
that the low layers (Gpu, Particles, Render2D/3D, Ecs, Audio, Content) can all share.
So primitives get reimplemented per package.

Concrete duplications the audit confirmed (corroborated by two independent agents):

- **Color** has three representations: typed `Render2D/Color.cs`, bare `Vector4`-as-color
  everywhere below Render2D, and `Content/ColorHex.cs` (parses hex into `Vector4`, no
  interop with the typed `Color`). The typed `Color` was added *specifically* to kill the
  bare-`Vector4` foot-gun, but the core `SpriteBatch`/`PrimitiveRenderer`/`TextHelper`
  draw calls still take `Vector4`, so the foot-gun is still live.
- **RNG** has three xorshift implementations plus a fourth inline one and a
  non-deterministic `System.Random`: `Ecs/DeterministicRng` (xorshift128+, resumable,
  best of the lot), `Particles/Xorshift32` (struct), `Audio/AudioSystem` (`System.Random`,
  non-reproducible), and an inline xorshift in `Audio/WavSynth.cs:69`.
- **PNG decode** is the same StbImageSharp call copy-pasted in 3 spots
  (`Render2D/ImageRgba.cs:49`, `Render2D/Internal/Render2DCore.cs:33`,
  `Render3D/Scene3D.cs:118`); Render3D re-imports Stb to repeat what `ImageRgba` already does.
- **Math helpers**: `Clamp01` is private in `Render2D/Easing.cs`, `Lerp` is re-rolled in
  `Particles/ParticleSystem.cs:201`, and the aspect-fit `MathF.Min(w/W, h/H)` formula is
  open-coded in 5 places (`AppWindow.cs:131`, `Camera2D.cs:77`, `CameraFraming.cs:57`,
  `Scene3D.cs:335`, `IsoCamera3D.cs:82`).

The audit also surfaced two correctness bugs-in-waiting and one dead package, folded into
this batch since they are cheap and related to the same "use shared code properly" theme.

## Scope

In scope (one version bump, `6.0.0`):

1. New `KhaozEngine.Primitives` leaf package.
2. Breaking Color migration onto the shared `Color` type.
3. Image-decode dedup through `ImageRgba.Decode`.
4. Two correctness fixes (FileSettingsStorage read options; ECS save versioning + stable
   component ids).
5. Pooling wired into ECS allocations.
6. Audio onto the shared deterministic RNG.

Out of scope (follow-on, separate repos, after 6.0.0 ships and they pin it):

- Hardpoint (currently 5.70.0) Color-migration adopt PR.
- Nullwake (currently 5.59.0) Color-migration adopt PR.
- SpaceGame is on legacy 4.x MonoGame packages, unaffected.

Explicitly NOT in this batch (noted by the audit, deferred):

- Render3D `LineRenderer`/`FillRenderer`/`BillboardRenderer` -> generic `OverlayRenderer<TVertex>`.
- `Render3DPreview.Capture` per-frame `WaitForIdle()` stall.
- Music streaming per-frame `new uint[1]` allocation.
- Metal clip-space-Y flip TODOs (correct on Metal today; for the Windows/Linux port).
- Device-lost / device-removed handling.

## Component design

### 1. `KhaozEngine.Primitives` (new leaf)

Zero engine dependencies. Only `System.Numerics`. Becomes the lowest node in the dependency
graph; no cycles because nothing it references is an engine package. Packable, versioned
via `$(KhaozEngine5xVersion)` like every other project.

Contents:

- **`Color`** — relocated from `Render2D/Color.cs` (namespace changes; this is part of the
  breaking surface). Keeps R/G/B/A floats, `FromBytes`, `WithAlpha`, `White`/`Black`/`Transparent`.
  Gains `FromHex(string)` / `ToHex()` absorbing the logic from `Content/ColorHex.cs`. The two
  clamp-to-byte helpers (`ColorHex.C(v)` and `Color.FromBytes`) collapse into one.
- **`DeterministicRng`** (class) — relocated from `Ecs/DeterministicRng.cs`. Reference
  semantics retained because Ecs and Audio share and advance a single instance. Keeps
  `State` save/resume, `CreateDerived(name)`, `Next`/`NextDouble`. Gains `NextFloat()` and
  `Range(min,max)` (int and float overloads, ported from `Xorshift32`). `StableHash` (DJB2)
  promoted from private to public static.
- **`XorRng`** (struct) — the fast value-type xorshift promoted from `Particles/Xorshift32.cs`.
  Allocation-free for hot paths (particle emission, WavSynth noise). Has `NextFloat()`,
  `Range(min,max)`. Snapshot = copy the struct.
- **`MathUtil`** (static) — `Clamp01(float)`, `Lerp(a,b,t)`, `InverseLerp(a,b,v)`.
- **`ViewportMath`** (static) — `Fit(srcW,srcH,dstW,dstH)` (uniform scale that fits source
  inside dest, aspect preserved) and `Cover(...)` (aspect-cover variant). Replaces the
  open-coded `MathF.Min` formula at the 5 sites.
- **`Easing`** — relocated from `Render2D/Easing.cs` (pure math, broadly useful). `Clamp01`
  delegates to `MathUtil`.

Two RNG types is deliberate (decision: option-available, avoids a struct-copy footgun if a
shared `DeterministicRng` instance were passed by value). Each is implemented and tested once.

### 2. Breaking Color migration

`Color` becomes the parameter type, replacing `Vector4`-as-color, on:

- `Render2D`: `SpriteBatch` draw calls (`SpriteBatch.cs:200-211,232,268`),
  `PrimitiveRenderer` (`:58-151`), `TextHelper` (`:47-97`). The thin `Color` overloads that
  forward to `Vector4` today become the only signatures.
- `Render3D`: `Material.Emissive`, `Palette.Colors`, `ModelRenderer`/`PixelPostProcess`
  color params.
- `Particles`: `Particle.Color`, `EmitterConfig.Start/EndColor`.
- `Gpu`: `IGpuCommandList.ClearColorTarget(uint, Color)`.

Each project that uses a migrated type (`Gpu`, `Render2D`, `Render3D`, `Particles`, `Content`,
and `Effects` only if it references `Color`) adds a `ProjectReference` to `Primitives`; the
implementation plan confirms the per-project usage before adding the ref. `Content.ColorHex`
is deleted; the color JSON converter switches to `Color`.

Rendering output MUST stay byte-identical: the `Color` -> internal float conversion must
produce the same values that the `Vector4` path produced. Verified by the existing golden
snapshots run with `KE_GPU_TESTS=1`.

### 3. Image-decode dedup

`Render3D` already references `Render2D`, so:

- `Render3D/Scene3D.cs:118` and `Render2D/Internal/Render2DCore.cs:33` call `ImageRgba.Decode`
  (the single intended helper) instead of `ImageResult.FromMemory(...)` directly.
- Render3D drops its direct StbImageSharp usage; the dependency stays only where the decoder
  lives (Render2D).
- The duplicated "create R8G8B8A8 Sampled texture + UpdateTexture" block
  (`Render2DCore.cs:23-29`, `Scene3D.cs:125-132`) is consolidated into one helper next to the
  decoder.

### 4. Correctness fixes

- **`Persistence/FileSettingsStorage.cs:48`**: change `JsonSerializer.Deserialize<T>(json)`
  (no options) to read with `JsonDefaults.TolerantRead`, matching its own write
  (`:35`, `JsonDefaults.IndentedWrite`) and its sibling `GameStorage.Load`. Fixes the
  read/write asymmetry where a human-edited settings file with a comment or trailing comma
  loads through one path and throws through the other.
- **`Ecs/WorldSerializer`**:
  - `Load` reads `FormatVersion` (currently written at `:117` but never read), and throws a
    clear, typed error on an unknown *future* version instead of mis-deserializing.
  - Add a migration-dispatch seam (a registered handler keyed by from-version) so an older
    save can be upgraded on load.
  - Add an optional `[ComponentId("stable-key")]` attribute. When present, components serialize
    under the stable key instead of `Type.FullName` (`:70,89`), so renaming or moving a
    component struct no longer breaks existing saves. Absent the attribute, behavior is
    unchanged (`Type.FullName`), so this is additive for current saves.

### 5. Pooling wired into ECS

`KhaozEngine.Pooling.ObjectPool` (currently referenced by nothing but its own test) is wired
into ECS's per-call allocations:

- `EntityCommandBuffer.cs:40` per-playback `new Dictionary<int,Entity>()` -> pooled, cleared
  and returned after playback.
- Query result list allocation -> pooled where the lifetime is clearly scoped.

A headless test asserts the pool actually reuses instances (allocation count does not grow
across repeated playbacks). `Ecs` adds a `ProjectReference` to `Pooling`.

### 6. Audio onto shared deterministic RNG

`Audio/AudioSystem` replaces its `new System.Random()` field (`:29`) with the shared
`DeterministicRng`, injected via the existing `SetRng` seam (`:138`, signature updated to take
`DeterministicRng`). `WavSynth.cs:69`'s inline xorshift uses `XorRng`. `Audio` adds a
`ProjectReference` to `Primitives`.

Behavior note: the random-track *sequence* changes (different algorithm than `System.Random`).
The track-*set* semantics from 5.71.0 (`SetRotationPool`, null = all) are unaffected. Called
out in the CHANGELOG.

## Dependency graph after the change

```
Primitives  (new, zero engine deps, System.Numerics only)
  ^  ^  ^  ^  ^  ^
  |  |  |  |  |  |
 Gpu Render2D Render3D Particles Effects Content Ecs Audio  (all add ref to Primitives)
```

`Ecs` keeps its `Serialization` ref and adds `Primitives` + `Pooling`. `Audio` keeps its
`Diagnostics` ref and adds `Primitives`. `Foundation` umbrella adds `Primitives` so
foundation-only consumers get Color/RNG/math; `Game2D`/`Game3D` get it transitively.

## Error handling

- `Color.FromHex` throws a clear `FormatException` on malformed hex (same contract `ColorHex`
  has today).
- `WorldSerializer.Load` throws a typed, descriptive exception on unknown future
  `FormatVersion` and on an unknown component key with no migration registered (currently it
  silently mis-deserializes / throws a bare "Unknown component type").
- `ObjectPool` use in ECS must guarantee return-on-all-paths (try/finally around playback) so
  a throwing system does not leak a pooled instance.

## Testing

Every new behavior ships a headless test (`KhaozEngine.Tests`):

- `Color`: `FromHex`/`ToHex` round-trip, `FromBytes` clamp, alpha helpers.
- `DeterministicRng`: same-seed determinism, `State` save/restore reproduces the stream,
  `CreateDerived` independence, `NextFloat`/`Range` bounds, `StableHash` platform-stable value.
- `XorRng`: determinism, copy-is-snapshot, `Range`/`NextFloat` bounds.
- `MathUtil`/`ViewportMath`: `Clamp01`/`Lerp`/`InverseLerp` edges, `Fit`/`Cover` against the
  known results at the 5 former sites.
- ECS migration: load a v1 save, confirm round-trip; load an unknown future version, confirm
  the typed throw; `[ComponentId]` rename survives load.
- Pooling: repeated `EntityCommandBuffer` playback does not grow allocations (reuse asserted).
- Audio: given a seeded `DeterministicRng`, `PlayRandomTrack` sequence is reproducible.
- Image decode: the three former decode sites produce identical pixels for the same file.

Golden render snapshots (`KE_GPU_TESTS=1`) must stay green after the Color migration
(byte-identical rendering is a hard requirement, not a nice-to-have).

## Release ritual (per CLAUDE.md)

In order: bump `<KhaozEngine5xVersion>` in `Directory.Build.props` (5.71.0 -> 6.0.0) ->
CHANGELOG.md newest-first entry (breaking Color, new Primitives package, RNG consolidation,
the fixes) -> update the three doc-version declarations the guard checks
(`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version",
`README.md` `<PackageReference>` example) -> add `Primitives` to `docs/CONSUMERS.md` package
list -> `dotnet pack -c Release -o ./local-feed` (cumulative) -> commit -> `git tag v6.0.0` ->
push `main` + tag (CI publishes on `v*`). `scripts/check-doc-versions.sh` must pass.

This is the first `6.x` tag; the breaking Color migration is what takes the line to a major.

## Open questions

None. Design decisions resolved:
- Two RNG types (`DeterministicRng` class + `XorRng` struct): yes.
- `[ComponentId]` stable-key attribute: include now.
- Color migration: breaking (major bump).
- Batch scope: all six items.
