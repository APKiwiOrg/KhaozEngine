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
- **Always add a `CHANGELOG.md` entry on every version bump.** Newest-first, detailed (public API / behaviour
  change), with a tight one-line summary as the entry's first sentence so the file doubles as the high-level
  "history over time" view. It goes in the SAME commit as the `Directory.Build.props` version bump. Never bump
  the version (or tag a release) without it. (There is no separate `CHANGENOTES.md` - it was folded into
  `CHANGELOG.md`; one file is the single source of truth.)
- Release ritual, in order: bump `<KhaozEngineVersion>` in `Directory.Build.props` → add the
  `CHANGELOG.md` entry → update the engine-version declarations the
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
  `CLAUDE.md` package enumeration (the `<KhaozEngineVersion>` list above) + the umbrella descriptions,
  `docs/CONSUMERS.md` (the umbrella/package table), and `docs/USING-KHAOZENGINE.md` (a usage section for new public
  API). For a behaviour/bug change: `CHANGELOG.md` AND any doc, README, or code comment that
  described the OLD behaviour. Mechanical check before committing: grep the new (or removed) type / package / flag
  name across `*.md` + `CLAUDE.md` and confirm every place that should mention it does (and no stale doc still
  describes what you removed).
- `scripts/check-doc-versions.sh` enforces those three declarations match the **engine version line**
  (`<KhaozEngineVersion>`); CI runs it on every push, so a forgotten bump fails the
  build. Consumer pins are exempt and may lag.
- **`docs/CONSUMERS.md` tracks which game pins which package version.** Update its version matrix
  whenever a consumer bumps a `KhaozEngine.*` `<PackageReference>`, and the engine-version line on
  every release. Refresh snippet is at the bottom of that file.
