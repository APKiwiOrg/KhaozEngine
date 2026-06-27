# KhaozEngine

Shared, game-agnostic engine - a custom MonoGame-free 2D/3D render + windowing/input + Gui + ECS + netcode
stack (Hardpoint, Nullwake, SpaceGame, Ruinborne all run on it). See README.md and docs/USING-KHAOZENGINE.md.

## Before starting ANY engine work (concurrent-dev rule)
This section is the engine's instance of the global "Branching, worktrees, and finishing work"
default (worktree per change; finish by merge to `main` + commit + push). It wins where it differs:
heavy parallel dev makes the worktree mandatory (with the trivial-change exception below), and a
finished release is a full publish (merge + push `main` + push the `vX.Y.Z` tag + pack to
`local-feed`). **The publish (push + tag) is routinely HELD and BATCHED, and confirmed with the
user before pushing:** CI publishes every package to GitHub Packages on each `v*` tag, so related
work is committed + packed locally across several version bumps and pushed together, not
per-feature (the merge to `main` happens as work finishes; only the push/tag is batched). There
is a lot of parallel development on this engine.
Before you touch anything:
1. Check for ongoing parallel work first: `git worktree list`, `git branch -a`,
   and `git fetch && git status` to see other branches/trees in flight.
2. If your change fits an existing branch/worktree, work there.
3. If it does not fit any of them, create a NEW worktree (do not start work
   loose on `main` or pile onto an unrelated branch). Isolate the change in its
   own tree so concurrent work does not collide.

This applies to every change with one exception below: code, tests, docs, and
version/release work.

- **How to create the tree:** prefer the native `EnterWorktree` tool, not
  `git worktree add`. The native tool is what the parallel-dev workflow expects.
  **But this repo holds/batches pushes, so local `main` is routinely ahead of
  `origin/main`, and `EnterWorktree` branches from `origin/<default-branch>` by
  default - a tree it makes then silently lacks your unpushed commits.** If the
  change builds on unpushed local work (e.g. the next layer atop a just-merged but
  unpushed one), create the tree from local HEAD instead:
  `git worktree add .claude/worktrees/<name> -b worktree-<name> main`, then
  `EnterWorktree` with its `path` to switch in.
- **Branch / tree naming:** `feature/<short-name>` for new features, `fix/<short-name>`
  for bug fixes, `<batchN>-promote` for game-code-into-engine promotion batches
  (e.g. `batch1-promote`). Keep the worktree directory name matching the branch.
- **Trivial-change exception:** a self-contained edit that ships no package
  (a doc typo, a comment, a CLAUDE.md/governance tweak, a one-line non-API fix
  with no version bump) may be made directly on a clean `main` without a worktree,
  as long as the parallel-work check in step 1 comes back clean. Anything that
  touches public API, tests, or triggers the release ritual still needs a tree.

## Rules
- `AppWindow` (KhaozEngine.Windowing) is the ONLY class that touches the Silk.NET/GLFW input
  statics. Everything else reads the immutable `InputState` snapshot (handed in via `Frame.Input`)
  through `InputManager`/`Pointer` - keeps input headless-testable. (There is no `MonoGameRawInput`
  or `IRawInput` any more; the engine is MonoGame-free.)
- New behaviour ships with a headless test in `KhaozEngine.Tests` (construct an `InputState`
  frame-by-frame and feed `InputManager.Update(input, viewport?)`; `dt` is a plain `float` in seconds,
  no `GameTime`).
- Hit-test via `InputManager`/`Pointer` bounds helpers (`IsTapIn`, etc.), never raw position + button.

