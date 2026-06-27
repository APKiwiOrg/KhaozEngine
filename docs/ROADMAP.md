# KhaozEngine roadmap

Future work only: what's planned or missing, highest-priority first. This file does NOT record shipped
history. See [CHANGELOG.md](../CHANGELOG.md) and `git tag` for what landed and when. When an item ships,
delete it from here (the detail moves to the changelog) rather than marking it "done".

Current released version: **7.56.0** (the shared `<KhaozEngineVersion>` line in `Directory.Build.props`).

Each near-term item gets its own design spec + plan under `docs/superpowers/` when it is scheduled.

## Near-term (next up)

### 1. Database deploy consistency (DACPAC)

Persistence today is `KhaozEngine.WorldStore` with two backends: `WorldStore.Sqlite` (dev/test, single-node)
and `WorldStore.SqlServer` (prod, Azure SQL). Both bootstrap their schema imperatively on construction (one
`world_store(key, data, updated_at)` table, dialect upsert). That works for a single-table KV store, but there
is no declarative, versioned schema-deploy artifact. Ruinborne (the SQL Server MMO consumer) needs a managed,
repeatable prod DB deploy and has none; the other DB-backed projects already use a DACPAC.

Goal: a canonical DACPAC / SSDT schema-deploy story for the SQL Server backend so prod deploys are declarative,
diffable, and versioned, while the SQLite path stays in schema parity for dev/test (one schema, two dialects).

Open decisions for the spec:
- Engine-first vs per-game: does the engine ship the canonical schema as a `KhaozEngine.WorldStore.SqlServer`
  SSDT/DACPAC project (every game gets the same deploy), or does each game own a DACPAC over a documented engine
  schema? Persistence/schema is a default-centralize domain, so the engine-shipped DACPAC is the likely call,
  but it needs sign-off when scheduled.
- Single source of truth: keep the declarative schema (DACPAC) and the imperative bootstrap in sync, by either
  generating one from the other or making the DACPAC authoritative and reducing the bootstrap to a guard/assert.
- Cross-repo: Ruinborne is the immediate consumer that adopts this once it lands.

### 2. Engine-level Discord social SDK

Discord integration (rich presence, join/invite, lobbies, OAuth identity) is currently done bespoke per game.
Centralize it: a `KhaozEngine.Social` seam (`ISocialProvider`: presence, invites/friends, identity, optionally
achievements) with a `KhaozEngine.Social.Discord` implementation over the Discord Social SDK, so every game gets
Discord from the engine. Retire the game-specific Discord implementations once the package ships.

- Design the seam for extensibility: the provider interface should leave room for Steam / other platforms later
  behind the same contract.
- Native dependency: the Discord SDK ships native libraries, so the `.Discord` package pulls them while the
  `Social` core seam stays dependency-free, mirroring the opt-in-backend pattern (`Netcode.LiteNetLib`,
  `WorldStore.*`). Per-RID bundling is the same concern as Audio/Windowing.

### 3. Physics engine

Movement today is a kinematic character controller, not a physics engine: `CharacterMovement.Step` does gravity
+ jump (coyote/buffer/terminal-clamp), ground-clamp onto a height delegate, and capsule push-out versus static
prop/building colliders (`KhaozEngine.Collision`), all XZ-plane and authoritative. It has no notion of arbitrary
geometry, so the world cannot answer the questions gameplay needs: standing on / jumping onto props and
buildings, building interiors (floors/walls/stairs), what is a ledge, where a character can jump up, where a
jump lands, line-of-sight.

Goal: a real physics layer (likely a render-free, server-authoritative `KhaozEngine.Physics` package the
character controller and collision layers fold into) with dynamic bodies and collision queries
(raycast/sweep/overlap) for ledge detection, jump targeting, and AI. It must run headless on the authoritative
server, deterministically, and match client prediction: the same authoritative + predicted contract the movement
stack already holds.

Key decision, explicitly do we need C/C++:
- Managed (e.g. BepuPhysics v2): pure .NET, no native libs, keeps the MonoGame-free / .NET-everywhere thesis
  intact (iOS AOT, no per-RID native bundling), good perf, broadly deterministic. The post-MonoGame pivot chose
  .NET ("no C++") for exactly these reasons.
- Native (e.g. Jolt or Bullet via bindings): more mature/complete and potentially faster at MMO scale, but
  reintroduces the per-RID native-bundling + AOT-compat burden the engine spent effort eliminating, and
  cross-platform determinism is harder.

Plan: a design spike + spec that prototypes both against the server-determinism requirement and the iOS/AOT
constraint, then commits to one. This supersedes the "out of scope" items the recent movement releases deferred
(standing on props/buildings, interiors/ledges, step-height, double/wall-jump, climbing, swimming, fall damage).

## Netcode / MMO refinements

- Delta + AoI unification: fold the interest-grid filtering into the delta encoder (one pass instead of two).
- Delta bit-packing / quantization: shrink per-snapshot bandwidth.
- SpaceGame as the first netcode adopter / testbed: validate the authoritative stack on a real game.

## Overworld / world content

- Procedural dungeon generator.
- Animated characters / creatures: needs a glTF animation-clip-playback feature first (also unblocks the
  SpaceGame 2.5D rigged-creature direction and pairs with the physics work).
- Per-cell world-state snapshot persistence: persist cell/world state, not just player records (pairs with
  sharding).
- PBR splat textures + a water shader: terrain-material upgrades on the chunk mesher's already-plumbed splat
  weights.

## Rendering

- Shadows: 3D shadow rendering (shadow maps). A named gap from the 5.x engine audit; the MRT / depth
  infrastructure already exists.
- Depth-sorted transparency (3D): transparent meshes and billboards currently render unsorted, so overlapping
  alpha is draw-order dependent. Sort back-to-front (or add order-independent transparency) for correct blending.

## Cross-platform reach

- Mobile: iOS / Android platform layers (lifecycle, touch, packaging, store submission). Silk.NET is the
  windowing/input foundation it builds on.
- GL backend verification: the `gl` Veldrid backend override parses but is unverified; waits on real GPU CI /
  hardware. See [CROSS-PLATFORM.md](CROSS-PLATFORM.md).
- Live gamepad smoke: polling is best-effort + compile-verified; needs an on-device pass with a physical
  controller.
- Richer text entry: IME / locale / dead-keys (the current `TextEntry` is US-layout key-mapping).

## Tooling & developer experience

- On-screen profiling / diagnostics overlay: a live frame-time / draw-call / memory overlay (logging only
  today). The Gui makes it cheap to build.
- Asset hot-reload: reload meshes, textures, and shaders at runtime during development. The prop asset pipeline
  shipped (7.46.0); hot-reload did not.

## Possible future factoring (unscheduled)

- Shared 2D/3D particle sim core: `KhaozEngine.Particles` (3D) and `Render2D.Vfx.Particle2DSystem` (2D) share
  the emit/integrate/lerp-over-life model; a pass could factor the common sim core. No single target today.
- Nullwake camera convergence: Nullwake's `OreField.RefToScreen` is a non-uniform scale into a screen sub-rect,
  not `Camera2D`. Converging would need sub-viewport + non-uniform-scale support in the engine camera, else it
  stays game-specific (see [CONSUMERS.md](CONSUMERS.md)).