- SemVer: additive = minor, fixes = patch, breaking = major.
- **One shared version line - the engine is entirely MonoGame-free.** `Directory.Build.props` carries a single
  `<KhaozEngineVersion>` governing the WHOLE engine; every packable project sets
  `<Version>$(KhaozEngineVersion)</Version>` in its csproj, so one bump releases all packages together (repack to
  `local-feed`, single tag `vX.Y.Z`). `check-doc-versions.sh` enforces this line. **Per-version history lives in
  `CHANGELOG.md`, not here** - below is the durable package catalog only:
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
    `INetTransport`/`LoopbackTransport` + the `NetServer`/`NetClient` session layer (the `IConnectionAuthenticator`
    gate returns a verified `subject` on accept, surfaced as `ServerSessionEvent.Subject`; `AllowAllAuthenticator`
    = token-as-subject dev default, `SignedToken`/`HmacTokenAuthenticator` = zero-dep HMAC-SHA256 signed
    connect-token `v1.<subject>.<expUnix>.<base64url-mac>`); `Replication` = authoritative ECS
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
  - **Walkable prop/building surfaces (stand on / jump onto rocks + roofs, sub-project B on the 7.54.0 vertical
    physics):** in `Collision`, `PropSurface` (a unit-scale top-down max-height grid + bilinear `SampleLocal` +
    binary IO; single-valued top contour, no overhangs), `WorldSurface` (a placed surface, scale/yaw applied at
    query time), and `WorldSurfaces` (a `SpatialHashGrid` set whose `Query(x,z)` returns the max prop-top under
    you); plus `WorldCollider.Top` + a height-aware `WorldColliders.Resolve(position,radius,footY,WorldSurfaces?)`
    that blocks a side only while the feet are below the WALKABLE SURFACE (not the prop's single max `Top`): a
    collider is skipped when the capsule centre is already over its footprint (the vertical support/step-up places
    you, so a domed top is never shoved off mid-traverse) or, approaching from outside, once the feet clear the rim
    height where you step onto it (sampled inward from the footprint edge toward the player) - so a domed rock is
    standable AND mountable by walking/jumping up its side, while a flat-top prop (rim = `Top`) stays mountable only
    from above and a thin blocker (a tree: `Top = +inf`, no surface) always blocks. (History: gating on the peak `Top`
    alone shoved you off domed tops everywhere but the peak (fixed 7.56.1 by an explicit `surfaceTop` scalar - that
    overload remains); gating on the peak also made the side a wall up to peak height so you could only drop onto a
    rock from above (fixed 7.58.0 by this `WorldSurfaces` overload).)
    `Locomotion.MoveTuning` gains `StepHeight`, and the vertical `CharacterMovement.Step` takes an optional
    `WorldSurfaces?` (support = `max(terrain, surface)`, height-aware block, step-up; null = unchanged), threaded
    server-authoritative + predicted through `PlayerMoveSimulator`/`PlayerMovementSystem`/`WorldServer`/
    `ShardedWorldServer`/`CharacterController3D`. `Render3D.PropSurfaceBake` bakes the grid from a normalized mesh
    (+ `IsWalkableSolid` classification: rock/log/building -> surface, tree -> thin blocker, no surface);
    `Render3D.PropSurfaceLoader` reads the baked `.surf` render-free; `AssetEntry` gains `Surface` + `Heightmap`.
    `Terrain.PropSurfaces.FromScatter` builds the set (+ a top-aware `PropColliders.FromScatter` overload stamps
    each collider's top). The offline bake is the `ke-propbake` tool (folded into kit ingest; re-ingest = re-bake).
  - **Locomotion + networked-world libs (render-free movement + the netcode wiring):** `Locomotion` (leaf, in
    `Foundation`, deps Primitives + Collision) = `CharacterMovement.Step`, two overloads sharing one horizontal core
    (camera-relative move from a `MoveCommand` + ground delegate + one `MoveTuning`, slope gate - runs only when a
    `groundNormal` delegate is passed, e.g. `TerrainCollision.GroundNormal`, so the rim wall can't be climbed - plus
    optional static-world collision via a nullable `WorldColliders`: the capsule footprint
    (`MoveTuning.CapsuleRadius`, default 0.4) is pushed out of props/buildings, null/empty = unchanged): the original
    `Step(Vector3,...) -> Vector3` is horizontal-only (Y instant-clamped to ground + half-height), and
    `Step(in MoveState,...) -> MoveState` is the vertical-physics step (gravity terminal-clamped to `MaxFallSpeed`,
    land-and-clamp ground contact with a `GroundedEpsilon` slope skin, jump with coyote-time + jump-buffer and no
    apex double-jump, `AirControl`-scaled airborne XZ, plus an optional `clampXz` for the play-area bound). `MoveState`
    = the carried kinematic state (position + `VerticalVelocity` + `Grounded` + coyote/buffer timers); `MoveCommand`
    has a `Jump` bit; `MoveTuning` carries `Gravity`/`JumpSpeed`/`MaxFallSpeed`/`CoyoteTime`/`JumpBuffer`/`AirControl`/
    `GroundedEpsilon`. Shared by the local `CharacterController3D` (Game.Render3D wraps it, jumps on Space), the
    server sim, and client prediction. `NetWorld` (in
    `Server`, deps Locomotion/**Collision**/Netcode/Replication/Ecs/Serialization/WorldStore/**Sharding**) = `WorldBounds`
    (abstract `Contains`/`Clamp` + `CircleBounds`/`RectBounds`, the authoritative play-area shape; `Clamp` =
    nearest in-bounds point, idempotent inside + clamp-and-slide outside) wired as a nullable bound through the
    movement step (off = unbounded, unchanged), `PlayerMoveSimulator` (`ITickSimulator` over
    `CharacterMovement.Step`, clamps to `WorldBounds`; resolves the optional `WorldColliders` inside `Step` so the
    server is authoritative + identical to client prediction), `WorldServer` (single-`World` authoritative: per-player `RemoteCommandQueue` +
    ground-clamped sim + per-client AoI via `SnapshotWriter.WriteFiltered`+`InterestGrid`, framed `[localNetId][ack]`;
    a persistence seam = `PlayerJoined`/`PlayerLeaving` events + accountId-from-verified-subject (the
    authenticator's `subject`, `guest:{slot}` when empty) + an optional `IConnectionAuthenticator` ctor arg
    (default `AllowAllAuthenticator`) + `SetPlayerState`),
    `ShardedWorldServer` (+ `ShardedWorldServerConfig`, the multi-cell variant = the same movement stack run across a
    `Sharding.ShardHost` cell grid: routes each client's `MoveCommand` to the owning cell, steps each cell's
    `PlayerMovementSystem` (also clamps to the optional `WorldBounds`) via `ShardHost.Tick` scheduler-fanned, exactly-once handoff on boundary crossings -
    `NetId` stable - border ghosting, single home-cell AoI framed identically; `PendingMove` = the per-tick command
    a cell applies to an owned player, server-local + not replicated/migrated; the `WorldClient`/`MoveProtocol` are
    UNCHANGED), `WorldClient` (`NetClient`+`ClientReplicationView`+`ClientPrediction` -> `EntityRenderState[]`, local
    predicted/reconciled - prediction runs against the same optional `WorldBounds`/`WorldColliders`/`WorldSurfaces`
    as the server (trailing ctor params mirroring `WorldServer`, default null = terrain-only), so it predicts around
    solid props and clamps at the wall instead of rubber-banding, keeping reconciliation a no-op -
    remotes replicated), and `WorldPersistence` (+ `PlayerRecord`, a forward-tolerant JSON
    record): wires an `IWorldStore` into the server lifecycle via `IWorldPersistenceHost` (the seam `WorldServer` AND
    `ShardedWorldServer` both implement) - load-on-join / save-on-leave / periodic dirty snapshot, keys
    `player:{accountId}`, player-keyed + cell-agnostic (a loaded player spawns at its saved position in whatever cell
    contains it) - so the world survives a restart, backend-agnostic. `PlayerMoveState :
    IPredictedState` lives in NetWorld (not Locomotion) so the movement core + local controller stay netcode-free; it
    wraps a `Locomotion.MoveState` (exposes `Position` + `VerticalVelocity` + `Grounded`). The vertical axis rides
    the wire as a replicated `MovementState` component (type id 2, alongside `ReplicatedPosition`), so it survives a
    cell handoff and forms the client's exact reconcile basis: `WorldServer`/`ShardedWorldServer` write it (added at
    spawn), and `WorldClient` reconciles `y`/`VerticalVelocity`/`Grounded` alongside XZ (full basis rebased + unacked
    commands replay the same `Step`, so a jump in flight converges with no permanent desync). Demos
    (`IsPackable=false`): `NetworkedWalkServer` (headless,
    multi-cell `ShardedWorldServer`, persists via `SqliteWorldStore`) + `NetworkedWalkSample` (windowed `--connect`
    client, sends a stable account token).
    Networked-overworld render-scale sub-project (`docs/superpowers/specs/2026-06-27-networked-overworld-design.md`);
    persistence sub-project (`docs/superpowers/specs/2026-06-27-persistent-worldstore-design.md`); multi-cell sharding
    sub-project 6b (`docs/superpowers/specs/2026-06-27-multicell-sharding-design.md`).
  - **Animated characters (glTF clip playback + locomotion blend, 7.56.0):** the GPU-free animation layer in
    `Render3D` beside the rig/skinning - `GltfLoader.LoadAnimations(path)` reads SharpGLTF `LogicalAnimations` into
    `AnimationClip`s (per-joint TRS keyframe tracks - `JointTrack`/`Vector3Track`/`QuaternionTrack` keyed by glTF
    logical node index, `InterpolationMode` LINEAR/STEP, CUBICSPLINE reduced to its value keys), and
    `GltfLoader.LoadSkinned` now also attaches a `Skeleton` (topologically-ordered parent links + rest-local
    `JointPose` TRS + bone-to-node map + logical-node lookup) to `SkinnedGltfMesh` (new optional
    `SkinnedGltfMesh.Skeleton`; old constructors unchanged). `AnimationSampler` samples a clip -> per-node local
    poses -> composes the hierarchy into the joint-WORLD bone palette `Scene3D.DrawSkinned` consumes
    (`SamplePose`/`Compose`/`SampleToBonePalette`/`BlendPoses`/`Wrap`); `AnimationPlayer` advances + loops a clip and
    crossfades into a new one (blends the two clips' local TRS, composes once). In `Game.Render3D`: `LocomotionState`
    (Idle/Walk/Run/Jump/Fall) + `LocomotionThresholds` + `LocomotionStateMachine.Evaluate(speed,grounded,vVel,t)`
    (speed picks idle/walk/run; airborne wins - rising=Jump else Fall), and `AnimatedCharacter` (wraps a mesh
    `Skeleton` + per-state clips + `AnimationPlayer` + the SM; movement state + dt -> bone palette, driven the same
    for the LOCAL and REMOTE players). Client-cosmetic (picked from already-replicated movement; NO netcode/server
    animation). `TerrainWalkSample` walks a committed KayKit CC0 rigged+animated character (skinned-ingest preserves
    the rig + clips; NOT the flatten-prop path). Out of scope: animation events, root motion, IK, additive/facial
    layers, full blend trees, networked animation. Design:
    `docs/superpowers/specs/2026-06-27-animated-characters-design.md`.
  - **Umbrellas (code-free metapackages):** `Foundation`, `Game2D`, `Game3D`, `Server`.
  - **Tools, same version line:** `Updates.Tool` (`ke-updater`: manifest/genkey/sign/verify), `Sfx.Tool`
    (`ke-sfxbake`: ElevenLabs-driven SFX bake), and `PropSurface.Tool` (`ke-propbake`: bakes a walkable-surface
    `.surf` heightmap per walkable-solid prop in a kit manifest, folded into kit ingest) are `PackAsTool`;
    `Content.Validator` is a build-time tool (`IsPackable=false`, shipped inside the `Content` package, not
    separately versioned).
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
