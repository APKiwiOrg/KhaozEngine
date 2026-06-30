# KhaozEngine

A shared, game-agnostic, **MonoGame-free** 2D/3D engine: windowing + input, a GPU abstraction, 2D and 3D
renderers, an immediate-mode + screen-stack GUI, audio, particles, an ECS, netcode, and the usual foundation
(content, persistence, localization, diagnostics). One implementation, used by four games (Hardpoint,
Nullwake, SpaceGame, Ruinborne), so a fix written once propagates to all of them.

KhaozEngine is split into focused, independently-referenceable NuGet packages plus a few umbrella metapackages,
so a game pulls in just what it needs (and a logic library or headless server can pull a renderer-free subset).

| Package | What it gives you | Depends on |
|---|---|---|
| **KhaozEngine.Primitives** | The zero-dependency leaf (`System.Numerics` only): `Color` (`FromHex`/`ToHex`, `* float`, `Lerp`), `DeterministicRng`, `XorRng`, `MathUtil`, `ViewportMath`, `Easing`. The bottom of the dependency graph; an RGBA color anywhere in the public API is a `Color`. | Pure .NET |
| **KhaozEngine.Gpu** | The GPU backend seam: backend selection (Metal/D3D11/Vulkan/OpenGL via RID), device + command-list abstraction over Veldrid. The only graphics-API-aware layer. | Pure .NET (+ Veldrid) |
| **KhaozEngine.Windowing** | `AppWindow` (owns the Silk.NET/GLFW window + the per-frame `Run` loop), the immutable `InputState` snapshot, `InputManager`/`Pointer` (unified pointer, edges, `IsTapIn` press-origin invariant, region blocking, drag/scroll, keyboard/gamepad/menu-nav), `GameClock`, `DesignViewport`/`AdaptiveViewport`. Wires a GLFW text clipboard into `Platform.Clipboard`. | KhaozEngine.Gpu, Platform |
| **KhaozEngine.Render2D** | `SpriteBatch` (textured quads + `DrawString` over stb_truetype `SpriteFont`, optional model transform + sampler mode), `Camera2D`, `Texture2D`, `ImageRgba` (CPU pixel/opaque-mask decode), `Render2DSurface`, scissor/point-sampling, offscreen capture. | Windowing, Gpu |
| **KhaozEngine.Render3D** | Stylized 3D: `Scene3D` (multi-instance mesh draw, glTF + procedural meshes, per-mesh albedo textures, materials/lighting, billboards, runtime GPU bone-palette skinning for code-driven deformation, a splat-material pipeline for PBR terrain (`LoadSplatMaterial`/`SplatMaterialHandle`), debug draw), `IsoCamera3D` (ortho iso + screen-to-ground/ray picking), `PixelPostProcess`, `Render3DSurface`. | Ecs, Windowing, Gpu |
| **KhaozEngine.Gui** | `GuiSurface` (immediate-mode UI: Panel/Label/Button/Slider/Toggle, hover, click-through gate), `ScreenStack` (top-to-bottom routed screen stack + widgets), `FocusNavigator` (menu navigation), `DiagnosticsOverlay` (F1-toggled corner telemetry HUD: game-built `OverlaySection`/`OverlayRow`s + `PerformanceSection`/`NetworkSection` populators, headless-testable `Update`). | Windowing, Render2D, Diagnostics |
| **KhaozEngine.Audio** | `AudioSystem`: an OpenAL (Silk.NET.OpenAL) streaming music backend + SFX one-shots and 3D positional audio over a voice pool. WAV/OGG/MP3. Random track rotation is scopeable via `SetRotationPool` (e.g. menu-only music while every track stays playable on demand). | Diagnostics |
| **KhaozEngine.Particles** | Pure, deterministic particle simulation (xorshift, `System.Numerics` + BCL only): `ParticleSystem` pool, `EmitterConfig` presets, `RateAccumulator`. | Pure .NET |
| **KhaozEngine.Effects** | Game-feel visual effects: `ScreenShake` (trauma-based), parallax helpers. | Pure .NET |
| **KhaozEngine.Telegraphs** | Attack-telegraph / danger-zone indicators (presentation-only): `TelegraphStyle` (+ `Generic`/`Fire`/`Poison` presets), the pure `TelegraphResolve` progress->visual mapping, and the immediate-mode `TelegraphRenderer2D` (circle/ring/beam/cone/arc). In the `Game2D` umbrella. | Render2D, Primitives |
| **KhaozEngine.Telegraphs.Render3D** | The ground-plane arm of telegraphs: `Scene3D.GroundCircle/Ring/Beam/Cone/Arc` extensions that paint danger zones flat on the ground/terrain via the engine's depth-sampling `DrawGroundDecal` primitive. In the `Game3D` umbrella. | Telegraphs, Render3D |
| **KhaozEngine.Terrain.Render3D** | The render arm of terrain: `TerrainChunkBuilder` meshes finite chunks off a `TerrainField` into Render3D `GltfMesh`es with distance LOD (`TerrainLod.PickLod`), ~0.3 m skirts, per-vertex splat weights, and a chunk AABB; plus `Scene3D.LoadTerrainChunk`/`DrawTerrainChunk`, the `TerrainStreamer` client world-streaming layer (`ChunkCoord`/`ChunkGrid`, `IChunkSink`, `StreamerConfig`, `Scene3DChunkSink`: an endless ring of chunks + props with a hysteresis band, distance-LOD re-meshing, and amortized main-thread loading; both are `IDisposable` so a streaming rebuild against a surviving `Scene3D` frees the loaded ring instead of leaking it), and the PBR splat-material layer (`TerrainMaterialLayer`/`TerrainLayeredMaterial`, `TerrainMaterialPresets`, `TerrainScene3D.LoadTerrainMaterial` - supply a `TerrainLayeredMaterial` to render five tileable PBR layers blended by the baked splat weights; omit it for the height/slope vertex-colour ramp fallback). In the `Game3D` umbrella. | Terrain, Render3D |
| **KhaozEngine.Game** | The 2D game-loop facade: `GameApp` (abstract base owning the per-frame compose: clock/viewport/input/draw) + `GameAppOptions`, and a `SceneManager`/`GameScene` state stack (Push/Pop/Replace/SwitchTo, overlay DrawBelow/UpdateBelow). | Windowing, Render2D, Gui |
| **KhaozEngine.Game.Render3D** | The 3D bridge for the Game framework: `GameApp3D` (a `GameApp` that stands up a `Render3DSurface` + drives the 3D pass), `IGameScene3D`, and a `SceneManager.Draw3D` extension; plus the animated-character layer (`AnimatedCharacter` + `ReplicatedCharacterAnimators`, the position-driven per-player animator bridge). Kept separate so a 2D game pulls no 3D renderer. | Game, Render3D |
| **KhaozEngine.Ecs** | A struct-based archetype `World`/`Entity`/`ISystem` ECS: by-ref component access, `ForEach`, opt-in data-parallel `ParallelForEach` (fans archetype rows across the `IJobScheduler` worker pool, with an `AccessSet` read/write-declaration model + a debug hazard guard + per-worker command buffers), command buffer, system groups, `CachedQuery`, `WorldSerializer`. (`DeterministicRng` moved to `KhaozEngine.Primitives` in 6.0.0.) | Serialization, Simulation |
| **KhaozEngine.Content** | Config/content loading (embedded or disk JSON) with JSON-schema validation + build-time schema enforcement. | Diagnostics, Serialization (+ JsonSchema.Net) |
| **KhaozEngine.Diagnostics** | Logging service: levels, pluggable sinks (rotating file / console / debug / in-memory), category loggers, a static `Log` facade over an injectable `LogManager`, crash hooks. Plus the telemetry trio: `FrameStats` (rolling fps / frame-ms / managed-bytes meter), `TelemetryRecorder` (+`TelemetryChannel`, crash-safe JSON-Lines session recorder), and `ClientNetStats` (the connection-health snapshot `WorldClient.NetStats` fills and the overlay renders). | Pure .NET |
| **KhaozEngine.App** | App/runtime helpers: `BuildMetadata` (read `AssemblyMetadata` at runtime), `AppDataPaths` (publisher-rooted OS-correct per-app data dir), `ServiceLocator`. | Pure .NET |
| **KhaozEngine.Localization** | `LocalizationManager`: discover satellite-resource cultures and set the current thread culture. | Diagnostics |
| **KhaozEngine.Persistence** | `SaveEncoder` (Base64 + HMAC-SHA256 tamper-deterrent), `AtomicJsonWriter` + `PersistenceQueue` (crash-safe atomic writes), `SettingsManager<T>`, and the `GameStorage` facade. | Diagnostics, App, Serialization |
| **KhaozEngine.Serialization** | Shared `System.Text.Json` defaults (`JsonDefaults`: tolerant-read / indented-write / include-fields) so Content, Persistence, and Ecs serialize consistently. | Pure .NET |
| **KhaozEngine.Platform** | `Clipboard`: cross-platform system-clipboard facade. Text via a registered window/GLFW provider (wired by Windowing's `AppWindow`) with a macOS `NSPasteboard` fallback; images via Windows CF_DIB / macOS / optional mobile bridge. Best-effort and never-throwing. | Pure .NET |
| **KhaozEngine.Pooling** | `ObjectPool<T>`: fixed-capacity free-list pool with O(1) rent/return, active/free tracking, swap-removal compaction. | Pure .NET |
| **KhaozEngine.Collision** | Deterministic 2D collision + broadphase: `CircleCollision`, `SpatialHashGrid`, static-world collision (`BoxCollision` circle-vs-AABB/oriented-box push-out, `WorldCollider`/`WorldColliders` with height-aware blocking), and walkable prop surfaces (`PropSurface`/`WorldSurface`/`WorldSurfaces` top-surface height grids you stand on / jump onto). Bit-identical math for lockstep sims (`System.Numerics`). | Pure .NET |
| **KhaozEngine.Physics** | Dependency-free 3D physics seam (in `Foundation`): `IPhysicsWorld` (static bodies, `Step(dt)`, raycast/sweep-capsule/penetration queries), value-type shapes (`Sphere`/`Capsule`/`Box`/`Cylinder`/`ConvexHull`/`TriangleMesh`/`Compound`), `Pose`, `PhysicsMaterial`, `QueryFilter`, `StaticHandle`, `RayHit`/`SweepHit`. Also the render-free `PropCollisionFormat` KECL `.coll` reader/writer + headless loaders (`LoadDirectory`/`Load`), so an authoritative server with no GPU/windowing loads the same baked shapes a client predicts against. The backend is opt-in (see `Physics.Bepu`). | Pure .NET |
| **KhaozEngine.Physics.Bepu** | BepuPhysics v2 backend (opt-in, NOT in any umbrella; add explicitly like `WorldStore.Sqlite`): `BepuPhysicsWorld : IPhysicsWorld` over BepuPhysics 2.4.0 (pure-managed, Apache-2.0). Single-threaded deterministic `Simulation`. `AddStatic`/`RemoveStatic`, `Step(dt)`, raycast, sweep-capsule, penetration. Wire into `CharacterController3D` / `WorldServer` / `WorldClient` via the `IPhysicsWorld?` ctor param. | KhaozEngine.Physics, BepuPhysics |
| **KhaozEngine.Terrain** | Render-free analytic terrain: `TerrainField` (`SampleHeight`/`SampleNormal`/`SampleBiome`/`WaterLevel`) folds biome-band shaping, stateless coordinate-hash fractal noise (`TerrainNoise`), and ordered features (`LakeFeature`/`RidgeFeature`/`FlattenFeature`); height at a point depends only on `(x, z, seed)`. Plus `TerrainCollision` (ground height + slope walkability) and `TerrainPresets.Clearing()`. Plain `float`; server and client sample the same field. In the `Foundation` umbrella. | Primitives |
| **KhaozEngine.Locomotion** | Render-free character locomotion core: `CharacterMovement.Step` from a `MoveCommand` (camera-relative WASD axis + run + camera yaw + jump) over a timestep, normalized diagonals, ground-clamped via a height delegate with an optional slope gate. Two overloads share one horizontal core: `Step(Vector3,...)` (horizontal-only, Y instant-clamped) and the vertical-physics `Step(in MoveState,...)` (gravity + jump with coyote-time/jump-buffer + air control over a carried `MoveState`, plus optional 3D prop collision via `IPhysicsWorld?` (swept collide-and-slide: trap-proof against one-sided building meshes, walkable-surface following, and a `StepHeight` step-up so low stairs/curbs are walkable)). One `MoveTuning` source of truth (speeds + gravity/jump/fall/feel) shared by the local `CharacterController3D`, the authoritative server sim, and client prediction. No input/render/netcode. In the `Foundation` umbrella. | Primitives, Physics |
| **KhaozEngine.Determinism** | `DeterministicFpScope` / `DeterministicFp`: pins the CPU floating-point environment to a canonical IEEE state (round-to-nearest-even, FTZ/DAZ off, traps masked) for a fixed-tick / lockstep sim, then restores it, so a fixed-seed host sim doesn't drift across threads/machines. Pure-managed P/Invoke over `<fenv.h>`; `IsSupported` no-ops safely where unwired. | Pure .NET |
| **KhaozEngine.Updates** | Delta auto-update pipeline: SHA256 manifests + diffing, a host-agnostic update source, an `UpdateService` state machine with resumable staged downloads, and a cross-platform staged-apply core (`UpdateApplier`). | Diagnostics |
| **KhaozEngine.Netcode.Abstractions** | The zero-dependency channel-split contract: `IChannelSplittable<TSelf>` + `NetChannelReliability`. Reference this alone from a transport-agnostic DTO project (e.g. one shared with a web server). | Pure .NET |
| **KhaozEngine.Netcode** | Transport-free netcode primitives (`System.Numerics`): `UnitAxisQuantizer` (deterministic 8-bit axis codec), `ClientPrediction<TState,TCommand>`, `RemoteCommandQueue<TCommand>`, and the `INetTransport` byte-transport seam (`NetConnectionId`/`NetEvent`) with a deterministic in-memory `LoopbackTransport` for headless tests + local play; the `NetServer`/`NetClient` session layer (Hello/Welcome handshake, slot assignment, `IConnectionAuthenticator` gate that returns the verified subject) plus `SignedToken` + `HmacTokenAuthenticator`, a zero-dependency HMAC-SHA256 signed connect-token; `RateLimiter` (a deterministic per-connection token bucket for message-flood protection) and `NetServer.Disconnect(slot)` (a kick seam); `BoundedEventQueue<T>` (a drop-oldest hard cap the `NetServer` session inbox uses so a stalled or flooded host can't grow undrained events without bound, drops counted in `DroppedEventCount`); `NetTransportStats` + the default-interface `INetTransport.Stats` (RTT / loss / byte counters, `Unavailable` by default, filled by the LiteNetLib UDP binding) forwarded via `NetClient.TransportStats`. Type-forwards the channel-split contract from Abstractions. | Netcode.Abstractions |
| **KhaozEngine.Netcode.LiteNetLib** | LiteNetLib transport binding: `ChannelSplitter` maps `NetChannelReliability` to LiteNetLib's `DeliveryMethod`, plus `LiteNetLibServerTransport`/`LiteNetLibClientTransport` (`INetTransport` over reliable-UDP; their raw event inboxes are bounded the same way via `BoundedEventQueue<T>` - optional `maxQueuedEvents` + `DroppedEventCount`). | LiteNetLib, Netcode |
| **KhaozEngine.Simulation** | Headless simulation-host primitives: `FixedTickHost`, a deterministic fixed-timestep accumulator (turns variable elapsed time into whole fixed-dt ticks, with a spiral-of-death backlog guard) that decouples sim rate from render rate; and `IJobScheduler`, the engine's worker-pool seam (`SingleThreadedJobScheduler` inline default + `ThreadPoolJobScheduler` over `Parallel.For`) for fanning independent jobs across cores. The base of the authoritative server loop. | Pure .NET |
| **KhaozEngine.Replication** | Authoritative ECS replication: `NetId` + a closure-based `ReplicationRegistry`, `SnapshotWriter` (full-state + `WriteFiltered` per-client interest), `ClientReplicationView` (`Apply`/`ApplyDelta`: spawn/despawn/update + interpolation), `ServerReplicator` (per-slot acked baselines + baseline/delta), and `InterestGrid` (area-of-interest spatial query). Transport-free (snapshots are `byte[]`). | Ecs |
| **KhaozEngine.WorldStore** | Server-side durable-state seam: `IWorldStore` (async keyed `byte[]`, DB-shaped) + a thread-safe `InMemoryWorldStore` reference impl. Durable backends are the two opt-in packages below. | Pure .NET |
| **KhaozEngine.WorldStore.Sqlite** | SQLite `IWorldStore` backend over `Microsoft.Data.Sqlite` (`SqliteWorldStore`): one `world_store` table, `INSERT ... ON CONFLICT` upsert, raw parameterized async ADO.NET, no EF/ORM. The zero-infra dev/test + single-node backend (keeps persistence headless-testable). Opt-in (NOT in the `Server` umbrella; add explicitly alongside it). | WorldStore, Microsoft.Data.Sqlite |
| **KhaozEngine.WorldStore.SqlServer** | SQL Server / Azure SQL `IWorldStore` backend over `Microsoft.Data.SqlClient` (`SqlServerWorldStore`): `MERGE WITH (HOLDLOCK)` upsert, raw parameterized async ADO.NET, no EF/ORM. The production backend (Azure SQL); same contract as the SQLite one. Opt-in (NOT in the `Server` umbrella; add explicitly alongside it). | WorldStore, Microsoft.Data.SqlClient |
| **KhaozEngine.Sharding** | World topology for an authoritative server: a uniform grid of authoritative cells. `CellCoord` (world position -> integer cell coord), `CellSim` (one cell = an ECS `World` + `FixedTickHost` + `ServerReplicator` + `InterestGrid` + read-only border ghosts), `ShardHost` (owns the cell map, creates cells on demand, routes entities to the cell containing their position, ticks every cell at one fixed rate, `SyncGhosts` mirrors border-overlap entities into neighbor cells as `Ghost`s over the `ICellLink` seam, `ProcessHandoffs` transfers authority on a boundary crossing with exactly-once semantics, and `SnapshotForClient` serves a bound client its whole area-of-interest from its single home cell with seamless re-bind on crossing). `Tick` fans the independent cells across an opt-in `IJobScheduler` (default inline) for near-linear-in-cores throughput. The in-process container the seamless-shard topology builds on. | Ecs, Simulation, Replication |
| **KhaozEngine.NetWorld** | Render-free networked-world layer wiring movement to the authoritative netcode stack: `PlayerMoveState` (wraps a `Locomotion.MoveState`: position + vertical velocity + grounded) + the replicated `MovementState` component carrying the vertical axis on the wire (survives a sharded handoff, forms the client's exact reconcile basis); `PlayerMoveSimulator`/`PlayerMovementSystem` (run `CharacterMovement.Step` server-authoritatively and inside client prediction), `WorldServer`/`ShardedWorldServer` (authoritative sim + per-client AoI snapshots over `SnapshotWriter`+`InterestGrid`, headered with the receiver's net id + last-acked seq, with an opt-in server-side anti-cheat layer - `AntiCheatConfig`: per-connection message rate limiting + an `OnSuspiciousActivity` signal hook for malformed/NaN, flood, and movement-correction anomalies - plus a `Disconnect(slot)` kick seam), and `WorldClient` (wraps `NetClient`+`ClientReplicationView`+`ClientPrediction`, reconciles position + the vertical axis, exposes `EntityRenderState[]`: local predicted, remotes replicated and smoothly interpolated between snapshots by default (a remote glides instead of teleporting one ~tick-rate snapshot-step per ingest, driven by `AdvancePresentation`; opt out with `WorldClientConfig.InterpolateRemotes = false`), each carrying the exact grounded flag + vertical velocity (local predicted, remote from the replicated `MovementState`) so an animator bridge reads jump/fall for remotes too; optional `WorldBounds`/`IPhysicsWorld?` ctor params mirror `WorldServer` so prediction runs against the same bound + solid props, no rubber-banding). Also `WorldPersistence` (+ `PlayerRecord`): wires an `IWorldStore` into the server lifecycle (load-on-join / save-on-leave / periodic dirty snapshot) so the world survives a restart, backend-agnostic. `WorldClient.NetStats` (`ClientNetStats`) surfaces connection health for a telemetry overlay: RTT / loss / byte rates from the transport, the AoI snapshot ingest rate, and the prediction-correction magnitude (last + rolling avg). `WorldClient.LocalHorizontalSpeed` (8.7.0) gives the local player's predicted planar speed (off `ClientPrediction.PredictedHorizontalSpeed`, computed per prediction tick, immune to reconciliation snaps) for a HUD / audio / locomotion blend, alongside `LocalGrounded` / `LocalVerticalVelocity`. Client self-rescue (8.6.0): `WorldClient.RequestSelfRescue()` asks the server to teleport the local player to a server-decided safe spot (an "unstuck"), gated by `WorldServerConfig.SelfRescueDestination` (null = off) + `SelfRescueCooldownSeconds` on both servers; reuses the admin Teleport apply path, rides a length-distinct control frame (`MoveProtocol.ClientControlKind`) so older servers ignore it. Reconnect input backlog (8.8.0): `WorldClient.SendInput` no-ops (returns `-1`) unless connected, so a per-frame send loop builds no stale-input backlog across a long reconnect outage, and `WorldServerConfig.MaxInputBacklog` / `ShardedWorldServerConfig.MaxInputBacklog` (default 8 ticks) caps how far behind live the server falls under a deep backlog (skip-to-newest, latest-wins), so a player is never frozen under minutes of old input on rejoin. In the `Server` umbrella. | Locomotion, Physics, Diagnostics, Collision, Netcode, Replication, Ecs, Sharding, WorldStore, Serialization |
| **KhaozEngine.Server.Admin** | Opt-in HTTPS admin endpoint (Kestrel + bearer token over TLS) over the `ServerAdmin` surface (list/teleport/kick/broadcast, account enumeration, ban/unban). The only package that references ASP.NET Core; not in the `Server` umbrella. | NetWorld, WorldStore, Microsoft.AspNetCore.App |
| **KhaozEngine.Content.Validator** | Build-time JSON-schema enforcement tool for content (`IsPackable=false`; ships inside the Content package). | Content |
| **KhaozEngine.Sfx.Tool** | The `ke-sfxbake` dotnet tool (`PackAsTool`): manifest-driven bulk SFX generation + bake. Reads a per-game `sfx.manifest.jsonc`, generates each effect via the ElevenLabs sound-effects REST API, encodes with ffmpeg/oggenc, idempotent via `.sfxmeta` sidecars. Author-time tool, not a runtime package. | Serialization |
| **KhaozEngine.PropSurface.Tool** | The `ke-propbake` dotnet tool (`PackAsTool`): bakes a 3D collision `.coll` shape for every prop (trees get a leaning trunk-hull collider) and a walkable-surface `.surf` heightmap for walkable-solid props (rocks/logs/buildings) in a kit manifest, stamping the `surface`/`heightmap` fields and feeding `PropCollisionLoader` for physics wiring. Run as the last kit-ingest step (re-ingest = re-bake). Author-time tool, not a runtime package. | Render3D |

**Umbrella metapackages** (code-free curated dependency groups - one `<PackageReference>` instead of a dozen):

| Metapackage | Pulls in | For |
|---|---|---|
| **KhaozEngine.Game2D** | 2D runtime (Windowing/Render2D/Gui/Audio/Particles/Effects/Telegraphs) + `Game` + `Foundation` | a desktop 2D game |
| **KhaozEngine.Game3D** | `Game2D` + `Render3D` + `Game.Render3D` (the 3D scene bridge) + `Telegraphs.Render3D` + `Terrain.Render3D` (chunked-LOD terrain mesh + world streaming) | a desktop 3D game |
| **KhaozEngine.Server** | `Foundation` + netcode (`Netcode`/`.Abstractions`/`.LiteNetLib`) + `Simulation` (fixed-tick host) + `Replication` + `WorldStore` (the `IWorldStore` seam + `InMemoryWorldStore` only; the `.Sqlite` / `.SqlServer` durable backends are opt-in siblings, added explicitly, not bundled) + `Sharding` (cell grid) + `NetWorld` (authoritative movement server + client glue + `WorldPersistence`) | a headless sim server (no GPU) |
| **KhaozEngine.Foundation** | the GPU-free foundation (Primitives/App/Content/Diagnostics/Ecs/Localization/Locomotion/Persistence/Serialization/Pooling/Collision/Physics/Terrain/Determinism/Platform/Updates) | a gameplay-logic library (no renderer) |

Target framework `net10.0`. MonoGame-free: Silk.NET windowing/input (GLFW natives bundled per-RID), Veldrid
behind `KhaozEngine.Gpu` for the GPU, Silk.NET.OpenAL for audio. `System.Numerics` math throughout.

## Why it exists

KhaozEngine began as a shared input + screen-stack foundation extracted from three MonoGame games (a
Rule-of-Three extraction), with one improvement: the raw hardware read sits behind a seam so the whole
input + routing surface is **unit-testable without a device**. It then grew into a full custom stack and the
games migrated **off MonoGame entirely** - MonoGame's GLSL-1.20-on-Apple dead end forced a custom Veldrid/Metal
renderer, and once 2D, 3D, text, audio, and input were all proven on the custom stack the legacy MonoGame
packages were deleted. The headline payoff is unchanged: the **click-through fix** (a tap only registers when
press-origin and release land in the same target, and overlays reserve their footprint so clicks never leak to
the layer beneath) lives in one place. See [`docs/ROADMAP.md`](docs/ROADMAP.md), "The post-MonoGame pivot".

## The one rule that matters most

> **`AppWindow` is the only code in the entire stack that touches the Silk.NET/GLFW input.** Everything above it
> reads an immutable `InputState` snapshot (handed in each frame via `Frame.Input`) through `InputManager` /
> `Pointer`. Games must not reach around the seam - doing so re-introduces the untestable, click-through-leaking
> pattern this library exists to kill. And hit-test with the bounds helpers (`IsTapIn`, …), never raw
> position + button.

Full consumer contract: [`docs/USING-KHAOZENGINE.md`](docs/USING-KHAOZENGINE.md). Read it before wiring a game in.
All docs are indexed in [`docs/INDEX.md`](docs/INDEX.md) (living docs vs the dated design archive).

## Quickstart (the canonical game-loop wiring)

A game subclasses `GameApp` (2D) or `GameApp3D` (3D) and overrides the per-frame seams; the base owns the
`AppWindow.Run` loop, clock, viewport, input, and the 2D batch. The smallest thing that runs:

```csharp
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

public sealed class MyGame : GameApp
{
    public MyGame() : base(GameAppOptions.For("My Game", 1280, 720)) { }

    protected override void OnUpdate(float dt) { if (Input.WasPressed(Key.Escape)) Quit(); }
    protected override void OnDraw2D(SpriteBatch batch) { /* batch.Draw(...) / DrawString(...) */ }
}

// Program.cs
using var game = new MyGame();
game.Run();
```

Full wiring reference (the 3D `OnDraw3D`/`GameApp3D` pass, scenes, input, fonts, the click-through-safe
`Pointer.IsTapIn` pattern): [`docs/USING-KHAOZENGINE.md`](docs/USING-KHAOZENGINE.md), "Wiring a game".

## Consuming the packages

Published to a private GitHub Packages feed on tagged releases, and packed to a local file-feed for day-to-day development.

```xml
<!-- nuget.config (additive) -->
<add key="khaozengine-local" value="/Users/antonio/KhaozEngine/local-feed" />
<!-- or the GitHub Packages feed: https://nuget.pkg.github.com/APKiwi/index.json -->
```
```xml
<!-- One reference per project via an umbrella metapackage. Pick the bundle that fits: -->
<PackageReference Include="KhaozEngine.Game2D"     Version="8.8.1" />  <!-- desktop 2D: 2D runtime + GameApp/SceneManager + foundation -->
<PackageReference Include="KhaozEngine.Game3D"     Version="8.8.1" />  <!-- desktop 3D: Game2D + Render3D + the 3D scene bridge -->
<PackageReference Include="KhaozEngine.Server"     Version="8.8.1" />  <!-- headless: foundation + netcode, no graphics -->
<PackageReference Include="KhaozEngine.Foundation" Version="8.8.1" />  <!-- gameplay-logic lib: foundation only, no renderer/netcode -->
```

The metapackages have no code; they just pull in the granular packages. You can still reference those
directly (e.g. just `KhaozEngine.Netcode.Abstractions` for a wire-contract project) and mix a bundle with extra
packages (e.g. `KhaozEngine.Game2D` + `KhaozEngine.Netcode.LiteNetLib` for a 2D multiplayer game).

**Versioning is SemVer.** Each game pins a version and adopts fixes by bumping it - so you can keep one game on an old version while you migrate another. Don't fork the packages; if a game needs an API that isn't there, add it here and bump the version.

## Testability standard

Every input and routing path is covered by `KhaozEngine.Tests` (xUnit), headless, by constructing `InputState`
snapshots frame-by-frame and feeding them to `InputManager.Update` (`dt` is a plain `float` in seconds). New
behaviour added to the library ships with a headless test. This is the standard, not a nicety - it's the reason
the raw read sits behind the `AppWindow`/`InputState` seam.

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
```

## Repo layout

```
# Custom render/runtime stack
KhaozEngine.Gpu/   KhaozEngine.Windowing/   KhaozEngine.Render2D/   KhaozEngine.Render3D/   KhaozEngine.Gui/
KhaozEngine.Audio/   KhaozEngine.Particles/   KhaozEngine.Effects/   KhaozEngine.Game/   KhaozEngine.Game.Render3D/
KhaozEngine.Telegraphs/   KhaozEngine.Telegraphs.Render3D/   KhaozEngine.Terrain.Render3D/
# Foundation (GPU-free, pure .NET)
KhaozEngine.Primitives/
KhaozEngine.Ecs/   KhaozEngine.Serialization/   KhaozEngine.Content/   KhaozEngine.Content.Validator/
KhaozEngine.Diagnostics/   KhaozEngine.App/   KhaozEngine.Localization/   KhaozEngine.Persistence/
KhaozEngine.Pooling/   KhaozEngine.Platform/   KhaozEngine.Collision/   KhaozEngine.Physics/   KhaozEngine.Physics.Bepu/
KhaozEngine.Terrain/   KhaozEngine.Updates/
KhaozEngine.Locomotion/
KhaozEngine.Netcode/   KhaozEngine.Netcode.Abstractions/   KhaozEngine.Netcode.LiteNetLib/   KhaozEngine.Simulation/
KhaozEngine.Replication/   KhaozEngine.WorldStore/   KhaozEngine.WorldStore.Sqlite/   KhaozEngine.WorldStore.SqlServer/
KhaozEngine.Sharding/   KhaozEngine.NetWorld/
KhaozEngine.Server.Admin/      Opt-in HTTPS admin endpoint (Kestrel) over ServerAdmin
# Umbrella metapackages
KhaozEngine.Foundation/   KhaozEngine.Game2D/   KhaozEngine.Game3D/   KhaozEngine.Server/
# Tests, samples, tools
KhaozEngine.Tests/   GuiSample/   Render2DSample/   Render3DSample/   SceneSample/   WindowingSample/   MiniGame/
SnapshotSample/   TerrainWalkSample/   MmoServerSample/ (reference dedicated MMO server)
NetworkedWalkServer/ + NetworkedWalkSample/ (networked walkable overworld: headless server + windowed client)
KhaozEngine.Updates.Tool/ (ke-updater)   KhaozEngine.Sfx.Tool/ (ke-sfxbake)   KhaozEngine.PropSurface.Tool/ (ke-propbake)
tools/   docs/USING-KHAOZENGINE.md
Directory.Build.props (shared version)   nuget.config   .github/workflows/ci.yml
```

CI builds, tests, packs, and on a `v*` tag publishes to GitHub Packages.

## Running the samples

Each sample is a runnable head (`dotnet run --project <name>`) proving one slice of the engine, no MonoGame.
The windowed ones open a GPU window (need a display); the server / snapshot heads are headless.

**Interactive (windowed)** - open a window, drive with keyboard + mouse:

| Sample | Demonstrates | Run | Controls |
|---|---|---|---|
| `WindowingSample` | Windowing + **input**: gestures (drag/tap/long-press), `GameClock` pause + time-scale, clipboard | `dotnet run --project WindowingSample` | Space pause, 1/2/3 slow/normal/fast, drag / tap / long-press, Esc quit |
| `GuiSample` | **Gui** screen stack: a menu pushes modal Settings / Widgets / Immediate screens | `dotnet run --project GuiSample` | Click buttons, Esc quit |
| `SceneSample` | `SceneManager` push / switch / overlay / pop | `dotnet run --project SceneSample` | Key or click to start, Esc pushes a pause overlay, Esc again pops |
| `Render2DSample` | 2D: sprites, text, alpha blend, batched quads | `dotnet run --project Render2DSample` | Esc quit |
| `Render3DSample` | 3D mesh viewer + retro post (cel / outline / palette) | `dotnet run --project Render3DSample` | Space model, O outline, A starfield, R retro, C cel, P palette, W/S zoom, arrows orbit, Esc quit |
| `TerrainWalkSample` | **Walkable streamed 3D overworld** (follow camera + character controller, endless chunk streaming) | `dotnet run --project TerrainWalkSample` | WASD move, mouse-drag orbit, scroll zoom, Shift run, Esc quit |
| `MiniGame` | A whole tiny game (Windowing + Render2D + Gui + Audio): "Catcher" | `dotnet run --project MiniGame` | A/D or arrows move, Esc quit |

**Networked** - run the server, then one or two clients (two clients = two players on the same terrain):

```bash
dotnet run --project NetworkedWalkServer        # headless authoritative server on UDP :47700
dotnet run --project NetworkedWalkSample        # windowed client; same controls as TerrainWalkSample
# optional args: NetworkedWalkSample [host] [port]   (defaults 127.0.0.1 47700)
```

**Headless (no window)**:

| Sample | What it does | Run |
|---|---|---|
| `SnapshotSample` | Writes a 2D + a 3D PNG via the snapshot harness (needs a GPU device, not a display) | `dotnet run --project SnapshotSample -- /tmp/ke-snapshot-demo` |
| `MmoServerSample` | Reference dedicated MMO server (cell grid + seamless handoff) on a UDP socket | `dotnet run --project MmoServerSample` |

**Windowed smoke (CI / quick check).** Every windowed sample honors `KE_MAX_FRAMES=N`: render N frames, then
exit 0. So any of them doubles as a smoke test on a GPU box without needing someone to close the window:

```bash
KE_MAX_FRAMES=5 dotnet run --project TerrainWalkSample
```

`Render2DSample` and `Render3DSample` also take `--smoke` (capture one frame, print a pass/fail line, exit with a
code), with extra `Render3DSample` flags `--retro --pico --gb --asteroid`, e.g.:

```bash
dotnet run --project Render3DSample -- --smoke --retro --gb
```

## Dev tools

Author-time dotnet tools that ship as packages on the shared version line (not runtime dependencies):

- **`ke-sfxbake`** (`KhaozEngine.Sfx.Tool`) - manifest-driven bulk SFX generation + bake. Each game owns a
  `sfx.manifest.jsonc` describing its effects; the tool generates them via the ElevenLabs sound-effects API and
  encodes into the asset tree, skipping anything already up to date (`.sfxmeta` hash sidecars). Needs
  `ELEVENLABS_API_KEY`, plus an ffmpeg with libvorbis or `oggenc` (vorbis-tools) for OGG output.

  ```bash
  export ELEVENLABS_API_KEY=...           # already set on dev machines
  ke-sfxbake bake path/to/sfx.manifest.jsonc --dry-run   # plan + estimated credits, spends nothing
  ke-sfxbake bake path/to/sfx.manifest.jsonc             # generate new/changed, skip unchanged
  ke-sfxbake bake path/to/sfx.manifest.jsonc --force     # regenerate everything
  ```

  ```jsonc
  // sfx.manifest.jsonc - paths resolve relative to this file
  {
    "sounds": [
      { "key": "ui/confirm", "prompt": "crisp sci-fi UI confirm blip, short synth tail",
        "durationSeconds": 1.2, "out": "Assets/Sfx/ui/confirm.ogg" },          // mono OGG (default)
      { "key": "ui/click", "prompt": "soft latency click", "format": "wav",
        "out": "Assets/Sfx/ui/click.wav" },                                     // 16-bit PCM WAV for one-shots
    ],
  }
  ```

- **`ke-updater`** (`KhaozEngine.Updates.Tool`) - generate, sign, and verify update manifests (RSA-2048).

## Consumers

| Game | References | Status |
|---|---|---|
| **Hardpoint** (3D) | `KhaozEngine.Game3D` (head) + `KhaozEngine.Foundation` (logic) | On 7.68.0, fully off MonoGame. |
| **Nullwake** (2D) | `KhaozEngine.Game2D` | On 7.68.0, fully off MonoGame. Source of the widgets, transitions, and the click-through fix. |
| **SpaceGame** (2D) | `KhaozEngine.Game2D` (head) + foundation pins on `SpaceGame.Sim` | On 7.68.0, fully off MonoGame. Deterministic lockstep sim split into `SpaceGame.Sim`. |
| **Ruinborne** (3D MMO) | `KhaozEngine.Game3D` + `NetWorld` (client) + `KhaozEngine.Server` + `WorldStore.SqlServer` (server) | On 7.68.0. Authoritative networked overworld, Azure SQL persistence. |

Full per-package version + adoption matrix: [`docs/CONSUMERS.md`](docs/CONSUMERS.md).