## Build / test / release
- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` - every new behaviour ships with a headless test.
- **Always update BOTH `CHANGELOG.md` AND `CHANGENOTES.md` on every version bump.** `CHANGELOG.md` gets the
  newest-first detailed entry (public API / behaviour change); `CHANGENOTES.md` gets a newest-first one-or-two
  sentence digest line (the high-level "history over time" view). Both go in the SAME commit as the
  `Directory.Build.props` version bump. Never bump the version (or tag a release) without both.
- Release ritual, in order: bump `<KhaozEngine5xVersion>` in `Directory.Build.props` → add the
  `CHANGELOG.md` entry → add the one-line `CHANGENOTES.md` entry → update the engine-version declarations the
  guard checks (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", and
  the `README.md` `<PackageReference>` example) → `dotnet pack -c Release -o ./local-feed` (cumulative within a
  release) → commit → `git tag vX.Y.Z` → push `main` + the tag (CI publishes to GitHub Packages on `v*`).
  `local-feed/` is a gitignored dev convenience; GitHub Packages (every published `v*`) is the durable store, so
  `local-feed` may be pruned up to the lowest version any consumer still pins (see `docs/CONSUMERS.md`; do not prune
  below it) without losing anything recoverable.
- **Full doc sweep on EVERY feature / bug / change - not just the guard-checked declarations.**
  `check-doc-versions.sh` only verifies the 3 version strings; it does NOT catch package/feature docs drifting,
  so those silently rot (7.34.0 shipped with the `README.md` package table and this `CLAUDE.md` package map both
  missing the two new packages). After ANY change, sweep ALL docs that could reference what you touched and update
  every one. When a package is ADDED/REMOVED: the `README.md` package-catalog table + the repo-layout block, this
  `CLAUDE.md` package enumeration (the `<KhaozEngine5xVersion>` list above) + the umbrella descriptions,
  `docs/CONSUMERS.md` (the umbrella/package table), and `docs/USING-KHAOZENGINE.md` (a usage section for new public
  API). For a behaviour/bug change: `CHANGELOG.md` + `CHANGENOTES.md` AND any doc, README, or code comment that
  described the OLD behaviour. Mechanical check before committing: grep the new (or removed) type / package / flag
  name across `*.md` + `CLAUDE.md` and confirm every place that should mention it does (and no stale doc still
  describes what you removed).
- `scripts/check-doc-versions.sh` enforces those three declarations match the **5.x line**
  (`<KhaozEngine5xVersion>`, which is the engine); CI runs it on every push, so a forgotten bump fails the
  build. Consumer pins are exempt and may lag.
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **One shared version line - the engine is entirely MonoGame-free.** `Directory.Build.props` carries a single
  `<KhaozEngine5xVersion>` governing the WHOLE engine; every packable project sets
  `<Version>$(KhaozEngine5xVersion)</Version>` in its csproj, so one bump releases all packages together (repack to
  `local-feed`, single tag `vX.Y.Z`). `check-doc-versions.sh` enforces this line. **Per-version history lives in
  `CHANGELOG.md` / `CHANGENOTES.md`, not here** - below is the durable package catalog only:
  - **Leaves (minimal deps):** `Primitives` (`Color`/`DeterministicRng`/`XorRng`/`MathUtil`/`ViewportMath`/`Easing`,
    zero-dependency), `Imaging` (`PngWriter`, the dependency-free RGBA8 PNG encoder; `Render2D.Png` shims it),
    `Determinism` (`DeterministicFp`/`DeterministicFpScope`, the CPU FP-environment pin for fixed-tick/lockstep sims;
    in the `Foundation` umbrella).
  - **Render/runtime stack:** `Gpu`, `Windowing`, `Render2D`, `Render3D`, `Gui`, `Audio`, `Particles`, `Effects`,
    `Game`, `Game.Render3D`.
  - **Foundation (MonoGame-free):** `Ecs`, `Serialization`, `Content`, `Diagnostics`, `App`, `Localization`,
    `Locomotion`, `Persistence`, `Pooling`, `Platform`, `Updates`, `Collision`, `Terrain`, `Netcode`,
    `Netcode.Abstractions`, `Netcode.LiteNetLib`, `Simulation`, `Replication`, `WorldStore`, `WorldStore.Sqlite`,
    `WorldStore.SqlServer`, `Sharding`, `NetWorld`.
  - **Server / parallel-job core types:** `Simulation` = `FixedTickHost` + the `IJobScheduler` worker-pool seam
    (`SingleThreadedJobScheduler` inline default + `ThreadPoolJobScheduler`); `Netcode` =
    `INetTransport`/`LoopbackTransport` + the `NetServer`/`NetClient` session layer; `Replication` = authoritative ECS
    replication (`NetId`/`ReplicationRegistry`/`SnapshotWriter`/`ClientReplicationView`/`ServerReplicator` +
    `InterestGrid` AoI); `WorldStore` = `IWorldStore` async keyed-blob seam + `InMemoryWorldStore` (dep-free core),
    with two opt-in durable backends each pulling their own ADO.NET provider (same `Netcode.LiteNetLib` pattern):
    `WorldStore.Sqlite` = `SqliteWorldStore` over Microsoft.Data.Sqlite (dev/test + single-node, always-tested) and
    `WorldStore.SqlServer` = `SqlServerWorldStore` over Microsoft.Data.SqlClient (prod = Azure SQL); both = one
    `world_store(key,data,updated_at)` table, schema bootstrap on construction, dialect upsert (SQLite ON CONFLICT /
    SQL Server MERGE HOLDLOCK), raw parameterized async ADO.NET, no EF/ORM. The save/load orchestration is
    `NetWorld.WorldPersistence` (+ `PlayerRecord`), wiring `IWorldStore` into the server connect/disconnect/tick
    lifecycle via `IWorldPersistenceHost` (both `WorldServer` and `ShardedWorldServer`; load-on-join / save-on-leave /
    periodic dirty snapshot), backend-agnostic. `Sharding` = the in-process
    cell-grid topology (`CellCoord`/`CellSim`/`ShardHost`; a cell = an ECS `World` + `FixedTickHost` +
    `ServerReplicator` + `InterestGrid`) with cross-cell ghosting / exactly-once handoff / home-cell AoI serving over
    the `ICellLink` seam, plus the `MmoServerSample` toy-2D reference server (`IsPackable=false`); the overworld
    movement stack runs on it via `NetWorld.ShardedWorldServer` (6b). `Ecs` also has opt-in
    data-parallel `World.ParallelForEach` + the `AccessSet` access-declaration model + the `ParallelHazardChecks`
    guard, so it depends on `Simulation` (acyclic - `Simulation` is a zero-dependency leaf). The parallel-job-system
    program (cells + entities axes shipped; the system-scheduler layer was benchmark-gated and de-scoped) is
    `docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`; its benchmark is the `KhaozEngine.Benchmarks`
    project (no package; `--gate` runs the system-scheduler gate).
  - **Snapshot + telegraph libs:** `Snapshot` (2D `SnapshotRunner`/`SnapshotHost`) + `Snapshot.Render3D` (`Shot3D`),
    in NO umbrella (a game's snapshot tool refs them directly); `Telegraphs`
    (`TelegraphStyle`/`TelegraphResolve`/`TelegraphRenderer2D`, in `Game2D`) + `Telegraphs.Render3D` (the `Scene3D`
    `Ground*` extensions over `Render3D.DrawGroundDecal`, in `Game3D`).
  - **Terrain libs (render-free leaf + companion):** `Terrain` (render-free leaf, in `Foundation`) = the analytic
    `TerrainField` (`SampleHeight`/`SampleNormal`/`SampleBiome`/`WaterLevel`) folding biome-band shaping + stateless
    coordinate-hash fractal noise (`TerrainNoise`) + ordered `ITerrainFeature`s (`LakeFeature`/`RidgeFeature`/
    `FlattenFeature`/`RimFeature` (+ `RimPass`) - the bounded-zone enclosing wall, a smoothstep ramp to a jagged
    crest with road-out corridors, circular MVP shaped around a "distance to the play-area boundary"), plus
    `TerrainCollision` (`GroundHeight`/`GroundNormal` (the slope-gate delegate)/`IsWalkable`),
    `PropColliders.FromScatter` (deps `Collision`: builds a `Collision.WorldColliders` from deterministic scatter
    placements - footprint per prop id with a default-shape fallback - plus an explicit obstacle/building list;
    streaming-consistent, same coordinate-hash as the rendered scatter), and
    `TerrainPresets.Clearing()`/`BoundedClearing()`. Height depends only on `(x,z,seed)`
    (load-order independent for sharded streaming); plain `float` (authoritative server + visual client, NOT
    `DeterministicFp`). `Terrain.Render3D` (companion, in `Game3D`) = `TerrainChunkBuilder` (chunked-LOD mesh off the
    field: skirts, per-vertex splat weights plumbed for the later PBR upgrade, height/slope vertex-colour ramp,
    chunk AABB) + `TerrainLod.PickLod` + `Scene3D.LoadTerrainChunk`/`DrawTerrainChunk`, plus the `TerrainStreamer`
    client world-streaming layer (`ChunkCoord`/`ChunkGrid` coord<->world, `IChunkSink` load/unload seam,
    `StreamerConfig`, and the production `Scene3DChunkSink` that builds the chunk mesh + scatters props on load,
    re-LODs on tier crossing, frees on unload, and draws the loaded ring; ring load/unload with a hysteresis band,
    distance-LOD re-meshing, amortized main-thread loading). First overworld render-scale sub-project
    (`docs/superpowers/specs/2026-06-27-terrain-system-design.md`); world streaming is sub-project 6a
    (`docs/superpowers/specs/2026-06-27-world-streaming-design.md`); multi-cell server sharding (6b), PBR-textures,
    and water are later sub-projects.
  - **Static-world collision (in `Collision`):** `BoxCollision` (circle-vs-AABB / oriented-box / circle
    minimum-translation push-out), `ColliderShape` (unplaced cylinder/box prop footprint), `WorldCollider` (one
    placed static collider), `WorldColliders` (a `SpatialHashGrid`-backed queryable set: `Query(x,z,radius)` +
    an iterate-and-slide `Resolve` that pushes a capsule footprint out of overlaps, slide along surfaces).
    Render-free, kinematic, XZ-plane, authoritative (NOT a physics engine). `Collision` stays a `System.Numerics`
    leaf; `Locomotion`/`NetWorld`/`Terrain`/`Render3D` reference it (acyclic). `Render3D.AssetEntry` carries an
    optional `ColliderShape Collider` (manifest `"collider": { "type": "cylinder"|"box", ... }`); when omitted,
    `Render3D.PropFootprint.Derive(GltfMesh)` sizes the collider from the actual mesh (short prop = full footprint,
    tall prop = bottom trunk slice so a tree's canopy isn't solid; cylinder or oriented box by aspect ratio),
    `PropFootprint.DeriveAll(AssetManifest)` building the `id -> ColliderShape` lookup `PropColliders.FromScatter` takes.
  - **Locomotion + networked-world libs (render-free movement + the netcode wiring):** `Locomotion` (leaf, in
    `Foundation`, deps Primitives + Collision) = `CharacterMovement.Step` (pure XZ move from a `MoveCommand` + ground
    delegate + one `MoveTuning`, ground-clamped + slope gate - the slope gate runs only when a `groundNormal`
    delegate is passed, e.g. `TerrainCollision.GroundNormal`, so the rim wall can't be climbed - plus optional
    static-world collision via a nullable `WorldColliders`: the capsule footprint (`MoveTuning.CapsuleRadius`,
    default 0.4) is pushed out of props/buildings, null/empty = unchanged) shared by the local
    `CharacterController3D` (Game.Render3D wraps it), the server sim, and client prediction. `NetWorld` (in
    `Server`, deps Locomotion/**Collision**/Netcode/Replication/Ecs/Serialization/WorldStore/**Sharding**) = `WorldBounds`
    (abstract `Contains`/`Clamp` + `CircleBounds`/`RectBounds`, the authoritative play-area shape; `Clamp` =
    nearest in-bounds point, idempotent inside + clamp-and-slide outside) wired as a nullable bound through the
    movement step (off = unbounded, unchanged), `PlayerMoveSimulator` (`ITickSimulator` over
    `CharacterMovement.Step`, clamps to `WorldBounds`; resolves the optional `WorldColliders` inside `Step` so the
    server is authoritative + identical to client prediction), `WorldServer` (single-`World` authoritative: per-player `RemoteCommandQueue` +
    ground-clamped sim + per-client AoI via `SnapshotWriter.WriteFiltered`+`InterestGrid`, framed `[localNetId][ack]`;
    a persistence seam = `PlayerJoined`/`PlayerLeaving` events + accountId-from-connect-token + `SetPlayerState`),
    `ShardedWorldServer` (+ `ShardedWorldServerConfig`, the multi-cell variant = the same movement stack run across a
    `Sharding.ShardHost` cell grid: routes each client's `MoveCommand` to the owning cell, steps each cell's
    `PlayerMovementSystem` (also clamps to the optional `WorldBounds`) via `ShardHost.Tick` scheduler-fanned, exactly-once handoff on boundary crossings -
    `NetId` stable - border ghosting, single home-cell AoI framed identically; `PendingMove` = the per-tick command
    a cell applies to an owned player, server-local + not replicated/migrated; the `WorldClient`/`MoveProtocol` are
    UNCHANGED), `WorldClient` (`NetClient`+`ClientReplicationView`+`ClientPrediction` -> `EntityRenderState[]`, local
    predicted/reconciled - prediction clamps to the same `WorldBounds` so reconciliation stays clean at the wall -
    remotes replicated), and `WorldPersistence` (+ `PlayerRecord`, a forward-tolerant JSON
    record): wires an `IWorldStore` into the server lifecycle via `IWorldPersistenceHost` (the seam `WorldServer` AND
    `ShardedWorldServer` both implement) - load-on-join / save-on-leave / periodic dirty snapshot, keys
    `player:{accountId}`, player-keyed + cell-agnostic (a loaded player spawns at its saved position in whatever cell
    contains it) - so the world survives a restart, backend-agnostic. `PlayerMoveState :
    IPredictedState` lives in NetWorld (not Locomotion) so the movement core + local controller stay netcode-free;
    `CharacterMovement.Step` takes/returns `Vector3`. Demos (`IsPackable=false`): `NetworkedWalkServer` (headless,
    multi-cell `ShardedWorldServer`, persists via `SqliteWorldStore`) + `NetworkedWalkSample` (windowed `--connect`
    client, sends a stable account token).
    Networked-overworld render-scale sub-project (`docs/superpowers/specs/2026-06-27-networked-overworld-design.md`);
    persistence sub-project (`docs/superpowers/specs/2026-06-27-persistent-worldstore-design.md`); multi-cell sharding
    sub-project 6b (`docs/superpowers/specs/2026-06-27-multicell-sharding-design.md`).
  - **Umbrellas (code-free metapackages):** `Foundation`, `Game2D`, `Game3D`, `Server`.
  - **Tools, same version line:** `Updates.Tool` (`ke-updater`: manifest/genkey/sign/verify) and `Sfx.Tool`
    (`ke-sfxbake`: ElevenLabs-driven SFX bake) are `PackAsTool`; `Content.Validator` is a build-time tool
    (`IsPackable=false`, shipped inside the `Content` package, not separately versioned).
  - **Gotchas / history:** the package id is `KhaozEngine.Sharding`, NOT `KhaozEngine.World` (a `World` leaf would
    shadow the ECS `World` type). The legacy 4.x MonoGame line + its six packages
    (`UI`/`Graphics`/`Screens`/`Sprites`/`Input`/`Time`) were DELETED - there is no 4.x line; all three consumers are
    on the 7.x line (pins in `docs/CONSUMERS.md`). The line dropped `-experimental` at `5.31.0`; the foundation
    graduated onto it at `5.46.0`. See `docs/ROADMAP.md` ("The post-MonoGame pivot").
- **Commit subjects:** conventional-commit style `area(scope): summary`, e.g.
  `audio(4.3.1): MacOsMusicBackend loads built .ogg` or `docs(consumers): ...`.
  On a release/version-bump commit, use the new version as the scope (`audio(4.3.1):`).
- **One version bump per batch, not per item.** When a worktree promotes several
  related items, commit each item individually but do the single `Directory.Build.props`
  bump + `CHANGELOG.md` entry + `dotnet pack` ONCE at the end of the batch, then do
  per-consumer adopt PRs. Never bump the version per-item within a batch.
- `local-feed/` is gitignored but MUST exist before `dotnet restore` (`mkdir -p local-feed`).
- net10.0, MonoGame-free: Silk.NET (windowing + input, GLFW natives bundled per-RID), Veldrid behind
  `KhaozEngine.Gpu` (GPU), Silk.NET.OpenAL (audio), xUnit (tests).
