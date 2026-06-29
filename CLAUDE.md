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
  API). When public API is ADDED/CHANGED WITHIN an existing package (no package add/remove): also update **that
  package's own `<Package>/README.md`** - the `PackageReadmeFile` that ships *inside the nupkg*, so it is what NuGet
  consumers read, and it rots independently of the root catalog (the `NetWorld` README still described the pre-8.0.0
  `WorldColliders`/`WorldSurfaces` ctor params two releases later; 8.2.0's telemetry types were missing from the
  `Diagnostics`/`Gui`/`Netcode` READMEs on the first sweep) - and `docs/DEPENDENCY-SEAMS.md` whenever a dependency
  edge or a seam member changed. For a behaviour/bug change: `CHANGELOG.md` AND any doc, README, or code comment that
  described the OLD behaviour. Mechanical check before committing: grep the new (or removed) type / package / flag
  name across **ALL `*.md` recursively** (root, `docs/`, AND every per-package `<Package>/README.md`) + `CLAUDE.md`
  and confirm every place that should mention it does (and no stale doc still describes what you removed).
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
  - **Render/runtime stack:** `Gpu`, `Windowing`, `Render2D`, `Render3D` (includes the splat-material pipeline:
    `SplatProjection`/`SplatLayerImage`/`SplatMaterialConfig`/`SplatMath`, `Scene3D.LoadSplatMaterial`/
    `SplatMaterialHandle`/`UnloadSplatMaterial`, `Scene3D.LoadMesh(GltfMesh, SplatMaterialHandle)`; the `SplatFrag`
    shader + a second model-pass pipeline in `ModelRenderer`), `Gui`, `Audio`, `Particles`, `Effects`,
    `Game`, `Game.Render3D`.
  - **Foundation (MonoGame-free):** `Ecs`, `Serialization`, `Content`, `Diagnostics`, `App`, `Localization`,
    `Locomotion`, `Persistence`, `Pooling`, `Platform`, `Updates`, `Collision`, `Physics`, `Terrain`, `Netcode`,
    `Netcode.Abstractions`, `Netcode.LiteNetLib`, `Simulation`, `Replication`, `WorldStore`, `WorldStore.Sqlite`,
    `WorldStore.SqlServer`, `Sharding`, `NetWorld`. `Physics.Bepu` is opt-in (NOT in any umbrella; add
    explicitly like `WorldStore.Sqlite`).
  - **Server / parallel-job core types:** `Simulation` = `FixedTickHost` + the `IJobScheduler` worker-pool seam
    (`SingleThreadedJobScheduler` inline default + `ThreadPoolJobScheduler`); `Netcode` =
    `INetTransport`/`LoopbackTransport` + the `NetServer`/`NetClient` session layer (the `IConnectionAuthenticator`
    gate returns a verified `subject` on accept, surfaced as `ServerSessionEvent.Subject`; `AllowAllAuthenticator`
    = token-as-subject dev default, `SignedToken`/`HmacTokenAuthenticator` = zero-dep HMAC-SHA256 signed
    connect-token `v1.<subject>.<expUnix>.<base64url-mac>` (+ a v2
    `v2.<subject>.<base64url-name>.<expUnix>.<base64url-mac>` carrying an optional cosmetic display-name claim,
    surfaced via the opt-in `IConnectionDisplayName` companion interface as `ServerSessionEvent.DisplayName` -
    distinct from `subject`/account id, empty for v1)); plus `RateLimiter` (a deterministic, headless token bucket -
    per-step `Refill`/`TryConsume`, no wall-clock - for per-connection message-flood protection) and
    `NetServer.Disconnect(slot)` (a kick seam); plus `BoundedEventQueue<T>` (a drop-oldest hard cap - keeps the
    newest, evicts the oldest at capacity, `DefaultCapacity` 10,000, `DroppedCount` observable - the `NetServer`
    session inbox and both `Netcode.LiteNetLib` transport inboxes use it via an optional `maxQueuedEvents` ctor arg
    + a `DroppedEventCount` property, so a stalled/flooded host can't grow undrained events (each Data event pins a
    payload buffer) without bound; mirrors the per-slot bounding `RemoteCommandQueue` already does, never bites a
    drain-each-poll host); plus `NetTransportStats` + the default-interface `INetTransport.Stats` (transport-agnostic
    RTT / loss / cumulative byte counters, `Unavailable` by default so the loopback + any external transport keep
    compiling untouched; the `Netcode.LiteNetLib` client binding sets `EnableStatistics` and fills it from the server
    peer), forwarded by `NetClient.TransportStats` and surfaced to games as `WorldClient.NetStats`; `Replication` = authoritative ECS
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
    streaming-consistent, same coordinate-hash as the rendered scatter),
    `PropScatter.GenerateCompanions(TerrainField, IReadOnlyList<PropPlacement>, CompanionConfig)` (pure,
    render-free, tiling-invariant: rings each host whose id is in `CompanionConfig.HostKinds` with `Count`
    foliage instances hashed off the host's centimetre-quantized world XZ; `Y` resampled from the field;
    `MaxHeight` excludes off-mountain companions; new `CompanionConfig` type), and
    `TerrainPresets.Clearing()`/`BoundedClearing()`. Height depends only on `(x,z,seed)`
    (load-order independent for sharded streaming); plain `float` (authoritative server + visual client, NOT
    `DeterministicFp`). `Terrain.Render3D` (companion, in `Game3D`) = `TerrainChunkBuilder` (chunked-LOD mesh off the
    field: skirts, per-vertex splat weights, chunk AABB) + `TerrainLod.PickLod` + `Scene3D.LoadTerrainChunk`/
    `DrawTerrainChunk`, plus the `TerrainStreamer` client world-streaming layer (`ChunkCoord`/`ChunkGrid`
    coord<->world, `IChunkSink` load/unload seam, `StreamerConfig`, and the production `Scene3DChunkSink` (now
    multi-layer via N `PropLayer`s): each `PropLayer` (tagged struct) is either `PropLayer.ScatterLayer(...)` or
    `PropLayer.CompanionLayer(hostLayerIndex, ...)` with its own mesh set and draw radius (short for dense ground
    cover, long for trees); companion layers derive their placements per chunk from their host scatter layer so
    each host emits companions exactly once even when they spill into a neighbour chunk; the existing single-layer
    ctor is unchanged and byte-identical; ring load/unload with a hysteresis band, distance-LOD re-meshing,
    amortized main-thread loading; both `Scene3DChunkSink` and `TerrainStreamer` are `IDisposable` (7.75.0 leak fix):
    `TerrainStreamer.UnloadAll()` flushes the loaded ring through the sink (rebuild streaming while reusing the same
    sink), `Dispose()` does that plus disposes the sink if `IDisposable`, and `Scene3DChunkSink.Dispose()` unloads
    every still-loaded chunk's GPU mesh so a streaming teardown against a surviving `Scene3D` frees the ring instead of
    leaking it; the splat material is caller-owned by default (free it via `Scene3D.UnloadSplatMaterial`, never
    per-chunk) unless the opt-in `ownsMaterial` ctor flag hands it to the sink's `Dispose`),
    plus the PBR splat-material layer (shipped 7.64.0): `TerrainSplatPacking`, `TerrainMaterialLayer`/
    `TerrainLayeredMaterial`, `TerrainMaterialPresets` (procedural placeholder), `TerrainScene3D.LoadTerrainMaterial`
    + a textured `LoadTerrainChunk` overload, and an optional material slot on `Scene3DChunkSink` - supply a
    `TerrainLayeredMaterial` to render five tileable PBR layers (grass/dirt/rock/sand/snow) blended per-fragment
    by the baked splat weights via world-space triplanar tiling, normal maps, mips, and anisotropic filtering;
    omit it for the height/slope vertex-colour ramp fallback (byte-identical). First overworld render-scale
    sub-project (`docs/superpowers/specs/2026-06-27-terrain-system-design.md`); world streaming is sub-project 6a
    (`docs/superpowers/specs/2026-06-27-world-streaming-design.md`); multi-cell server sharding (6b) and water are
    later sub-projects.
  - **3D physics seam + backend (8.0.0):** `Physics` (dependency-free, in `Foundation`) = `IPhysicsWorld`
    (static bodies, `Step(dt)`, spatial queries: `Raycast` -> `RayHit`, `SweepCapsule` -> `SweepHit`,
    `ComputePenetration`), value-type `PhysicsShape` subclasses (`Sphere`/`Capsule`/`Box`/`Cylinder`/
    `ConvexHull`/`TriangleMesh`/`Compound`), `Pose` (position + quaternion, `Pose.At(Vector3)`),
    `PhysicsMaterial(Friction, Restitution)`, `QueryFilter`, `StaticHandle`, `RayHit`/`SweepHit`. `Physics.Bepu`
    (opt-in, NOT in any umbrella; add explicitly like `WorldStore.Sqlite`) = `BepuPhysicsWorld : IPhysicsWorld`
    over BepuPhysics v2 2.4.0 (pure-managed, Apache-2.0, no native libs); constructed via `new BepuPhysicsWorld()`
    (no factory, no gravity param in SP1). `ke-propbake` bakes a 3D collision shape per prop into the
    kit manifest: trees -> trunk `ConvexHullShape` tracking the lean via `PropCollisionBake.BakeTrunkHull`
    (percentile-filtered lower-trunk verts, cylinder fallback on degenerate; 8.4.0), rocks/
    solid props -> `ConvexHullShape` (base-aligned compound, full deduplicated vertex set via `HullFromPoints`), buildings ->
    `TriangleMeshShape` (concave interior); `PropBakePlan.For` single-sources the per-prop bake decision (8.4.0).
    The render-free KECL `.coll` format lives in `Physics` as
    `PropCollisionFormat` (8.1.0): `Write`/`Read` (the encoder/decoder) + headless `LoadDirectory(dir)` (maps
    `<id>.coll` -> shape) / `Load(IEnumerable<(id,collPath)>)` loaders, needing only `System.IO` + `PhysicsShape`,
    so an authoritative server referencing just `Physics` (+ opt-in `Physics.Bepu`) builds the same world a client
    predicts against - no `Render3D`/`Gpu`/`Windowing`. `Render3D.PropCollisionBake.Write` /
    `PropCollisionLoader.Read` delegate to it (byte-identical, public API unchanged); the manifest-driven
    `PropCollisionLoader.LoadAll(AssetManifest)` + the shape-producing `Bake(GltfMesh)` stay in `Render3D`.
    `Scene3DChunkSink.Load`/`Unload` call `AddStatic`/`RemoveStatic` on chunk load/unload.
    `CharacterMovement.Step` accepts `IPhysicsWorld?` (8.4.0: substepped swept collide-and-slide via
    `SweepCapsule` + a step-up probe wiring `MoveTuning.StepHeight`; walkable contacts followed; steep contacts
    block-and-slide; depenetration retained as a residual settle pass; no tunneling through thin meshes; no
    trapping inside closed meshes; terrain stays analytic; `null` = terrain-only; no signature change).
    The old 2D `WorldColliders?`/`WorldSurfaces?` `Step` overloads are removed
    (BREAKING 8.0.0). `KhaozEngine.Collision` remains in `Foundation` for 2D games / lockstep sims; it is no
    longer on the 3D movement path.
  - **Static-world collision (in `Collision`):** `BoxCollision` (circle-vs-AABB / oriented-box / circle
    minimum-translation push-out), `ColliderShape` (unplaced cylinder/box prop footprint), `WorldCollider` (one
    placed static collider), `WorldColliders` (a `SpatialHashGrid`-backed queryable set: `Query(x,z,radius)` +
    an iterate-and-slide `Resolve` that pushes a capsule footprint out of overlaps, slide along surfaces).
    Render-free, kinematic, XZ-plane. `Collision` stays a `System.Numerics` leaf. Pre-8.0.0 the movement
    path used `WorldColliders`/`WorldSurfaces`; since 8.0.0 the 3D movement path uses `IPhysicsWorld` (the
    `Physics` seam) instead. `Collision` is still used by 2D games and lockstep sims.
  - **Walkable prop/building surfaces (in `Collision`, pre-8.0.0 3D path, now superseded by `IPhysicsWorld`):**
    `PropSurface` (unit-scale top-down max-height grid + bilinear `SampleLocal` + binary IO), `WorldSurface`,
    `WorldSurfaces` (spatial-hash set, `Query(x,z)` returns max prop-top); the `ke-propbake` tool bakes `.surf`
    heightmaps; `AssetEntry` carries `Surface`/`Heightmap`. These types remain in `Collision` but are no longer
    wired into `CharacterMovement.Step` / `PlayerMoveSimulator` / `WorldServer` / `WorldClient` since 8.0.0
    (replaced by the `IPhysicsWorld` down-probe).
  - **Locomotion + networked-world libs (render-free movement + the netcode wiring):** `Locomotion` (leaf, in
    `Foundation`, deps Primitives + Physics) = `CharacterMovement.Step`, two overloads sharing one horizontal core
    (camera-relative move from a `MoveCommand` + ground delegate + one `MoveTuning`, slope gate - runs only when a
    `groundNormal` delegate is passed, e.g. `TerrainCollision.GroundNormal`, so the rim wall can't be climbed - plus
    optional 3D physics collision via a nullable `IPhysicsWorld?` (8.0.0, replaces the old `WorldColliders?` pair;
    8.4.0: now resolved by a substepped swept collide-and-slide over `SweepCapsule` - no tunneling through thin
    one-sided walls, capsule never trapped inside a closed mesh - plus a step-up probe wiring `MoveTuning.StepHeight`
    so stair treads/curbs below `StepHeight` are auto-mounted; walkable contacts followed; steep contacts
    block-and-slide; depenetration retained as a residual settle pass; `null` = terrain-only, byte-identical):
    terrain height stays analytic): the original `Step(Vector3,...) -> Vector3` is horizontal-only (Y instant-clamped
    to ground + half-height), and `Step(in MoveState,...) -> MoveState` is the vertical-physics step (gravity
    terminal-clamped to `MaxFallSpeed`, land-and-clamp ground contact with a `GroundedEpsilon` slope skin, jump with
    coyote-time + jump-buffer and no apex double-jump, `AirControl`-scaled airborne XZ, plus an optional `clampXz`
    for the play-area bound). `MoveState` = the carried kinematic state (position + `VerticalVelocity` + `Grounded` +
    coyote/buffer timers); `MoveCommand` has a `Jump` bit; `MoveTuning` carries
    `Gravity`/`JumpSpeed`/`MaxFallSpeed`/`CoyoteTime`/`JumpBuffer`/`AirControl`/`GroundedEpsilon`/`CapsuleRadius`/`StepHeight`
    (default 0.4 / 0.4). Shared by the local `CharacterController3D` (Game.Render3D wraps it, jumps on Space), the
    server sim, and client prediction. `NetWorld` (in
    `Server`, deps Locomotion/**Collision**/**Physics**/Netcode/Replication/Ecs/Serialization/WorldStore/**Sharding**) =
    `WorldBounds` (abstract `Contains`/`Clamp` + `CircleBounds`/`RectBounds`, the authoritative play-area shape;
    `Clamp` = nearest in-bounds point, idempotent inside + clamp-and-slide outside) wired as a nullable bound through
    the movement step (off = unbounded, unchanged), `PlayerMoveSimulator` (`ITickSimulator` over
    `CharacterMovement.Step`, clamps to `WorldBounds`; resolves the optional `IPhysicsWorld?` inside `Step` so the
    server is authoritative + identical to client prediction), `WorldServer` (single-`World` authoritative: per-player `RemoteCommandQueue` +
    ground-clamped sim + per-client AoI via `SnapshotWriter.WriteFiltered`+`InterestGrid`, framed `[localNetId][ack]`;
    a persistence seam = `PlayerJoined`/`PlayerLeaving` events + accountId-from-verified-subject (the
    authenticator's `subject`, `guest:{slot}` when empty) + an optional `IConnectionAuthenticator` ctor arg
    (default `AllowAllAuthenticator`) + `SetPlayerState` + `SetPlayerDisplayName`; plus an opt-in server-side
    anti-cheat layer (shipped 7.74.0, same on both servers) = `WorldServerConfig.AntiCheat` /
    `ShardedWorldServerConfig.AntiCheat` (an `AntiCheatConfig`, all off by default):
    `MoveProtocol.TryDecodeMove` rejects a NaN/Inf move axis or yaw as malformed (always-on; defense-in-depth finite
    guard in `CharacterMovement.Step`), per-connection message rate limiting via the `Netcode.RateLimiter` token
    bucket, and an `OnSuspiciousActivity` signal hook firing a value-type `SuspiciousActivity { Slot, Reason,
    Magnitude }` (`SuspiciousReason` = `MalformedPacket`/`RateLimited`/`MovementCorrection`; the last fires on a
    streak of authoritative move corrections beyond `MaxCorrectionDistance` - the client driving into the slope
    gate / collision / bound - measured via `CharacterMovement.IntendedHorizontalTarget`) + a `Disconnect(slot)`
    kick seam; signal not policy, the game decides),
    `ShardedWorldServer` (+ `ShardedWorldServerConfig`, the multi-cell variant = the same movement stack run across a
    `Sharding.ShardHost` cell grid: routes each client's `MoveCommand` to the owning cell, steps each cell's
    `PlayerMovementSystem` (also clamps to the optional `WorldBounds`) via `ShardHost.Tick` scheduler-fanned, exactly-once handoff on boundary crossings -
    `NetId` stable - border ghosting, single home-cell AoI framed identically; `PendingMove` = the per-tick command
    a cell applies to an owned player, server-local + not replicated/migrated; the `WorldClient`/`MoveProtocol` are
    UNCHANGED), `WorldClient` (`NetClient`+`ClientReplicationView`+`ClientPrediction` -> `EntityRenderState[]`, local
    predicted/reconciled - prediction runs against the same optional `WorldBounds`/`IPhysicsWorld?`
    as the server (trailing ctor params mirroring `WorldServer`, default null = terrain-only), so it predicts around
    solid props and clamps at the wall instead of rubber-banding, keeping reconciliation a no-op -
    remotes replicated AND smoothly interpolated: a render-time presentation clock in `AdvancePresentation` ramps
    each remote between its last two snapshots via `ClientReplicationView.Interpolate` (`ReplicatedPosition`'s lerp;
    `MovementState` stays un-interpolated), so a remote glides instead of teleporting one ~tick-rate snapshot-step
    per ingest - default-on `WorldClientConfig.InterpolateRemotes` (=`true`; ~one tick of remote render latency,
    renders ~one snapshot in the past, no extrapolation; `false` = raw latest, the pre-7.70.0 behaviour); plus the
    read-only `WorldClient.NetStats` -> a `Diagnostics.ClientNetStats` diagnostics snapshot of transport RTT / loss /
    byte rates, the AoI snapshot ingest rate, and the prediction-correction magnitude (last + a 64-sample rolling avg
    from the formerly-discarded `ReconciliationResult.PositionError`); rates roll over a ~1s window driven by
    `AdvancePresentation`, `Connected` tracks `Joined`, reading it never mutates state, no ctor/signature change so
    `NetWorld` now also references `Diagnostics`), and
    `WorldPersistence` (+ `PlayerRecord`, a forward-tolerant JSON
    record): wires an `IWorldStore` into the server lifecycle via `IWorldPersistenceHost` (the seam `WorldServer` AND
    `ShardedWorldServer` both implement) - load-on-join / save-on-leave / periodic dirty snapshot, keys
    `player:{accountId}`, player-keyed + cell-agnostic (a loaded player spawns at its saved position in whatever cell
    contains it) - so the world survives a restart, backend-agnostic. `PlayerMoveState :
    IPredictedState` lives in NetWorld (not Locomotion) so the movement core + local controller stay netcode-free; it
    wraps a `Locomotion.MoveState` (exposes `Position` + `VerticalVelocity` + `Grounded`). The vertical axis rides
    the wire as a replicated `MovementState` component (type id 2, alongside `ReplicatedPosition`), so it survives a
    cell handoff and forms the client's exact reconcile basis: `WorldServer`/`ShardedWorldServer` write it (added at
    spawn), and `WorldClient` reconciles `y`/`VerticalVelocity`/`Grounded` alongside XZ (full basis rebased + unacked
    commands replay the same `Step`, so a jump in flight converges with no permanent desync). A player's cosmetic
    display name rides as a replicated `PlayerIdentity { DisplayName }` component (type id 3, length-prefixed UTF-8
    capped at `MoveProtocol.MaxDisplayNameBytes` = 64, no lerp): set it via `WorldServer`/`ShardedWorldServer`
    `SetPlayerDisplayName(slot,name)` or carry it on a v2 `SignedToken` claim (auto-applied at join), and read it off
    the additive `EntityRenderState.DisplayName` (`null` when absent; distinct from the account id). Demos
    (`IsPackable=false`): `NetworkedWalkServer` (headless,
    multi-cell `ShardedWorldServer`, persists via `SqliteWorldStore`) + `NetworkedWalkSample` (windowed `--connect`
    client, sends a stable account token).
    8.2.0 additive: `WorldClient` exposes a live `ConnectionState` machine (Connecting/Connected/Reconnecting/Disconnected)
    + `ConnectionStateChanged` + `DisconnectReason`/`DisconnectReasonDetail` (RejectedToken/Unreachable/ServerShutdown/Timeout);
    a factory ctor `WorldClient(Func<INetTransport> connect, ...)` adds auto-reconnect with `ReconnectBackoff` and
    `ReconnectAttempt`/`SecondsUntilNextRetry` for a "reconnecting..." UI (`WorldClient` is now `IDisposable`);
    `Poll(float dt = 0f)` (dt 0 = net-only, no health timers; pass real dt for timeout/reconnect detection);
    server->client notice channel: `ServerNotice { Kind, Message, SecondsUntil, Payload }` / `ServerNoticeKind`
    (Custom/Maintenance/Shutdown), `WorldServer.BroadcastNotice` + `ShardedWorldServer.BroadcastNotice`, surfaced on
    `WorldClient.NoticeReceived` + `LastNotice`; graceful drain: `BeginDrain(notice, graceSeconds)` /
    `IsDraining` / `IsDrainComplete` on both servers (tick-driven; host calls `WorldPersistence.FlushAsync()` then
    disposes the transport on completion); wire: 1-byte `ServerFrameKind` envelope on the server->client Data stream
    (snapshot vs notice, internal protocol only).
    Networked-overworld render-scale sub-project (`docs/superpowers/specs/2026-06-27-networked-overworld-design.md`);
    persistence sub-project (`docs/superpowers/specs/2026-06-27-persistent-worldstore-design.md`); multi-cell sharding
    sub-project 6b (`docs/superpowers/specs/2026-06-27-multicell-sharding-design.md`).
    8.4.0 additive: a generic server-admin surface - `IAdminControllable` (`ListOnline`/`Teleport`/`Kick`/`Broadcast`,
    queued + applied on the host thread, online snapshot published per tick) implemented by BOTH `WorldServer` and
    `ShardedWorldServer`; `PlayerRef`/`OnlinePlayer`; an `IBanStore` seam (`InMemoryBanStore` + `WorldStoreBanStore`
    over the `IWorldStore` keyspace `ban:{accountId}`, sync `IsBanned` consulted at connect via the trailing optional
    `banStore:` ctor arg, ban-while-online kicks); and the `ServerAdmin` facade composing them. `WorldStore` gains the
    opt-in `IEnumerableWorldStore` (`EnumerateAsync` + `WorldStoreEntry`) on `InMemoryWorldStore`/`SqliteWorldStore`/
    `SqlServerWorldStore`.
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
    animation). The position-driven multi-player bridge (7.65.0) is `ReplicatedCharacterAnimators` (in `Game.Render3D`):
    owns one `AnimatedCharacter` per networked entity keyed by a stable id, fed an engine-neutral `CharacterSample[]`
    (position-only, or position + exact movement) each frame via `Update(samples, dt)` -> draw-ready `CharacterPose`s
    (`Live`, each `World` = `scale * RotationY(facingYaw) * Translation` + the bone palette); derives planar speed /
    vertical velocity / facing yaw from the position displacement averaged over a short window
    (`CharacterAnimatorTuning.VelocityWindowSeconds`, default 1/30 s = one tick - holds velocity across the
    zero-delta frames a plateauing `ClientPrediction.RenderedState` produces when render fps > tick rate, so the
    locomotion state does not strobe Idle<->moving; `<= 0` reverts to per-frame; 7.67.0 fix) (exact grounded + vVel
    honored when a sample carries them, e.g. the local player), lifecycle-creates/-drops per id (no leak on
    disconnect), holds yaw at rest, reuses `LocomotionStateMachine`; tuned by `CharacterAnimatorTuning`. `AnimatedCharacter`
    additionally DEBOUNCES ground-state transitions (`stateDebounceSeconds` ctor param /
    `CharacterAnimatorTuning.StateDebounceSeconds`, default `AnimatedCharacter.DefaultStateDebounceSeconds` = 0.08 s):
    a new idle/walk/run commits only after it has held that long, so the residual ripple in the derived speed (the
    prediction/reconcile render stream isn't perfectly smooth; a remote's replicated position is a ~30 Hz staircase)
    can't restart the clip every few seconds - worst while sprinting, where the ripple straddles the walk/run split;
    air states switch instantly; `0` = immediate (pre-7.68.0). NO netcode dependency (the consumer maps its
    `EntityRenderState` -> `CharacterSample` in a 3-line loop, keeping `Game.Render3D` off `NetWorld`). For EXACT air
    state, `EntityRenderState` carries `Grounded` + `VerticalVelocity` for EVERY entity (7.68.0; local = predicted,
    remote = replicated `MovementState` surfaced by `WorldClient.Snapshot()`), so a consumer feeds the exact air state
    for remotes too - deriving "airborne" from a remote's terrain-following position misfires (the faster it moves over
    a slope, the more it looks like falling); the read-only `WorldClient.LocalRenderState`/`LocalGrounded`/
    `LocalVerticalVelocity` (7.65.0) remain for the local sample. `TerrainWalkSample` walks a committed Quaternius Universal CC0 rigged+animated
    character (clips named exactly Idle/Walk/Run/Jump/Fall; skinned-ingest preserves the rig + clips; NOT the flatten-prop
    path), and `NetworkedWalkSample` drives one animated avatar per replicated player through `ReplicatedCharacterAnimators`
    (the same asset). Out of scope: animation events, root motion, IK, additive/facial
    layers, full blend trees, networked animation. Design:
    `docs/superpowers/specs/2026-06-27-animated-characters-design.md`.
  - **Diagnostics / telemetry overlay (8.2.0):** three render-free types in `Diagnostics` - `FrameStats` (rolling
    fps / frame-ms avg-min-max / `ManagedBytes` meter off a `Sample(dt)` stream), `TelemetryRecorder` (+
    `TelemetryChannel`: a crash-safe JSON-Lines session recorder, flushed per line, non-finite -> JSON `null`), and
    `ClientNetStats` (the connection-health snapshot shape; defined HERE, not in `NetWorld`, so the Gui overlay can
    name it without `Gui` -> server/netcode stack) - plus the `DiagnosticsOverlay` widget + `DiagnosticsOverlayTheme`
    (+ `OverlayRow`/`OverlaySection`) in `Gui`: a pure presenter modeled on `UpdateOverlayView` (F1-toggled corner
    panel, game-assembled sections each frame via `SetSections`, `PerformanceSection(FrameStats)` /
    `NetworkSection(in ClientNetStats)` populators, headless-testable `Update`, `Draw` over `GuiDraw` + `SpriteBatch`).
    `Gui` and `NetWorld` each gained a project reference to the `Diagnostics` leaf. Surfaced live via
    `WorldClient.NetStats` (above). Drives Ruinborne's alpha telemetry HUD; design
    `Ruinborne/docs/superpowers/specs/2026-06-29-telemetry-overlay-design.md`.
  - **Server admin endpoint (8.4.0):** `Server.Admin` = the opt-in HTTPS admin endpoint (`AdminHttpServer` over
    Kestrel minimal hosting, `AdminEndpointOptions`, `AdminTlsCertificate` incl. `CreateSelfSigned`) exposing
    `ServerAdmin` as a bearer-token REST API. The ONLY package referencing ASP.NET Core (via `FrameworkReference`);
    NOT in any umbrella - added explicitly like `WorldStore.Sqlite` / `Physics.Bepu`.
  - **Umbrellas (code-free metapackages):** `Foundation`, `Game2D`, `Game3D`, `Server`.
  - **Tools, same version line:** `Updates.Tool` (`ke-updater`: manifest/genkey/sign/verify), `Sfx.Tool`
    (`ke-sfxbake`: ElevenLabs-driven SFX bake), and `PropSurface.Tool` (`ke-propbake`: bakes a `.coll` collision
    shape for EVERY prop (trees get a `BakeTrunkHull` leaning-trunk hull; 8.4.0) and a `.surf` heightmap for
    walkable solids only, folded into kit ingest; `PropBakePlan.For` single-sources the per-prop bake decision) are `PackAsTool`;
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
