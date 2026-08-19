# Dependency seams: how third-party libraries stay swappable

KhaozEngine wraps almost every third-party dependency behind a **seam**: a dependency-free contract
(interfaces + value types) that the rest of the engine codes against, with the real library contained in a
separate, opt-in backend that nobody else references. [RENDER-PIPELINE.md](RENDER-PIPELINE.md) and
[PHYSICS-PIPELINE.md](PHYSICS-PIPELINE.md) are the two worked examples; this doc is the convention they share
and the map of every place it is applied.

## The rule

```mermaid
flowchart TD
    GAME["Game / engine code<br/>(codes against the seam only)"]
    SEAM["Seam package - dependency-free contract<br/>interfaces + value types, System.Numerics at most"]
    BACK["Backend package - the ONLY assembly<br/>referencing the third-party library"]
    LIB["Third-party library"]

    GAME --> SEAM
    SEAM -. implemented by .-> BACK
    BACK --> LIB
```

Three properties fall out of it:

- **Headless-testable.** The seam has no native or GPU dependency, so behaviour is tested against the contract
  (or a fake/loopback/in-memory impl) with no real device. New behaviour ships with a headless test.
- **Swappable.** A second backend is a new `KhaozEngine.<Area>.<X>` package; upstream code is untouched.
- **Pay-for-what-you-use.** Backends that pull a heavy or platform-specific dependency are **opt-in** (not in
  any umbrella metapackage) and added by id, so a consumer that does not need them does not drag them in.

### Mechanically enforced

These graph rules are not just prose. Headless architecture tests in `KhaozEngine.Tests` read the real
`*.csproj` files and fail CI when an edit breaks them:

- `ArchitectureTests.cs` - third-party containment (every third-party PackageReference stays in its allowlisted
  seam/backend home, and any new one must be added to the allowlist deliberately), the layering invariants
  (`Primitives` is the zero-dependency leaf, `Simulation` may reference `Determinism` and nothing else, the
  Foundation umbrella stays GPU-free, `App` never references `Gui`), the locked ProjectReference membership of the four umbrellas, opt-in backends staying out of
  every umbrella's transitive closure, `Render3D` staying seams-only, and the native Direct3D 11 backend
  declaring no `Veldrid` package of its own.
- `GpuPublicApiTests.cs` - reflection guards that walk the public and protected surface of `KhaozEngine.Gpu` and
  fail if any Veldrid type leaks through it, proving the GPU seam keeps Veldrid contained. The same walk runs
  over `KhaozEngine.Gpu.D3D11` for `Veldrid`, `Vortice` and `SharpGen`, since a Direct3D type in a public
  signature would load a Windows-only assembly on a platform that has none. A third guard reads that assembly's
  own references and fails on any `Veldrid` one, which is the only way to catch a Veldrid type crossing through
  an INTERNAL API that no surface scan looks at.

Changing a documented edge therefore means changing the matching expectation in these tests, so the graph and
this doc cannot silently drift apart.

## Every seam in the engine

| Area | Seam (dependency-free) | Backend(s) | Third-party library |
|---|---|---|---|
| GPU / rendering | `KhaozEngine.Gpu` (`GpuDeviceContext` + `GpuInterfaces`, `GpuBackendSelector`, `GpuBackendProviders` / `IGpuBackendProvider` for a backend that ships outside this package, and `GpuRecording`, the mandatory open-recording gate described below) | (in-package) `Internal/VeldridGpuDevice`, which is all four Veldrid legs including Metal, plus THREE engine-owned native backends that ship outside it, each opt-in and in no umbrella: `KhaozEngine.Gpu.D3D11` (`KhaozEngineD3D11.Register()`), `KhaozEngine.Gpu.Vulkan` (`KhaozEngineVulkan.Register()`) and `KhaozEngine.Gpu.Metal` (`KhaozEngineMetal.Register()`), and any other registered out-of-package provider | Veldrid (+ Veldrid.SPIRV, Vortice.Direct3D11) in the seam. Vortice.Direct3D11 + Vortice.D3DCompiler in the D3D11 backend and Silk.NET.Vulkan (+ its KHR and EXT extension packages) in the Vulkan one. The Metal backend declares NO third-party binding at all: its Objective-C interop is engine-owned `libobjc` and `Metal.framework` P/Invoke. None of the three declares a Veldrid package of its own |
| 3D physics | `KhaozEngine.Physics` (`IPhysicsWorld`, value-type shapes/poses/queries, and since the floating-origin release also the default-method `Origin` / `CanRebase` / `Rebase(newOrigin)`, so an existing implementer keeps compiling and correctly reports that it cannot rebase - the seam learns a plain `Vector3`, never a frame type, and keeps its zero project references) | `KhaozEngine.Physics.Bepu` (`BepuPhysicsWorld`, which overrides all three: bulk direct pose writes plus broadphase refits over every allocated body set and the statics, so sleeping bodies, contacts and constraints all survive a shift) | BepuPhysics v2 |
| Netcode transport | `KhaozEngine.Netcode` (`INetTransport` incl. the default-method `Stats` -> `NetTransportStats`, `LoopbackTransport`) + `Netcode.Abstractions` | `KhaozEngine.Netcode.LiteNetLib` (fills `Stats` via `EnableStatistics`) | LiteNetLib |
| Persistence | `KhaozEngine.WorldStore` (`IWorldStore`, `InMemoryWorldStore`) | `WorldStore.Sqlite`, `WorldStore.SqlServer` | Microsoft.Data.Sqlite / SqlClient |
| Commerce wallet | `KhaozEngine.Commerce` (`IWalletStore`, `IGrantScheduleStore`, `IEntitlementValidator`, `InMemoryWalletStore`) | `Commerce.Sqlite` (`SqliteWalletStore`), `Commerce.SqlServer` (`SqlServerWalletStore`) | Microsoft.Data.Sqlite / SqlClient |
| Persistence enumeration | `KhaozEngine.WorldStore` (`IEnumerableWorldStore`, `WorldStoreEntry`) | `InMemoryWorldStore`, `SqliteWorldStore`, `SqlServerWorldStore` (all three implement it) | (no extra dep; streaming `EnumerateAsync(keyPrefix?)`) |
| Server ban list | `KhaozEngine.NetWorld` (`IBanStore`, `InMemoryBanStore`) | `WorldStoreBanStore` (persists over any `IWorldStore` keyspace `ban:{accountId}`) | (no extra dep; sync `IsBanned` via in-memory cache, `LoadAsync()` at startup) |
| Player persistence | `KhaozEngine.NetWorld` (`IWorldPersistenceHost`, the surface `WorldPersistence` drives, implemented by BOTH `WorldServer` and `ShardedWorldServer`: `PlayerJoined` / `PlayerLeaving` / `SetPlayerState` / `JoinedSlots` / `TryGetAccountId` / `TryGetPlayerState`, and since 17.37.0 the join-seed pair `SetResumePositionProvider` + `TryGetConfiguredSpawn`, both default interface methods so an existing implementer keeps compiling and simply keeps spawning every join at its configured spawn) | `WorldPersistence` (+ `WorldPersistenceConfig`, `PlayerRecord`, and the `ResumePositionCache` behind the join seed) wires it to any `IWorldStore`, keyed `player:{accountId}` (a TOKENLESS connection, which both heads hand over as `guest:{slot}`, is not persisted at all unless `WorldPersistenceConfig.PersistGuests` is set, and then under a durable per-session key rather than the seat) | (no extra dep, it rides whichever `IWorldStore` backend the game chose) |
| Per-cell world persistence | `KhaozEngine.NetWorld` (`ICellPersistenceHost`, the surface `CellPersistence` drives, `ShardedWorldServer` implements it. Since 9.33.0 the host also carries `TryRestoreCell` for a non-throwing quarantining restore, a default interface method so existing implementers are unaffected. Since 10.0.0 the NetId high-water it carries - `NextNetId` / `EnsureNextNetIdAtLeast` - and the restored-id list are 64-bit `long`, and since 17.38.0 `SnapshotCell` skips any entity tagged `KhaozEngine.Sharding.Transient`, the per-entity persist opt-out, which needs nothing of the host, and the host also offers `Registry`, another default interface method, which `CellPersistence` takes as the default for `CellPersistenceConfig.Registry`, so a pre-v4 blob's generation is inferred against the host's live registry without the consumer wiring the same object in twice) | `CellPersistence` (+ `CellPersistenceConfig` with `RegisterMigration` + engine-provided migrations via `IncludeEngineMigrations`, `WorldMetaRecord`) wires it to any `IWorldStore`, migrating / quarantining / retaining on load and surfacing `CellPersistence.Issue` | Microsoft.Data.Sqlite / SqlClient (via the `IWorldStore` backend already chosen) |
| Cell eviction (unloading idle cells) | `KhaozEngine.NetWorld` (`ICellEvictionHost`, which EXTENDS `ICellPersistenceHost` with `CanEvictCell` / `EvictCell` / `TryReadEvictionSignals`, implemented by `ShardedWorldServer`), plus the policy seam `KhaozEngine.Sharding.ICellEvictionPolicy` over `CellEvictionSignals`, which is the game's to replace | `CellEvictor` (+ `CellEvictionConfig`) persists each candidate through `CellPersistence` and removes it from the `ShardHost` only once the write lands, restoring an evicted coordinate on recreation. The shipped policy is `IdleCellEvictionPolicy` | (no extra dep, it rides whichever `IWorldStore` backend `CellPersistence` already has) |
| World pickups (walk-over collectibles) | `KhaozEngine.NetWorld` (`IWorldPickupHost`, the surface `WorldPickups` drives, implemented by BOTH `WorldServer` and `ShardedWorldServer` - `JoinedSlots` / `TryGetPlayerNetId` / `TryGetPlayerState` / `SpawnEntity` are their pre-existing API verbatim, plus `TryGetEntity` / `DespawnEntity`, the resolve-and-remove halves `SpawnEntity` had been missing, neither of which ever touches a player entity, and since 17.38.0 `TryGetCellCoord`, a default interface method answering false so a host with no cell grid is unaffected) | `WorldPickups` (+ `WorldPickupsConfig` carrying the `OnCollect` decision hook and `OnRemoved`, the replicated `PickupState` built-in at `MoveProtocol.PickupTypeId`) owns spawn, the owner tag, the time-to-live, the linear proximity scan and the despawn, marks every pickup `Transient` so none is ever persisted, and follows a `CellEvictor` (`WorldPickupsConfig.Evictor` / `TrackEvictions`, with `ForgetCell` / `ForgetWhere` for a host that unloads its own way) so an evicted cell takes its pickups' tracking with it | (no extra dep. The payload is an opaque game-defined `long` the engine never interprets, and the ownership RULE lives in the consumer's `OnCollect`) |
| Audio | `KhaozEngine.Audio` (`IMusicBackend`, `ISfxBackend`, `Null*` no-op defaults) | (in-package) `OpenAlMusicBackend` / `OpenAlSfxBackend` | Silk.NET.OpenAL (+ NLayer mp3 / NVorbis ogg decode, contained) |
| Server-status fetch | `KhaozEngine.ServerStatus` (`IServerStatusSource`, `ServerStatusReport` wire contract, `ServerStatusClient`, `ServerStatusEvaluator`) | (in-package) `HttpServerStatusSource`, a fake source in tests | System.Net.Http (BCL `HttpClient`, contained in `HttpServerStatusSource`) |
| Server heartbeat (liveness) | `KhaozEngine.ServerStatus` (`IServerHeartbeatSink`, `ServerHeartbeat`, `Null`/`InMemory` reference sinks, `ServerHeartbeatService`) | **game-side** (the one-table upsert against the status DB), no engine backend package | Microsoft.Data.SqlClient / any - in the game, never the engine |
| Social / presence | `KhaozEngine.Social` (`ISocialProvider`, whose `TryInitialize` must be re-attemptable on the same instance and whose `IsConnected` is the RECOVERABLE way to report a dropped connection where a throw is the terminal one, value types, `NullSocialProvider` no-op, `SocialPresenceController` + `SocialPresenceState`/`SocialPresenceOptions`) | `KhaozEngine.Social.Discord` (`DiscordSocialProvider`) | none - hand-rolled Discord IPC over `System.IO.Pipes` / `System.Net.Sockets` (no third-party lib) |
| Player identity | `KhaozEngine.Identity` (`IIdentityProvider`, `IIdentityValidator`, `ITokenCache` + `FileTokenCache`, `IBrowserLauncher`, `ILoopbackListener`, `IdentitySession` orchestrator, `SessionToken` HMAC) | `KhaozEngine.Identity.Oidc` (`OidcClientProvider`, `OidcTokenValidator`, `SystemBrowserLauncher`, `HttpLoopbackListener`), `KhaozEngine.Identity.Discord` (`DiscordClientProvider`, `DiscordTokenValidator`) | Microsoft.IdentityModel.Protocols.OpenIdConnect + Microsoft.IdentityModel.JsonWebTokens (Oidc only); Discord backend has no third-party lib (plain HTTP token-introspection call) |
| Windowing / input | `KhaozEngine.Windowing` `AppWindow` is the sole toucher; everyone reads the immutable `InputState` via `InputManager`/`Pointer` (input IN), and drives gamepad rumble OUT through `AppWindow.Rumble` (`IRumble`; pure `RumbleMixer` + Silk `IRumbleOutput` sink; `NoopRumble` headless). GLFW backend exposes no motors, so rumble no-ops there | (containment, not a swap) | Silk.NET / GLFW |
| glTF load | `KhaozEngine.Render3D` `GltfLoader` (returns engine `GltfMesh`/`AnimationClip`/`Skeleton`) | (containment, in loader) | SharpGLTF |
| Image decode | `KhaozEngine.Render2D` `ImageRgba` (`Decode`/`Load` -> engine RGBA8 value type) | (containment, in `ImageRgba`) | StbImageSharp |
| Font rasterization | `KhaozEngine.Render2D` `SpriteFont` (glyphs baked to an engine texture atlas) | (containment, in `SpriteFont`) | StbTrueTypeSharp |
| Content validation | `KhaozEngine.Content` `JsonSchemaValidator` (`Validate` -> engine `ValidationReport`) | (containment, in validator) | JsonSchema.Net |
| MCP server protocol | The two dev tools, not engine packages: `KhaozEngine.MapEdit.Tool` (`Program.cs`, `ke-mapedit`) and `KhaozEngine.TileEdit.Tool` (`Program.cs`, `ke-tileedit`). No engine package references the SDK | (containment, not a swap) | ModelContextProtocol SDK |

`KhaozEngine.Sharding` gained the snapshot/restore primitives the per-cell persistence seam above is built on
(`CellSim.SnapshotOwned`/`RestoreOwned`/`MaxOwnedNetId`, `ShardHost.CellCreated`/`EnsureCell`) with no storage
dependency added: Sharding stays a pure ECS/Replication container and only returns/accepts `byte[]` snapshots.
Storage stays where it already lived, in `NetWorld` (`CellPersistence`) over `IWorldStore`. Cell eviction splits the
same way: the mechanical removal (`ShardHost.RemoveCell`/`CanRemoveCell`/`CellRemoved`) and the policy seam are
Sharding, while the persist-then-evict orchestration that decides a cell's bytes are durable is `NetWorld`
(`CellEvictor`). The inter-cell link seam grew two default-implemented members for it, `ICellLink.HasPending`
(eviction gate, defaults to `true` so a link that cannot answer blocks a lossy unload) and `ICellLink.Forget`
(defaults to a no-op), so an existing implementation is unaffected.

## Admin endpoint package edge

`KhaozEngine.Server.Admin` sits outside the seam/backend pattern: it is not a backend (there is no second
implementation of `IAdminControllable` - the seam is in `NetWorld`), but it is deliberately NOT in the `Server`
umbrella because it is the only package that references ASP.NET Core (via a `FrameworkReference` on
`Microsoft.AspNetCore.App`). Its dependency edge is:

```
KhaozEngine.Server.Admin -> KhaozEngine.NetWorld (IAdminControllable, IBanStore, ServerAdmin)
KhaozEngine.Server.Admin -> KhaozEngine.WorldStore (IEnumerableWorldStore)
KhaozEngine.Server.Admin -> Microsoft.AspNetCore.App [shared framework, FrameworkReference]
```

A server that does not want an admin HTTP endpoint never references `Server.Admin`, so the web stack stays
out of its dependency closure.

The game-registered admin action registry (`ServerAdmin.RegisterAction`/`ActionNames`/`TryGetAction`, since
10.131.0) lives on `ServerAdmin` itself, on the `NetWorld` side of this edge, not in `Server.Admin`. The
`/actions` HTTP routes in `Server.Admin` are a thin dispatch shell over that registry, so this edge is
unchanged: `Server.Admin` still only references `NetWorld`, `WorldStore`, and `Microsoft.AspNetCore.App`.

## Commerce wallet seams

`KhaozEngine.Commerce` splits into three seams, not one, because the wallet has three independent axes a
game can swap:

- **`IWalletStore`** - the durable, transactional wallet backing store. Credit/Debit are atomic and
  idempotent by an `idempotencyKey`, scoped per `(account, currency)`; `GetBalanceAsync`/`GetLedgerAsync`
  read it back. `InMemoryWalletStore` (in the core package) is the reference/test backend; `Commerce.Sqlite`
  and `Commerce.SqlServer` are the durable opt-in backends, same contract, same idempotency semantics.
- **`IGrantScheduleStore`** - persists the next-available instant per `(account, rewardId)` for
  `PeriodicGrant`. Last-write-wins is safe here because the wallet's credit idempotency key is the real
  double-grant guard, not this store. The first-ever (bootstrap) claim keys on a fixed sentinel retained
  in the wallet ledger, so it is a permanent one-shot: clearing this store while retaining the wallet
  ledger denies the re-grant. `InMemoryWalletStore` implements both `IWalletStore` and
  `IGrantScheduleStore`, and so do the two SQL backends.
- **`IEntitlementValidator`** - turns an untrusted external proof (`EntitlementProof`: a webhook body, a
  store receipt) into a `VerifiedEntitlement`, or null if invalid. No default implementation ships: the
  proof format and the trust decision are consumer-specific (a webhook signature, a platform receipt
  verification API), so this seam has no `InMemory*` reference impl, unlike the two store seams above.

Dependency edges:

```
KhaozEngine.Commerce -> KhaozEngine.Progression (PeriodicGrant is built on WallClockRewardSchedule)
KhaozEngine.Commerce.Sqlite -> KhaozEngine.Commerce, Microsoft.Data.Sqlite
KhaozEngine.Commerce.SqlServer -> KhaozEngine.Commerce, Microsoft.Data.SqlClient
```

Same shape as the `WorldStore` persistence seam above (dependency-free contract + opt-in SQL backends,
neither backend bundled in the `Server` umbrella), but a distinct seam: `IWorldStore` is opaque-bytes
last-write-wins and cannot express atomic increments or idempotency, which a currency ledger needs.

## Localization package edges

Compile-time localization enforcement adds two edges, both acyclic:

```
KhaozEngine.Gui -> KhaozEngine.App           (the LocalizedText sink type + StringId + LocalizationContext)
KhaozEngine.Game2D/Game3D -> KhaozEngine.Localization.Analyzers   (packed dependency, include="All", so the
                                                                   analyzer is applied in the consumer's build)
```

`App` is a pure-BCL foundation package (it references only `Diagnostics` and `Platform`, both GPU-free leaves)
and never references `Gui`, so the new `Gui -> App` edge introduces no cycle. `KhaozEngine.Localization.Analyzers` is a `netstandard2.0` Roslyn
analyzer with no runtime dependency; it ships its assembly under `analyzers/dotnet/cs` and flows to a game only
through the `Game2D`/`Game3D` umbrellas (a project that references neither never sees it). The marker attributes
it reads (`LocalizationExemptAttribute`, `LocalizationStringSinkAttribute`) live in `App`, so the analyzer keys
off fully-qualified names, not a hard reference.

A third edge is test-only:

```
KhaozEngine.Localization.TestKit -> KhaozEngine.App   (reads StringId.Key to extract keys from a game's key class)
```

`KhaozEngine.Localization.TestKit` is a coverage-test helper a game references from its **test project**. It is in
no umbrella (a runtime build never pulls it) and references `App` only to read `StringId`, so the edge is acyclic
and carries no weight into shipped game code.

The file-size ratchet adds one more analyzer edge, from every umbrella:

```
KhaozEngine.Game2D/Game3D/Server/Foundation -> KhaozEngine.CodeHealth.Analyzers   (packed dependency,
                                                    PrivateAssets="none", so the analyzer runs in the consumer's build)
```

`KhaozEngine.CodeHealth.Analyzers` is a `netstandard2.0` Roslyn analyzer with no runtime dependency. It ships its
assembly under `analyzers/dotnet/cs` and its `buildTransitive` props auto-discover the consumer's `.filesize-baseline`
and hand it to the analyzer as an `AdditionalFile`, so the edge is analyzer-only and drags nothing into shipped game
code. All four umbrellas carry it (a file-size ratchet is not renderer- or server-specific), so any project that
references an umbrella gets the ratchet.

## Self-relaunch seam: process control

Cooperative self-restart (`KhaozEngine.App.AppRelaunch`) adds one edge, acyclic:

```
KhaozEngine.App -> KhaozEngine.Platform   (IProcessControl: resolve the running exe/pid/args/managed entry,
                                           spawn a detached instance, wait for a pid to exit)
```

`IProcessControl` gained one member in 17.23.0, `CurrentManagedEntryPath`, so the seam can name the app under
`dotnet <app>.dll` where `CurrentExecutablePath` resolves to the shared dotnet muxer instead. It has a DEFAULT
implementation returning null, which keeps an existing external implementation of a shipped public interface
compiling, and null simply means that shape is not repaired. No new edge: it reads `Environment` like the members
beside it.

`Platform` is a zero-ProjectReference leaf (pure BCL P/Invoke), so `App -> Platform` cannot cycle and keeps
`App` GPU-free. The process operations are behind `IProcessControl` (the seam) so the relaunch orchestration
is headless-testable against a fake; `ProcessControl.System` is the real one.

This is the generalized form of the desktop auto-updater's parent-pid-wait relaunch. The updater
(`KhaozEngine.Updates`) keeps its own `IUpdaterEnvironment` with tuned, updater-specific behaviour (antivirus /
torn-image retry, elevation, relocation, and a deliberately non-truthful `IsProcessAlive` on POSIX), so it was
NOT retrofitted onto `IProcessControl` - the two share the *pattern* (start a successor, have it wait on the
predecessor's pid so it never races the file the predecessor writes during shutdown), not the code. Collapsing
part of the updater's env onto the new primitive would split its process ops across two homes for no real gain,
so they stay separate.

## Single-instance guard seam: foreground signaling

`KhaozEngine.App.SingleInstanceGuard` adds one testability seam, no new package edge into the umbrella
closures (the packages it touches were already reachable):

```
KhaozEngine.App -> (BCL only: System.Threading.Mutex, System.IO)   (ISingleInstanceLock / SystemSingleInstanceLock)
KhaozEngine.Game -> KhaozEngine.App           (was already transitive via Gui -> App; made direct because
                                               GameApp calls SingleInstanceGuard.TryAcquire directly)
KhaozEngine.Game -> KhaozEngine.Diagnostics   (was already transitive via Gui -> App -> Diagnostics; made
                                               direct because GameApp logs the conflict path via Log.Info
                                               directly)
```

`ISingleInstanceLock` splits into two independent OS operations: ownership (claim-and-hold a key) and a
foreground-request signal (a losing second launch telling the current owner "come forward"). Ownership is a
named `Mutex` - confirmed the one named synchronization primitive .NET actually implements off Windows. The
foreground signal is deliberately **not** a named `EventWaitHandle` or `Semaphore`: both throw
`PlatformNotSupportedException` on macOS/Linux (confirmed against the .NET 10 runtime directly, not assumed
from docs - an early implementation using a named `EventWaitHandle` passed on Windows-shaped assumptions but
silently never signaled cross-instance off Windows, since the constructor call was wrapped in a swallow-all
try/catch). `SystemSingleInstanceLock` instead touches a small polled marker file under the OS temp
directory, which works identically on every platform this engine targets. The seam is what makes this
headless-testable: `KhaozEngine.Tests.App.SingleInstanceGuardTests` covers the guard's orchestration against
a fake `ISingleInstanceLock`, plus a couple of real-primitive integration tests (mutex contention across
threads, the marker-file signal) against `SystemSingleInstanceLock` itself.

This is deliberately a sibling of `AppRelaunch`'s `IProcessControl` seam, not a member of it - same
"headless-testable OS operation behind an interface" pattern, but ownership-and-signaling is a different
contract from process spawn-and-wait, so it gets its own seam rather than growing `IProcessControl` a second
unrelated responsibility.

## Design-viewport seam: device-pixel snapping

`IDesignViewport` (`KhaozEngine.Primitives`) is the design-viewport seam. Its implementers all live in
`KhaozEngine.Windowing`: `DesignViewport`, `AdaptiveViewport`, and `UiViewport` (the point-space viewport for
DPI-aware UI). The seam gained a `SnapsToDevicePixels` member, a default-interface-member defaulting
to `false`: `DesignViewport` / `AdaptiveViewport` inherit `false`, and `UiViewport` returns `true`. `SpriteBatch`
reads the flag through the seam to confine device-pixel snapping to the point-space path, so no new dependency
edge is introduced (`Render2D` already depends on `Primitives`).

The seam also carries `WindowBounds` (10.38.0), a default-interface-member giving the whole window mapped into
design space (`DesignBounds` plus the letterbox bars). It is derived from the existing scale + offset, so it needs
no new seam data and every implementer - including consumer test stubs - gets it for free; the three engine
viewports override it concretely (`DesignViewport` with the letterbox formula, `AdaptiveViewport` / `UiViewport`
returning `DesignBounds` since they never letterbox). `KhaozEngine.Gui` reads it to fill full-window scrims /
backgrounds through the same seam, no new edge.

## Server-status seams

`KhaozEngine.ServerStatus` carries two independent seams, both dependency-free (BCL + `KhaozEngine.Diagnostics`
+ `KhaozEngine.Primitives` only), so a game client and a headless server can both reference the package
without dragging in a web stack or a database driver.

- **`IServerStatusSource`** - the client-side fetch seam. The default `HttpServerStatusSource` contains a BCL
  `HttpClient` (HTTPS enforced, response size-capped, every transport/parse error swallowed to null), the same
  containment pattern as `HttpUpdateSource`. Tests inject a fake, so the poller and the state evaluator run
  headless with no sockets. There is no second HTTP backend planned, the seam exists for testability.
- **`IServerHeartbeatSink`** - the server-side liveness-write seam. Unlike the `WorldStore` / `Commerce`
  persistence seams, the durable SQL implementation is **not** an engine opt-in package: it lives in the game
  (or the game-template infra recipe). The status DB's schema and the one-row upsert are per-game cloud infra,
  and the game server already owns SQL access, so the engine ships only the contract plus `Null` / `InMemory`
  reference sinks and the `ServerHeartbeatService` cadence driver. Keeping the SQL out of the engine is what
  lets a game *client* reference `ServerStatus` for the poller without ever pulling a database driver.

```
KhaozEngine.ServerStatus -> KhaozEngine.Diagnostics   (logging only, no GPU, no DB, no web stack)
KhaozEngine.ServerStatus -> KhaozEngine.Primitives    (VersionComparer: shared numeric x.y.z compare)
```

The design authority split (CI/CD writes deploy facts, the game server heartbeats liveness, the endpoint
derives health, the client polls + evaluates) is documented in the package README and `USING-KHAOZENGINE.md`.
The public status endpoint itself (an Azure Function) is game infra and is NOT an engine artifact.

## Client job-scheduler seam: Game references Simulation

Turn-key client-side multi-core ECS scaling adds one edge, acyclic:

```
KhaozEngine.Game -> KhaozEngine.Simulation   (IJobScheduler / ThreadPoolJobScheduler / SingleThreadedJobScheduler)
```

`GameApp.JobScheduler` hands a game a shared worker-pool scheduler it wires into a world once
(`world.DefaultScheduler = App.JobScheduler`), so the type it returns (`IJobScheduler`, built as a
`ThreadPoolJobScheduler`) has to be visible from `KhaozEngine.Game`. `KhaozEngine.Simulation` sits at the bottom
of the server/netcode stack and owns the scheduler abstraction (the same one `ShardHost.Scheduler` uses on the
server),
so the new `Game -> Simulation` edge introduces no cycle: `Simulation` never references `Game`, `Windowing`, or
any renderer. The edge was already reachable transitively (`Game` pulls `Ecs` via the umbrellas, and
`Ecs -> Simulation`), but is made direct so `KhaozEngine.Game` alone, with no `Ecs`/`Foundation` reference, still
exposes the property. `World.DefaultScheduler` itself is the per-world seam in `KhaozEngine.Ecs`: it defaults to a
`SingleThreadedJobScheduler`, so a world stays byte-identical until a game opts in, and an explicit per-call
scheduler still wins over it.

## Determinism at the scheduling boundary: Simulation references Determinism

The floating-origin major added one edge, acyclic, and it cost `KhaozEngine.Simulation` its zero-dependency-leaf
status:

```
KhaozEngine.Simulation -> KhaozEngine.Determinism   (DeterministicFpScope, around each ThreadPoolJobScheduler body)
```

`DeterministicFp` pins the floating-point control register (rounding mode, FTZ/DAZ, trap masks) on the CALLING
THREAD only, by its own contract. `ThreadPoolJobScheduler.For` hands its body to arbitrary BCL thread-pool workers,
which are neither the calling thread nor a dedicated sim thread, so their register is whatever the pool last left
it at - which is the exact class of bug the scope exists to remove, reintroduced one layer up at the
job-scheduling boundary (issue #197). Applying the scope there rather than at each call site means a consumer's own
`For()` is covered too. `ShardHost.Tick` installs the same scope around each cell's step, so the guarantee holds
whichever scheduler is in use. Entering twice is harmless, since the inner scope restores exactly what the outer
one had already made canonical.

`Determinism` references only `Diagnostics` (for one startup warning), so the transitive closure stays headless
and GPU-free and every umbrella-purity guard is unaffected. The architecture test is narrowed rather than deleted:
`Simulation` may reference `Determinism` and nothing else.

## Cell islands: Sharding references Physics

A frame is a property of a SPACE, and a physics world IS a space, so a shard cell owns both or neither:

```
KhaozEngine.Sharding -> KhaozEngine.Physics   (CellSim.Physics, the cell's own IPhysicsWorld)
```

`KhaozEngine.Physics` is itself a zero-project-reference seam package (it declares only `System.Numerics`), so the
edge adds nothing to the closure but the seam types. `CellSim` holds the world, disposes it with the cell, and
never touches its contents: the consumer builds and populates it through
`ShardedWorldServerConfig.PhysicsWorldFactory`, and the engine never adds a static to a cell world.

The conversion of an entity ARRIVING in a cell is the other half, and it does NOT add an edge. `Sharding` knows
nothing about `ReplicatedPosition` (that type is one layer up, in `NetWorld`, which references `Sharding`), so the
cell calls out through `ICellFrameAdapter` at each door an entity can arrive by and the layer that owns the
component supplies the conversion.

## Version comparison: one shared leaf, two thin wrappers

`KhaozEngine.Primitives.VersionComparer` is the single numeric, dot-separated `x.y.z` version-compare rule
in the engine. `KhaozEngine.Updates.UpdateVersion.IsNewer` and `KhaozEngine.ServerStatus.VersionOrder`
(`Compare`/`IsBelow`) both delegate to it instead of carrying their own copy:

```
KhaozEngine.Updates -> KhaozEngine.Primitives        (VersionComparer: IsNewer delegates)
KhaozEngine.ServerStatus -> KhaozEngine.Primitives   (VersionComparer: Compare/IsBelow delegate)
```

Before this, `VersionOrder` deliberately mirrored `UpdateVersion`'s segment-compare code rather than
referencing `KhaozEngine.Updates` directly, to avoid pulling the whole delta-update pipeline (+ `Platform`)
into a package a status-poller-only client would reference. `Primitives` being the zero-dependency leaf both
packages already sit above removes that trade-off: a shared home with no new heavy edge. Both public types
keep their existing signatures, so no caller-visible change. The one behavioural difference between the two
originals was null handling: `VersionOrder` already treated a null/blank version as the empty, all-zero
version, while `UpdateVersion.IsNewer`'s non-nullable parameters meant a null argument fell through to an
incidental `NullReferenceException` from an unguarded `Split` call. Since `VersionComparer` is null-tolerant
(needed for `VersionOrder`), `UpdateVersion.IsNewer` now guards explicitly with
`ArgumentNullException.ThrowIfNull` before delegating, so a null argument still fails loudly with a
documented, more precise exception type instead of silently comparing as `0.0.0`.

## Pathfinding seam: IPathPlanner

`KhaozEngine.Navigation` adds five edges, all acyclic:

```
KhaozEngine.Navigation -> KhaozEngine.Primitives   (System.Numerics only)
KhaozEngine.Navigation -> KhaozEngine.Collision     (WorldColliders footprints, NavGridBaker.BakeOverworld)
KhaozEngine.Navigation -> KhaozEngine.Terrain       (TerrainCollision slope, NavGridBaker.BakeOverworld)
KhaozEngine.Dungeon -> KhaozEngine.Navigation       (DungeonNav.Bake turns a DungeonLayout into a NavSpace)
KhaozEngine.Foundation -> KhaozEngine.Navigation    (umbrella ProjectReference, like every other Foundation package)
```

`IPathPlanner` (`FindPath(start, goal, agentRadius, budget) -> NavPath`) is the seam callers code against.
`GridPathPlanner` is the one shipped implementation, grid A* over a `NavSpace`. Unlike the seams in the table
above, there is no third-party library on the other side of this one: the point of the interface is not
containment but swappability of the *algorithm* itself, so a future planner (a navmesh, a flow field, a
hierarchical search) can replace or sit alongside `GridPathPlanner` without touching `PathFollower` or any
other call site. `PathFollower` and `PathPlannerExtensions.FindPath` (the default-budget convenience
overload) depend on the interface only, never on `GridPathPlanner` directly.

`Dungeon -> Navigation` is a forward edge onto a package that itself sits earlier in the dependency graph:
`Navigation` depends only on `Primitives`/`Collision`/`Terrain`, all of which `Dungeon` already reached
transitively through `MapDoc` (`MapDoc -> Terrain`, and `Terrain` itself depends on both `Primitives` and
`Collision`), so the new edge introduces no cycle. `DungeonNav.Bake` lives in `KhaozEngine.Dungeon` and
returns a `KhaozEngine.Navigation.NavSpace`, but nothing in `Navigation` references `Dungeon` back.

## Tile world package edges

`KhaozEngine.TileWorld` adds four edges, all acyclic and all onto packages that already sit below it:

```
KhaozEngine.TileWorld -> KhaozEngine.Primitives      (the foundation leaf, declared like every sibling package)
KhaozEngine.TileWorld -> KhaozEngine.Serialization   (Jsonc for the JSONC-tolerant manifest and catalog reads)
KhaozEngine.TileWorld -> KhaozEngine.Content         (JsonSchemaValidator against the embedded catalog schema)
KhaozEngine.Foundation -> KhaozEngine.TileWorld      (umbrella ProjectReference, like every other Foundation package)
```

It is a SIBLING of `KhaozEngine.MapDoc` rather than an extension of it, and there is no edge between the two in
either direction. `MapDoc` describes a continuous-terrain zone, `TileWorld` a discrete tile grid, and all they
share is the three foundation packages each already depends on (`Primitives`, `Serialization`, `Content`).
`TileWorld` deliberately does NOT reference
`Terrain` (which `MapDoc` does), because a tile lattice carries its own corner heights and has no analytic
field to sample, and it does not reference `Navigation` either: per-edge walls cannot be expressed in a
`NavGrid`'s per-cell blocking model, so `TilePathfinder` is its own BFS over `TileCollisionMap`. An
`IPathPlanner` adapter over it (tile centres to `Vector3`) is a follow-up for when NPC AI wants `Navigation`'s
utilities, and would live on the `TileWorld` side, so the non-edge holds either way.

`KhaozEngine.TileWorld.Render3D` is the render arm, split off exactly as `Terrain.Render3D` is from `Terrain`, so
the document package stays GPU-free for a server or a tool:

```
KhaozEngine.TileWorld.Render3D -> KhaozEngine.TileWorld          (the document it meshes, plus TileWorldSpace and TileTriangulation)
KhaozEngine.TileWorld.Render3D -> KhaozEngine.Render3D           (Scene3D, GltfMesh/GltfMeshPart, ModelVertex, MeshHandle, the cameras, Render3DSnapshot)
KhaozEngine.TileWorld.Render3D -> KhaozEngine.Render2D           (ImageRgba, to decode a catalog material's texture into a ground material layer)
KhaozEngine.TileWorld.Render3D -> KhaozEngine.Terrain.Render3D   (PropPlacement and the Scene3D.DrawProps/LoadPropMeshes prop path)
KhaozEngine.Game3D -> KhaozEngine.TileWorld.Render3D             (umbrella ProjectReference, like every other Game3D package)
```

All five are forward edges onto packages that already sit below it, and nothing references back. The
`Terrain.Render3D` edge is the one worth naming: the tile world reuses the prop renderer outright (LOD, instancing,
distance dissolve) rather than growing a second placement path, which is why an edge to the terrain render arm
exists at all when there is none between `TileWorld` and `Terrain` themselves. `TileWorldView` reaches the scene
through its own `ITileWorldScene` seam rather than a `Scene3D` field, so every view and residency rule is testable
without a device, and `Scene3DTileWorldScene` is the one place the two meet.

**The seam grew four members for textured ground and water (17.38.0), all DEFAULT interface implementations.**
`LoadTileGroundMaterial(TileGroundMaterialSet)` (defaults to an invalid handle),
`UnloadTileGroundMaterial(TileGroundMaterialHandle)` (a no-op), `LoadMesh(GltfMesh,
TileGroundMaterialHandle)` (falls through to the material-free `LoadMesh`, so the same geometry renders through
the model path) and `DrawWater(in WaterPlane)` (a no-op, so no water is drawn). Defaults rather than abstract
members because two implementations sit outside this repo's control (a consumer's own, and Grimhollow's test
fake), and the alternative is a compile break in a downstream game for a feature it has not adopted. The seam
still adds no abstraction of its own: each of the four is `Scene3D` API forwarded straight through by
`Scene3DTileWorldScene`. The mesher takes a second, smaller seam for the same headless reason,
`ITileGroundSlotMap` (`SlotOf(materialId)`, `MissingSlot`), which is what lets a slot map be swapped for a stub in
a test. `TileGroundMaterialSet` implements it, and `IdentitySlotMap` is the shipped stand-in for a caller that has
not built a set.

`KhaozEngine.TileWorld.Editing` is the command layer, and it is deliberately NOT part of either of the two
packages above:

```
KhaozEngine.TileWorld.Editing -> KhaozEngine.TileWorld   (the document it mutates, plus TileRect, TileFootprint and the collision baker)
KhaozEngine.Foundation -> KhaozEngine.TileWorld.Editing  (umbrella ProjectReference, like every other Foundation package)
```

One forward edge and one umbrella edge, and `TileWorld` does not reference it back. It is GPU-free and
render-free on purpose: two frontends author a tile world (the `ke-tileedit` MCP tool now, a GUI tile editor
later), both need the same undo stack, and a command layer living in either one would let the two drift apart
on what a single edit is. The `MapDoc` side is the counter-example rather than the precedent: its command stack
sits inside `KhaozEngine.MapEditor`, which carries Gui, Render3D and Terrain.Render3D, so `ke-mapedit` drags a
renderer in for two verbs. That is why `TileEdit.Tool` references `TileWorld.Editing` where the design
originally had it referencing a `TileEditor` GUI package.

`KhaozEngine.TileWorld` grants `InternalsVisibleTo` to `KhaozEngine.TileWorld.Editing` (alongside its test
assembly). It exists for exactly two members, both of which are the undo half of a public operation and neither
of which is safe as public API: `TileWorldDocument.AddObjectWithId`, which re-adds an object at a GIVEN id so
an undone place or delete comes back with the id it left with (a public one would let content invent ids and
collide with the allocator), and `TileWorldDocument.RestoreRegion`, which re-attaches the exact region instance
a `DeleteRegionCommand` detached (a public one would let a caller attach a region built anywhere, with any
plane count, into any document).

The MCP tool sits above all three and is a dev tool rather than an engine package, exactly like
`KhaozEngine.MapEdit.Tool`:

```
KhaozEngine.TileEdit.Tool -> KhaozEngine.TileWorld.Editing    (every mutation is a command through TileEditingDocument)
KhaozEngine.TileEdit.Tool -> KhaozEngine.TileWorld.Render3D   (TileWorldSnapshot and GreyboxMeshResolver, for the two render verbs)
KhaozEngine.TileEdit.Tool -> KhaozEngine.Imaging              (PngWriter.Encode, to turn a captured buffer into PNG bytes)
KhaozEngine.TileEdit.Tool -> ModelContextProtocol             (the MCP server SDK, contained here as it is in ke-mapedit)
KhaozEngine.TileEdit.Tool -> Microsoft.Extensions.Hosting     (the stdio host, contained the same way)
```

It is in no umbrella and nothing in the engine references it. The `TileWorld.Render3D` edge is the whole reason
the render verbs need a GPU while the other 41 do not, and it is the one edge that would disappear if the
render verbs ever moved to a separate tool.

## Surface-source seam: INavSurfaceProvider (a deliberate non-edge)

The step-aware overworld bake (`NavGridBaker.BakeOverworldSteps`) needs a per-cell walkable surface
height. The obvious source for a game with real physics is a downward probe against its `IPhysicsWorld`
(`PhysicsGroundProbe` or similar), which would suggest a `KhaozEngine.Navigation -> KhaozEngine.Physics`
edge. That edge was deliberately NOT added:

```
KhaozEngine.Navigation -> KhaozEngine.Primitives   (unchanged)
KhaozEngine.Navigation -> KhaozEngine.Collision     (unchanged)
KhaozEngine.Navigation -> KhaozEngine.Terrain       (unchanged)
KhaozEngine.Navigation -x KhaozEngine.Physics       (no edge - INavSurfaceProvider is the seam instead)
```

`INavSurfaceProvider` (`TrySample(x, z, out height, out headroom) -> bool`) is the surface-source seam:
`KhaozEngine.Navigation` codes against the interface only. `TerrainSurfaceProvider`, the shipped default,
implements it over `KhaozEngine.Terrain`/`KhaozEngine.Collision`, both dependencies the package already
has. A game that wants a physics-probe surface instead implements `INavSurfaceProvider` itself (or wraps a
delegate in `DelegateSurfaceProvider`) in its own code, over its own `IPhysicsWorld`, and hands the
provider to `BakeOverworldSteps`. The hop bake (`NavGridBaker.BakeOverworldHops`) reads its heights
through the same provider seam as `BakeOverworldSteps`, so hop-link generation adds no dependency
either. `KhaozEngine.Navigation`'s dependencies stay exactly `Primitives`,
`Collision`, `Terrain`, matching the package-global rule that a nav bake never re-touches a physics world
at query time (it already does not re-touch `TerrainCollision`/`WorldColliders` either, per
`NavGridBaker.BakeOverworld`). This is the same shape as the pathfinding seam above: an interface exists
not to contain a third-party library, but to keep an optional data source out of a package's dependency
graph while still letting a consumer plug one in.

**Phase 2 widens the same seam to many surfaces per column, the same non-edge.** The layered overworld
bake (`NavLayerBaker.BakeOverworldLayered`, vertical worlds phase 2, issue #30) needs more than one
walkable surface per XZ column, since a bridge deck over a path or a roofed interior under its roof puts
two standable surfaces at the same point, and `INavSurfaceProvider.TrySample` can only ever report one.
`INavColumnProvider` (`SampleColumn(x, z, Span<NavSurfaceSample>) -> int`) is that widening: every
standable surface in the column, bottom-up, each with its own headroom. It inverts exactly like
`INavSurfaceProvider` did, so the dependency graph does not change:

```
KhaozEngine.Navigation -> KhaozEngine.Primitives   (unchanged)
KhaozEngine.Navigation -> KhaozEngine.Collision     (unchanged)
KhaozEngine.Navigation -> KhaozEngine.Terrain       (unchanged)
KhaozEngine.Navigation -x KhaozEngine.Physics       (still no edge - INavColumnProvider is the seam)
```

`SurfaceColumnAdapter` wraps an `INavSurfaceProvider` so a phase-1, single-surface world can run through
the layered bake unchanged (degenerating to one layer), and `DelegateColumnProvider` mirrors
`DelegateSurfaceProvider` for a provider supplied as a plain delegate. The physics-side counterpart is
`PhysicsColumnProbe` in `KhaozEngine.Physics`: a repeated downward raycast sweep that reports every
standable surface in a column with its headroom to the hit above it, the same STATICS-ONLY stance as
`PhysicsGroundProbe`. Physics cannot reference Navigation any more than Navigation can reference Physics
(the layering forbids the reverse edge too), so the game glues `PhysicsColumnProbe.Sample` to
`INavColumnProvider.SampleColumn` with a one-line delegate. No new package, no new dependency edge either
direction.

## GPU backend-selection provenance: one new edge, `Gpu -> Diagnostics`

Since 17.21.0 `KhaozEngine.Gpu` references `KhaozEngine.Diagnostics`, so `GpuDeviceContext` can log which
backend it selected and where that decision came from:

```
KhaozEngine.Gpu -> KhaozEngine.Diagnostics   (logging only: one INFO line per created device naming the
                                              backend and its origin, plus a WARN when KE_GRAPHICS_BACKEND
                                              was set to something unparseable)
```

The edge is legitimate rather than a layering break. `Diagnostics` is a dependency-free leaf (its own catalog
row is "Pure .NET") and is already referenced by GPU-stack peers `Audio`, `Gui`, and `Terrain`, so it adds
nothing to any closure and changes no umbrella membership. It cannot reintroduce GPU into the GPU-free
`Foundation` / `Server` closures either, because the edge runs FROM `Gpu`, not INTO it. `ArchitectureTests`
needed no rule change: it constrains third-party `PackageReference` homes and umbrella membership, and this is
an engine-internal `ProjectReference` that alters neither.

Why it is worth an edge at all: without it, a mistyped `KE_GRAPHICS_BACKEND` is indistinguishable from the OS
default. The selector falls back to the probe, the run produces a perfectly ordinary trace, and the result reads
as "the requested backend did not help" when that backend never ran. `GpuBackendSelection` records the
provenance and the raw override value, and this edge is what lets the engine say so out loud.

The edge has since carried more per-device traffic without changing shape: the 17.22.0 Direct3D11 driver-threading
line and its WARN, and 17.24.0's adapter line, its Windows overlay-injector line and WARN, and the INFO line for
the `KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS` flag. All of it is logging out of `Gpu` into `Diagnostics`, so the
reasoning above is unchanged and no new reference was needed. The 17.24.0 module scan uses `System.Diagnostics.Process`
from the BCL, not a new package.

### 17.25.0 puts a `Diagnostics` TYPE in a `Gpu` signature for the first time

`GpuTelemetry.WithGpu` fills a `KhaozEngine.Diagnostics.TelemetrySessionInfo` from a device, so the edge now
carries an API surface and not only log calls. Still no new reference and still the same direction, but it is
worth naming because it decided where the bridge lives. `Diagnostics` cannot map the GPU enums itself: it sits
UNDER `Gpu` on this edge, and naming `GpuBackendKind` there would be a cycle. So the header's GPU fields are
plain strings and nullable bools, and the mapping onto them is an extension method in the package that owns the
enums. A consumer holding only an `AppWindow` calls the value overload, which is why `AppWindow` needs no new
member and `Windowing` needs no new edge.

### 17.32.0 adds a second `Diagnostics`-typed `Gpu` surface, and one more seam member

`GpuTelemetryChannels` projects a `GpuDeviceCounters` onto `KhaozEngine.Diagnostics.TelemetryChannel` rows, which
is the same edge in the same direction as `GpuTelemetry.WithGpu` above and needs no new reference either. It is a
SEPARATE type rather than more methods on `GpuTelemetry`, because the two do different jobs: that one fills the
header's creation-time identity once, this one appends numbers that move to every sample row.

The seam member is `IGpuDevice.Counters`, default-implemented like `IGpuDevice.Diagnostics` beside it, so it was
appended without breaking any implementer and every backend that counts nothing answers honestly
(`GpuDeviceCounters.HasValue` false, which is a different fact from counting and finding zero). `GpuDeviceContext`
and `AppWindow` forward it, both reading THROUGH to the device on every access, because unlike everything else the
header carries these numbers change on every frame. The two NATIVE backends fill it: `KhaozEngine.Gpu.D3D11` from
17.32.0, and `KhaozEngine.Gpu.Vulkan` from 17.34.0, which is also where the struct gained its `AcquireWaitCount`
and `AcquireWaitMs` pair and `GpuTelemetryChannels` gained the matching `gpuAcquireWaits` and `gpuAcquireWaitMs`
columns.

**Two members mean subtly different things on the two native backends, and the seam scopes both rather than
pretending otherwise.** `BackpressureStallCount` folds a second wait onto one accumulator on Vulkan, the command
list meeting its own oldest buffer slot, because one lever sizes both that and the ring segments there.
`DrainCount` is the one that can mislead a reader COMPARING the two: a backend that can determine caught-up
without flushing does not count that call, and the native Vulkan drain can, while the native Direct3D 11 drain
must signal a fresh point and flush because its immediate context holds work no fence value describes
([#545](https://github.com/APKiwiOrg/KhaozEngine/issues/545)). Both are locally correct, so the same consumer
call pattern reads higher on Direct3D 11 for a reason that is not a stall, and `DrainMs` is the figure that
compares across backends. A seam member whose meaning is backend-shaped has to say so where it is defined, or
the first cross-backend reading is the place it gets discovered.

### The stored backend preference deliberately adds NO edge (17.23.0)

Letting a player pick the backend in game is naturally read as "`Gpu` needs to load a setting", which would mean
a settings or persistence reference and would invert the layering: `KhaozEngine.Gpu` sits at the bottom with
only `Diagnostics` + `Primitives`, and the persistence stack sits above it. So the preference is DATA, a
`GpuBackendKind?` passed down (`GameAppOptions` to `AppWindow` to `GpuDeviceContext`), and the engine does no
file IO for it. The same rule is why the creation fallback only REPORTS
(`GpuBackendSource.FallbackAfterFailure` plus `RequestedBackend`) and never clears the offending setting: the
game owns that write. Direction of flow is what keeps the closure intact, not the size of the dependency.

## D3D11 driver-threading probe: a declared package, not a new seam

Since 17.22.0 `KhaozEngine.Gpu` declares a `PackageReference` to `Vortice.Direct3D11`, and
`ArchitectureTests` maps it to the same `Gpu` home as `Veldrid`:

```
KhaozEngine.Gpu -> Vortice.Direct3D11   (diagnostics only: ID3D11Device::CheckFeatureSupport with
                                         D3D11_FEATURE_THREADING, read once at device creation on
                                         Windows on the Direct3D11 backend, and nowhere else)
```

This adds no dependency the engine did not already have. `Vortice.Direct3D11` IS Veldrid's own D3D11 binding and
has been a transitive dependency of `Veldrid` 4.9.0 the whole time. What changed is that it is now DECLARED,
because `ArchitectureTests.EveryThirdPartyPackage_IsDeliberatelyMapped` requires every third-party reference a
packable library uses to be a deliberate edit rather than something inherited by accident. `Gpu` is the correct
home for THIS use: it is the same seam Veldrid is confined to, reached through the raw `ID3D11Device` pointer
Veldrid itself publishes on `BackendInfoD3D11.Device`, and nothing outside `Internal/D3D11ThreadingProbe` names a
Vortice type inside this package.

`Vortice.Direct3D11` now has a SECOND home, `KhaozEngine.Gpu.D3D11`, and that is a different use rather than a
widening of this one: that package IS the Direct3D 11 interop, so the binding is its subject matter and not a
diagnostic borrowed from a neighbour. Both homes pin the same Vortice 2.3.0 line, which is what Veldrid depends
on, so there is exactly one D3D11 binding and one `SharpGen.Runtime` in the graph. The native backend adds
`Vortice.D3DCompiler` on the same line for its own FXC call, and that one is its alone.

It also does not widen what loads at runtime anywhere else. The probe's guard returns before any Vortice type is
named, so on macOS and Linux, and on any non-Direct3D11 backend, that assembly is never loaded at all. The
containment is therefore stronger than "confined to one package": it is confined to one method, on one OS, on one
backend. Why it is worth having at all is in [USING-KHAOZENGINE.md](USING-KHAOZENGINE.md): a driver that cannot
build command lists makes Windows emulate them in software, and until 17.22.0 nothing could tell that machine
apart from one that was simply slow.

## Out-of-package graphics backends: an INVERTED edge, `GpuBackendProviders`

Every backend before this one lived inside `KhaozEngine.Gpu`, so the seam and its implementation shared an
assembly and the selector could just construct one. A backend that ships in its own opt-in package (the native
Direct3D11 backend is the first) cannot work that way: `Gpu` referencing it would be a cycle, and folding it back
into `Gpu` would make its interop non-optional for every consumer including the Linux server heads.

So the edge is inverted. `KhaozEngine.Gpu` declares `IGpuBackendProvider` and a `GpuBackendProviders` registry
keyed by `GpuBackendKind`, the backend package implements the interface, and the CONSUMING APP joins the two with
one explicit call at startup:

```
KhaozEngine.Gpu.D3D11 -> KhaozEngine.Gpu      (the only direction. Gpu never references a backend package)
consumer              -> both                 (and calls KhaozEngineD3D11.Register() once)
```

Two properties are load-bearing, and both are decisions rather than conveniences. Registration is EXPLICIT, not a
`[ModuleInitializer]` and not reflection by assembly name: the CLR loads an assembly lazily on first type
reference, so a package reference with no static type use does not guarantee an initializer runs, and a silent,
machine-dependent failure is the worst shape for a switch whose purpose is attributing measurements to a backend.
And a MISSING registration throws rather than falling back, which keeps it distinguishable from the genuinely
different fact that a machine cannot run the backend (that one is answered by the provider's own functional probe
and reported through the existing `FallbackAfterFailure` path). Full reasoning in
[design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md](design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md), section 4.1
and decisions P4 and I2.

### The second instance: `KhaozEngine.Gpu.Vulkan`

The pattern above is used three times now, which is what turns it from a one-off into the shape an out-of-package
graphics backend has here. `KhaozEngine.Gpu.Vulkan` is phase 3 of the same program and takes the same inverted
edge. As of `17.34.0` it registers a real provider through `KhaozEngineVulkan.Register()`, answers a real
functional machine probe, and creates a real HEADLESS device
([#514](https://github.com/APKiwiOrg/KhaozEngine/issues/514)) that hands out command lists and submits them
([#517](https://github.com/APKiwiOrg/KhaozEngine/issues/517)), with a real resource factory behind it
([#519](https://github.com/APKiwiOrg/KhaozEngine/issues/519)) and real descriptors
([#520](https://github.com/APKiwiOrg/KhaozEngine/issues/520)). The recording members those lists can serve are
`UpdateBuffer`, which routes to the uniform ring or the per-list staging arena
([#518](https://github.com/APKiwiOrg/KhaozEngine/issues/518)), all four resource-set binds, which record into
a per-slot array and flush as contiguous-run `vkCmdBindDescriptorSets`
([#521](https://github.com/APKiwiOrg/KhaozEngine/issues/521)), and the whole rendering half: the framebuffer
bind, both clears and both scissor members, over a `vkCmdBeginRendering` deferred to the first draw with no
`VkRenderPass` and no `VkFramebuffer` behind it at all
([#522](https://github.com/APKiwiOrg/KhaozEngine/issues/522)), whose attachment layout transitions ride that
deferred begin from [#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524). The factory also builds shader
sets and compute
shaders ([#526](https://github.com/APKiwiOrg/KhaozEngine/issues/526)), which is the row that split the shader
seam in two (see the shader-seam section below), and both PIPELINES
([#523](https://github.com/APKiwiOrg/KhaozEngine/issues/523)), which closes its refusal list entirely: every
member of `IGpuResourceFactory` is built on this backend now. That row added two seams of its own rather than
one, and the split is load-bearing: `IVulkanPipelineApi` CREATES pipelines and is unreachable from the recording
type (a pipeline creation is a shader compile, and one inside a frame is the classic hitch), while
`IVulkanPipelineBinder` is the single `vkCmdBindPipeline` a command list holds and can make nothing. The BARRIER
AND LAYOUT TRACKER ([#524](https://github.com/APKiwiOrg/KhaozEngine/issues/524)) added one more,
`IVulkanBarrierRecorder`, and it is a seam for a reason worth stating because it looks redundant next to the
budget seam that already carries `vkCmdPipelineBarrier2`: that one is consumed through a `where TSink : struct`
constraint so the JIT monomorphizes it onto the per-draw path, so it cannot be held as a FIELD without boxing,
and the tracker needs a held emitter. Both implementations are one call into a batching function that takes the
command sink as a generic parameter, so every barrier still passes through the budget seam, and the seam is
where the sink is SUBSTITUTED: the real recorder drives a `VulkanCmdSink` over the command buffer it is handed
and the device-free one drives the counting sink, which is what makes the per-draw barrier budget an assertion
that can fail rather than one that cannot see an image barrier at all. Since
[#527](https://github.com/APKiwiOrg/KhaozEngine/issues/527) the WINDOWED entry point is real too: a platform
surface chosen from `GpuWindowKind`, `VK_KHR_swapchain` on the device, and `SwapchainFramebuffer`,
`ResizeSwapchain` and `Present` all live behind a present boundary that acquires, resizes, recreates and
presents. **That row also changes what the seam's "a resize reconfigures nothing" wording means here**: on
Direct3D 11 and Metal vsync is an argument of the present call, and Vulkan cannot change a swapchain's present
mode in place at all, so a runtime `SyncToVerticalBlank` change on this backend queues a full recreate applied at
the next present boundary, exactly as a resize does. Since [#525](https://github.com/APKiwiOrg/KhaozEngine/issues/525) it DRAWS: every member of
`IGpuCommandList` is built, so a windowed run presents a frame the backend really rendered. Two more device-free
seams landed with it, on the same principle as every seam above. The DRAW emitter carries the vertex and index
binds, the draws, the dispatch and the dependent-dispatch barrier, and its two implementations reach the driver
through one batching function that takes the command sink as a GENERIC parameter, so the descriptor flush and the
command stay one monomorphized pair while the counting twin can see a `vkCmdDraw` at all: that is what completed
MV4, whose draw-call marginals read zero by construction until something emitted one. The TRANSFER sink carries
the six copy, blit and resolve calls and is deliberately NOT on the budget seam, because none of them scales with
draw count. **A vertex bind is the one class where widening the budget seam would have looked defensible**, since
it genuinely does scale with draw count, and it got its own line instead so the frozen marginals still mean what
they meant. **And the seam's compute rule 1 and rule 2 comment named a mechanism where it meant a rule**, which
is the failure mode a second implementation of one API creates: rule 1 is satisfied here by a real image barrier
at the sampled bind rather than by the queued layout restore that comment described, and a dependent-dispatch
chain inside one list IS ordered here. Both sentences were true of Veldrid's Vulkan backend and false of this
one. The comment now names the implementation each mechanism belongs to, and says outright that the mechanism is
the part that differs between two backends one environment variable apart, while rule 2's portable requirement is
unchanged and a consumer that drops the drain still breaks on the Veldrid legs the same machine falls back to
([#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529)). The native Metal backend later joined the
permissive side by a third mechanism, its compute encoder's serial dispatch type, which is what makes three of
three engine-owned backends honour rule 2 natively
([#585](https://github.com/APKiwiOrg/KhaozEngine/issues/585)) and leaves only the two Veldrid legs needing the
drain. That is the quorum [#461](https://github.com/APKiwiOrg/KhaozEngine/issues/461) has been waiting for, and
it is evidence rather than a contract change. It registers under `GpuBackendKind.VulkanNative`, which landed in
`17.32.0`, so the backend is selectable by name (`KE_GRAPHICS_BACKEND=vulkan-native`) and a machine that cannot
run it arrives through the reported fallback. Nothing selects it by default, and the `vulkan-native` CI leg
verifies the incumbent's committed goldens on lavapipe as a guest in that family, which is the continuous
exercise the rollout is measured against.

```
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Gpu      (the only direction, again)
KhaozEngine.Gpu.Vulkan -> KhaozEngine.Diagnostics   (the probe's one log line, same as the D3D11 instance)
KhaozEngine.Gpu.Vulkan -> Silk.NET.Vulkan(+.Extensions.KHR/.EXT)
```

Two things differ from the Direct3D 11 instance, and both are worth knowing before reading one package as a
template for the other.

**No platform guards, and their absence is the decision** (V-P1 of
[design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md](design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md)). The
D3D11 package targets `net10.0` and then spends a whole `[SupportedOSPlatformGuard]`-over-`NoInlining`
apparatus keeping the Vortice interop off the load path on macOS and Linux, because Direct3D is a Windows API
in an assembly that must load everywhere. Vulkan is not: the same managed code runs on Windows and Linux, the
loader resolves at runtime, and a machine without one fails the functional probe and routes through the
existing fallback. So the Vulkan package has no OS-suffixed TFM, no guard attributes and no `NoInlining`
bodies, and adding them back by analogy would create a boundary the backend does not have.

**The binding is contained the same way the Veldrid one is.** `Silk.NET.Vulkan` and its two extension packages
are mapped to `Gpu.Vulkan` alone in `ArchitectureTests.ThirdPartyHomes`, and no `Silk` type may appear on the
package's externally visible surface (`GpuPublicApiTests.GpuVulkanPublicApi_DoesNotLeakBackendTypes`). The
reason is the seam rather than the load path: a `Silk.NET.Vulkan` type in a public signature makes a consumer
that merely reads it compile against the Vulkan binding, which turns an opt-in backend into a second GPU
vocabulary the engine would owe stability to. Both packages also have their surface pinned member by member
(`GpuD3D11PublicSurface_IsExactlyTheApprovedMembers` and `GpuVulkanPublicSurface_IsExactlyTheApprovedMembers`),
which is what catches a new member that leaks no forbidden type and is therefore invisible to every scan above.

**Both packages grant `InternalsVisibleTo` to `KhaozEngine.TestSupport.Gpu`, and that is the whole cost of one
decision.** Section 2.2 of the Vulkan design declined to extract either backend's uniform ring into a shared
PRODUCTION home, on the rule of three and on the observation that the policy is identical where the mechanism is
not. What is shared instead is the ring's SEMANTIC tests, run against both rings through one internal test-only
interface plus one adapter per backend, all of which live in `KhaozEngine.TestSupport.Gpu`. That project is
`IsPackable=false` and `IsTestProject=false`, so it ships nothing and no shared production home exists, and it
already references both backend packages for the `[GpuFact]` registration seats. So the visibility this costs is
one csproj line each, beside the `KhaozEngine.Render.Tests` grant both already carried. The edge direction is
unchanged: `TestSupport.Gpu -> Gpu.D3D11`, `TestSupport.Gpu -> Gpu.Vulkan` and, since the Metal instance below,
`TestSupport.Gpu -> Gpu.Metal`, never the reverse.

The no-Veldrid rule below applies to ALL THREE packages, and both assertions are theories over the three of them.

### The third instance: `KhaozEngine.Gpu.Metal`

`KhaozEngine.Gpu.Metal` is phase 4 and the LAST instance, because after it every API the engine renders on has
an engine-owned implementation behind this seam. It takes the same inverted edge for the third time, registering
through `KhaozEngineMetal.Register()` under `GpuBackendKind.MetalNative`, which landed in `17.35.0` with the
`metal-native` and `mtl-native` tokens, so the backend is selectable by name
(`KE_GRAPHICS_BACKEND=metal-native`) and a machine that cannot run it arrives through the reported fallback.
Neither `IGpuDevice` nor `IGpuCommandList` has an unbuilt member left: it creates devices headless and windowed,
compiles GLSL through SPIR-V to MSL, builds pipelines from a per-program binding table read out of the emitted
MSL, records and submits against an `MTLSharedEvent` timeline, draws, dispatches, and presents through a
`CAMetalLayer`. Nothing selects it by default, and the `metal-native` CI leg verifies the incumbent's committed
`metal` goldens on the same real GPU as a guest in that family, which is the continuous exercise the rollout is
measured against.

```
KhaozEngine.Gpu.Metal -> KhaozEngine.Gpu           (the only direction, for the third time)
KhaozEngine.Gpu.Metal -> KhaozEngine.Diagnostics   (the probe's log lines, same as both siblings)
KhaozEngine.Gpu.Metal -> (no third-party package at all)
```

**That last line is the difference worth knowing, and it is a decision rather than an omission** (M-P3 of
[design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md](design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md)). Phase 2
took `Vortice.Direct3D11` and phase 3 took `Silk.NET.Vulkan` on the reasoning that owning the BACKEND and owning
the BINDING are different things. That reasoning is unchanged and it has nothing to point at here: Silk.NET
ships no Metal, Vortice ships no Metal, and Apple ships no managed binding of any kind. Vendoring
`Veldrid.MetalBindings` is rejected by name, so the interop is an engine-owned `[LibraryImport]` layer over
`objc_msgSend` with blittable-only signatures, one file per Objective-C class, and this is the only backend in
the program whose `ArchitectureTests.ThirdPartyHomes` row is empty. **Do not add one for symmetry**, and read
an empty row there as the assertion it is rather than as a missing entry.

**The platform boundary is the Direct3D 11 shape, not the Vulkan one**, and picking the wrong sibling as a
template is the mistake this paragraph exists to stop. Metal is an OS-specific API exactly as Direct3D is, so
the package targets plain `net10.0` (M-P1) and carries the whole
`[SupportedOSPlatformGuard("macos")]`-over-`NoInlining`-`[SupportedOSPlatform("macos")]` apparatus the D3D11
package carries, which with warnings as errors makes CA1416 the compiler-enforced boundary. The Vulkan
package's deliberate ABSENCE of that apparatus (V-P1) is right for Vulkan and wrong here. A macOS-suffixed TFM
would stop this assembly compiling on the Linux `ci.yml` leg and both Windows legs, and stop
`KhaozEngine.Render.Tests` referencing it unconditionally, which is where its device-free tests live.

Everything else is the pattern as before. The surface is pinned member by member
(`GpuMetalPublicSurface_IsExactlyTheApprovedMembers`) beside the leak scan
(`GpuMetalPublicApi_DoesNotLeakBackendTypes`), it grants `InternalsVisibleTo` to `KhaozEngine.Render.Tests` and
to `KhaozEngine.TestSupport.Gpu` for the third shared ring adapter, and it declares no `Veldrid` package.

### What the backend package may reference, and the one edge it may NOT

`KhaozEngine.Gpu.D3D11` references `KhaozEngine.Gpu`, `KhaozEngine.Diagnostics` (so the support probe can say
WHY a machine is unsupported instead of answering a bare false), `Vortice.Direct3D11` and `Vortice.D3DCompiler`.
It declares NO `Veldrid` package, and that is decision P2 rather than an accident of ordering.

The shader path needs glslang and, for Direct3D 11 and Metal, SPIRV-Cross. Both arrive as `Veldrid.SPIRV`.
Referencing it from a backend would be the obvious shortcut and is rejected twice over: blessing a Veldrid
package inside a backend whose entire premise is being Veldrid-free is a bad signal no guard would catch, and it
would scatter the eventual SPIRV-Cross replacement across every backend package instead of keeping it in one.
The edge stays in `KhaozEngine.Gpu` behind internal helpers plus `InternalsVisibleTo`, which is where it already
belongs: this package owns `ShaderValidation`, which uses precisely that static API with no device in existence.

**AND THAT SEAM IS TWO MEMBERS NOW RATHER THAN ONE, which is decision V-S3** (section 12.3 of
[design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md](design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md), landed
by [#526](https://github.com/APKiwiOrg/KhaozEngine/issues/526)). `Internal/SpirvFrontEnd` is the glslang half
(GLSL 450 to SPIR-V, under `Internal/SpirvFrontEndPin`) and `Internal/SpirvCrossCompile` is the SPIRV-Cross half.

**THE BACK-END HALF EMITS TWO LANGUAGES NOW, and it has three consumers rather than two** (decision M-S1,
section 12.1 of
[design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md](design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md), landed by
[#575](https://github.com/APKiwiOrg/KhaozEngine/issues/575)). `SpirvCrossCompile` emits SPIR-V to HLSL under
`Internal/HlslCrossCompilePin` and SPIR-V to MSL under `Internal/MslCrossCompilePin`, as two pairs of members in
one file rather than a second file, because the SPIRV-Cross replacement
([#462](https://github.com/APKiwiOrg/KhaozEngine/issues/462)) has to stay one seat. The two pins are separate
because they freeze different option sets and drift independently.

- `KhaozEngine.Gpu.D3D11` reaches both halves, because DXBC is a function of both.
- `KhaozEngine.Gpu.Metal` reaches both halves too, and that is the Direct3D 11 shape rather than the Vulkan one:
  its sources are GLSL and Metal consumes MSL. It reaches one member more than its Direct3D 11 sibling,
  `Internal/SpirvResourceDecorations`, which is the `(id, set, binding)` walk its binding table joins on
  (section 2.2b). That walk was written in the test project as a measurement and promoted into
  `KhaozEngine.Gpu/Internal` when the ruling took the id join, so the grant that already carried the toolchain
  carries it as well rather than a second grant existing for it. `KhaozEngine.Gpu`'s own
  `Internal/MslBindingOrder` reads it too, from inside the package rather than across the seam, which is what
  lets `ShaderValidation` join an emitted Metal argument back to its declared `(set, binding)`.
- `KhaozEngine.Gpu.Vulkan` reaches the FRONT END ONLY, because Vulkan consumes SPIR-V and
  `vkCreateShaderModule` takes the bytes verbatim, so nothing on that backend's shader path is cross-compiled.

Those one-sided edges are asserted rather than intended, and they need their own assertions because the two
scans below cannot see them: every half lives in the same assembly, so a Vulkan call into `SpirvCrossCompile`
would compile, would add no Veldrid reference, and would read identically to every package-level and
assembly-level check. `VulkanShaderFrontEndOnlyTests` reads that backend's `TypeRef` table off disk instead, and
asserts it names `SpirvFrontEnd` and no back-end type. `MetalShaderArchitectureTests` does the Metal half as a
MEMBER check rather than a type check, because that backend names `SpirvCrossCompile` legitimately and
`VertexFragmentToHlsl` is one letter away from `VertexFragmentToMsl` across the same grant. What both buy is
that the SPIRV-Cross migration stays a change to one half of one file.

```
KhaozEngine.Gpu.D3D11 -> Vortice.Direct3D11, Vortice.D3DCompiler   (its subject matter)
KhaozEngine.Gpu.D3D11 -> Veldrid*                                  (never, asserted two ways)
```

`Vortice.D3DCompiler` is CALLED from exactly ONE type, `Internal/D3D11Fxc`, which compiles emitted HLSL to DXBC
and reflects a vertex module's input signature. One further type names it without calling it:
`Internal/D3D11ShaderDebug` takes its two FXC flag values from `Vortice.D3DCompiler.ShaderFlags` in `const uint`
initializers, so the compiler folds them to literals and the built assembly carries the numbers rather than the
type. Both shapes are covered by the same load-path guard, `D3D11InteropLoad.AssertNotLoaded`, which asserts the
interop is absent from the process rather than reasoning about which reference survived compilation. Everything
else in the shader path (the target profiles, the cache key, the disk cache and the holed-signature rule) names
no Direct3D type at all, and all of it is tested headlessly on macOS and Linux. One FXC call site is also what lets
`KhaozEngineD3D11.ValidateShaderPair` be a genuine check rather than a second implementation: it compiles under
the same profile, the same flags and the same pinned cross-compile options as the shipped path, so it cannot
drift into validating a shader nobody ships.

Two ways, because one of them alone would not bind. `ArchitectureTests.NativeGpuBackend_DeclaresNoVeldridPackage`
reads the project file, which catches the deliberate edit. It cannot catch the subtler failure: Veldrid is in a
backend's transitive closure through `KhaozEngine.Gpu` whatever the project file says, so an INTERNAL helper
signature mentioning a Veldrid type would compile, would put a Veldrid assembly reference in the backend's IL,
and would be invisible to every public-surface scan there is.
`GpuPublicApiTests.NativeGpuBackend_ReferencesNoVeldridAssembly`
reflects over the built assembly's references and closes exactly that gap, which is why the helper's whole
contract is expressed in engine mirrors (`CrossCompiledPair` / `ShaderReflection` over `GpuVertexElement` and
`GpuResourceLayoutDescription`). Both are theories over `KhaozEngine.Gpu.D3D11` and `KhaozEngine.Gpu.Vulkan`,
so a third native backend joins them by adding one row each.

The same `InternalsVisibleTo` carries the other internal the backend needs, `IGpuDeviceLifecycle`: a natively
created device implements it to get the same disposal latch the Veldrid wrapper has, so a resource wrapper
disposed after the device no-ops instead of calling into freed driver objects.

### The shared-internal seam: four types all three backends now sit on

The shader toolchain above was the FIRST thing the backends shared. It is not the only thing any more.
[#531](https://github.com/APKiwiOrg/KhaozEngine/issues/531) re-assessed every candidate once a third backend
existed and moved two of them into `KhaozEngine.Gpu/Internal/`, on the same `InternalsVisibleTo` grants, so the
edge shape is unchanged and only its contents grew. That ruling and its three written refusals are the row 18
addendum to section 2.8 of
[design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md](design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md).

- **`Internal/DeviceLiveness`**, holding `IDeviceLiveness`, `DeviceLiveness` and `LiveDevice`. The volatile
  once-flipped token every resource wrapper reads before releasing anything. It absorbed a FOURTH copy that
  already lived in this namespace for the Veldrid wrappers, so the seam has one liveness type rather than four.
  WHERE in teardown each backend flips it still differs and lives in the caller, and the Metal backend's second
  use of the token as device IDENTITY (Apple silicon reports one `MTLDevice` per process, so a handle comparison
  decides nothing) works on the shared interface exactly as it did on the per-backend class.
- **`Internal/WaitTotals`**, **`Internal/WaitAccumulator`** and **`Internal/RingPatchStats`**: the counter
  CARRIERS behind `GpuDeviceCounters`. A count-and-duration snapshot with its volatile per-field `Sample`, the
  nine-line accumulator behind it, and the off-timeline deferral counters.

**What did NOT move is the counting, and that is the load-bearing half of the ruling.** The accumulation sites
stayed per backend because every one of them differs for an argued reason: a Direct3D 11 present has no acquire
to wait on, a `VkCommandPool` cannot be reset while its buffers are in flight where an `MTLCommandBuffer` is
single-use, and `nextDrawable` has no zero-timeout form to probe with where `vkAcquireNextImageKHR` does.
`DrainCount` is a shipped seam channel, so a shared drain would have to pick one counting rule and would change a
number in the field on two backends.

`KhaozEngine.TestSupport.Gpu` gets its own `InternalsVisibleTo` for one reason only: the three shared uniform-ring
adapters build their backend's ring, which takes a `WaitAccumulator`, and read the stall count off a `WaitTotals`.
It reaches nothing else internal to `KhaozEngine.Gpu`.

## Three flavours of the same idea

The pattern is applied at the granularity the dependency warrants:

1. **Separate opt-in backend package** (the strongest split): GPU, physics, netcode transport, persistence,
   the commerce wallet. The third-party reference lives in its own package so consumers pick it explicitly.
   Physics, worldstore, commerce SQL and the native Direct3D 11 GPU backends are genuinely opt-in (excluded
   from umbrellas). The Veldrid binding still ships inside `KhaozEngine.Gpu` because rendering is not optional
   for a windowed game and every consumer needs SOME device out of the box, and the LiteNetLib transport
   backend is deliberately bundled into the `Server` umbrella because a server needs a real transport out of
   the box. `KhaozEngine.Gpu.D3D11` is the first GPU backend to leave the seam package, which it can because it
   is an ALTERNATIVE implementation of a backend that already works rather than the only way to get a device.
2. **Seam + default + null, one package** (audio): the contract, the real OpenAL backend, and a no-op
   `Null*` backend live together. The null backend keeps audio headless-testable and lets a server run with
   no device, while still being one `add` for a game that wants sound.
3. **Containment** (windowing/input, glTF load, image + font decode, content validation): a single class or loader owns the raw dependency and hands
   the rest of the engine an immutable snapshot or an engine-native type. There is no second backend planned,
   but the dependency is still corralled to one place so it cannot leak across the codebase. The input rule
   ("only `AppWindow` touches Silk.NET/GLFW input statics") is enforced as a hard rule in
   [USING-KHAOZENGINE.md](USING-KHAOZENGINE.md) and `../AGENTS.md`. Since 14.25.0 the containment is split in
   two: `AppWindow` owns the Silk/GLFW binding and the platform reads, and `InputAccumulator` owns the pure
   raw-event to `InputState` state machine with those reads passed in as arguments. The rule is unchanged
   (`AppWindow` is still the sole toucher), but the half that has no platform dependency is now on the testable
   side of the line, which is what the rule was always for. Two bugs, focus-loss release semantics and
   first-frame cursor priming, had no headless test purely because that half sat inside the GLFW-bound class.

## GPU seam gate: every engine recording opens through `GpuRecording` (17.36.0)

A new kind of seam member, and the first one that is a GATE rather than a contract: `GpuRecording` is where the
portable one-open-recording-per-device rule on `IGpuCommandList.Begin` stopped being a paragraph and started
being a refusal. `GpuRecording.Open(device, list, owner)` begins the list and claims the device, the returned
`GpuRecordingScope` ends it and releases the claim, and a second `Open` on a device that already has one throws
`GpuNestedRecordingException` naming both sides. Every command list the engine opens goes through it: the
windowed frame loop, both snapshot hosts, the preview, the offscreen 2D captures, the ocean's priming pass, the
retire barrier, the mip generates and every readback.

**Why the gate is at the seam and not in a backend.** The backends genuinely disagree about a second concurrent
recording, and each disagreement is structural for that backend rather than an oversight: the Veldrid Direct3D11
leg refuses it, the three engine-owned native backends each tolerate N recordings for three different reasons,
and the Veldrid Metal and Vulkan legs silently produce half a frame. A seam whose behaviour changes when a
failed device creation falls back to another backend is not a seam, so the rule is enforced above all of them,
where it reads the same everywhere and is provable with no GPU at all. That makes it the first place in this
document where the seam constrains its own callers rather than only its implementers.

**What it does not reach**, stated because it bounds the guarantee. A consumer that calls
`IGpuCommandList.Begin` directly on a list of its own is invisible to the register and gets whatever its backend
does. That still covers the case that matters, because the OUTER list in a nested pair is almost always the
engine's own frame list, and the outer one is whose bindings the inner recording destroys.

**No cross-device coupling, by construction.** The register is keyed by device instance in a
`ConditionalWeakTable`, so a disposed device takes its entry with it, and each entry carries its own lock. No
lock at all is held while a backend is inside `Begin`, which matters because Begin BLOCKS by design on the
native Metal and Vulkan backends while their rings wait for the GPU. A process-wide gate around it would have
turned one device's backpressure into every other device's stall, which is exactly what the first cut did.

## GPU seam member: deferred retirement leaves Render3D (`GpuRetireQueue`, 17.37.0)

`GpuRetireQueue` is a new PUBLIC type on `KhaozEngine.Gpu` (with `GpuRetireBarrier` internal beside it), moved
wholesale out of `KhaozEngine.Render3D/Internal` where it was called `RetiredResourcePool`. It adds no reference
in either direction: the type only ever named `IGpuDevice`, `IGpuFence`, `IGpuCommandList` and `IDisposable`, so
it was already sitting one package too high, and `Render3D` reaches it now the same way it reaches every other
seam type. The move is what [#80](https://github.com/APKiwiOrg/KhaozEngine/issues/80) asked for: the safe
retire-instead-of-dispose idiom was hand-rolled per renderer, and a renderer that never copied it (Distortion,
Water) simply had the use-after-free instead.

**It is deliberately NOT a member of `IGpuDevice`.** `Retire(IDisposable)` on the interface was the shape #80
proposed, and it fails on two counts: a device has no frame boundary, which is the only point at which freeing is
provably safe, and adding an interface member breaks all four backend implementations plus every test fake, which
is a major-version change to fix a hitch. A seam-owned type that any renderer instantiates gets the same
by-construction safety with an additive surface.

**The recording gate above is what shapes its two factories, and this is the interesting part.** `Create` mints
its fence by opening a command list of its own, so it can only be advanced where nothing is recording on the
device. `Scene3D.Begin` is in the frame's prepare phase and qualifies. `SpriteBatch.NewFrame` is in the RECORD
phase on every host it has (`GameApp` calls it inside the frame's list, and both offscreen 2D captures call it
inside their own `GpuRecording.Open` scope), so a fenced queue there would be refused by the gate on every frame
that retired anything. `CreateFrameCounted` is that caller's answer: no fence, no drain on the frame path, and
the frame count carries the whole argument. Two renderers on one seam type, taking different paths through it for
a reason that is a property of WHERE they sit in the frame, not of what they render.

**And the gate reaches the teardown path too, which is the edge that is easy to miss.** `FlushAll` is public and
drains, so it is reachable from inside a recording exactly as a fenced `BeginFrame` is, with a worse failure: a
drain waits out SUBMITTED work, an open list has not been submitted, so the disposals behind that drain are a
use-after-free the drain appears to justify. It reads the same register (`GpuRecording.OpenOwner`) and refuses with
`GpuDrainDuringRecordingException`, which is why the queue holds its `IGpuDevice` at all rather than only the drain
delegate it used to take.

**The seam owns the BOUND as well as the mechanism (17.38.0,
[#425](https://github.com/APKiwiOrg/KhaozEngine/issues/425)).** Fence-polled ripeness means a batch lives until the
GPU reaches it, and nothing in the original shape said how many could pile up first, so the queue inherited its
ceiling from whatever happened to throttle the caller. That is the difference between a designed bound and an
emergent one, and it is why `MaxSealedBatches` (a `Create` parameter, default 8) sits on the queue rather than
being left to the backend's ring backpressure to imply: past it the queue drains once and frees the whole holding,
so the bound holds identically on a Veldrid backend with no ring at all and on an offscreen loop that never
presents. `ValveDrains` and the now-public `SealedBatchCount` are the two members that let a caller see it working.
The frame-counted factory needed none of this, since a frame count is already a bound.

## GPU-backend invariant: ONE uniform buffer per pipeline (Metal via Veldrid/SPIRV-Cross)

**MEASURED 2026-08-11: this is the INCUMBENT'S BUFFER NUMBERING, not a property of Metal.** The rule below is
KEPT and still binds every new render path, for the reason at the end of this section, but what it is a fact
ABOUT changed, so read the mechanism before designing around it.

**The mechanism.** Veldrid's `MTLResourceLayout` numbers a buffer by counting every buffer element declared in
the preceding sets, and SPIRV-Cross numbers only the arguments the stage it is emitting actually REFERENCES.
So a pipeline mis-binds when a stage references fewer buffers than the declared layout array puts before them:
a fragment function that reads set 1 alone is emitted at `buffer(0)` while Veldrid writes it at `buffer(1)`,
and the function reads a slot nothing wrote. All zero, silently, with no validation error, surfacing as
garbage geometry or unlit/black shading rather than as a failure. It holds offscreen as well as windowed.

**TWO SYMPTOMS COME OUT OF THAT ONE DISAGREEMENT, so match on the mechanism rather than on what you saw.**
Which one you get depends on whether the earlier buffer is bound to the READING stage at the index the function
ends up reading. Where nothing is bound there, the read is all zero, which is the shape measured above. Where
the earlier buffer IS bound to that stage, the index holds real bytes and the function reads ANOTHER buffer's
contents instead of nothing, which is the splat terrain's recorded signature. Same off-by-one either way.

**The narrower statement matters, because the broad one is false.** This section used to say that any pipeline
reading more than one uniform buffer mis-binds, full stop. Measured on an Apple M2 Max, two shapes that the
broad rule forbids bind CORRECTLY on the incumbent: a vertex stage reading two uniform buffers at sets 0 and
1, and a pipeline whose set-0 buffer is read by both stages with a fragment-only second buffer at set 1. The
shape that fails is the one the shipped record failed in, each stage referencing exactly one of the two
buffers. TEXTURES and samplers in a second set mapped fine in the shapes measured. Read that as a result about
those shapes rather than as a property of the texture index space: the count runs PER index space, so the
identical per-stage condition predicts a texture index disagreeing the same way. That prediction is not
measured here, and it is what the sample-all-textures-in-binding-order discipline already guards. This engine's
own incident record has two texture-space mis-binds in it, a model pass reading the normal texture through the
albedo sampler and a crease term reading depth data.

**The engine's own native Metal backend (`GpuBackendKind.MetalNative`) does not have the defect**, because it
binds at the index read out of each stage's own emission rather than at a counted one. All three shapes read
correct bytes there. The measurement is
`../KhaozEngine.Render.Tests/Gpu/MetalTwoUniformBufferGpuTests.cs`, three pixel-readback `[GpuFact]`s plus a
device-free row pinning the two numbers against each other, and section 2.3a of
`design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md` carries the values and the reasoning.

**One historical signature is NOT explained by that mechanism and was not reproduced.** The original
vertex-stage report is a bone-palette array losing everything past element 0 rather than a second buffer
reading all zero, and it was recorded under an older Veldrid and SPIRV-Cross pin. The two-uniform-buffer
vertex shape measured above does not reproduce it today. That narrows what is currently reproducible without
refuting the record, and the rule below keeps covering it either way.

**THE RULE IS UNCHANGED AND STILL BINDING**, and the measurement is not a licence to start spreading uniforms
across sets. `GpuBackendKind.Metal` is still selectable and still ships, so a pipeline written to the native
backend's binding would be correct there and read zeros on the incumbent, which is the same argument that keeps
the sample-textures-in-binding-order discipline. Lifting it is its own work with its own gates on all three
backends, tracked at https://github.com/APKiwiOrg/KhaozEngine/issues/604 and triggered by the retirement of the
Veldrid Metal leg rather than by this measurement.

The engine-wide rule for any new render path: the pipeline reads exactly ONE uniform buffer, at set 0
binding 0. Fold everything any stage needs from a UBO - the vertex's ViewProj / bone palette / per-instance
transforms AND the fragment's frame/lighting/shadow uniforms - into that single buffer, declared
identically in both stages (each stage uses its slice). Keep per-mesh TEXTURES at set 1 and up (fragment).

**Since 17.36.0 the shape this rule forbids is rejected STATICALLY rather than only written down here**:
`MslBindingOrder.CheckPrefix`, run by `ShaderValidation.ValidatePair`, throws when one stage's resources are not
a prefix of the layout's per index space, which is exactly the each-stage-references-a-different-buffer shape
measured above (see the `KhaozEngine.Gpu` README and `USING-KHAOZENGINE.md` for the guard's two checks and what
it deliberately stays silent about).

The model and splat-terrain passes follow this: the shadow-map matrix and the per-material splat params
ride in the SAME frame UBO after the point-light arrays, so each pass binds exactly one uniform buffer
(see the splat-params note in `../KhaozEngine.Render3D/Rendering/ModelRenderer.cs` and the `SplatVert`
comment in `../KhaozEngine.Render3D/Internal/ShaderSources.cs`). The modern particle pass follows it too: its
single set-0 frame UBO carries the clip-corrected ViewProj, the raw InvViewProj, the camera basis + eye, the
effect time, and the soft-fade / quality params, while every per-sprite value rides an instanced
vertex-attribute stream and the textures sit at set 0 bindings 1..5, sampled statically in binding order (the
Metal rule): the scene depth texture + sampler at 1 and 2, then the flipbook motion sheet, atlas sheet, and
atlas sampler at 3, 4, and 5 (motion precedes atlas so the static sample order supplies the warp vectors before
the atlas taps that consume them). Procedural sprites bind 1x1 dummy atlas + neutral motion textures for the
same static sample, so a procedural-only frame is byte-identical. Textures past the first UBO map fine. See
`../KhaozEngine.Render3D/Rendering/ParticleRenderer.cs`. The sibling screen-space distortion pass follows the
identical contract: one set-0 frame UBO (the same clip-corrected ViewProj + raw InvViewProj + camera basis +
params block, with a half-to-full texel ratio folded in), every per-sprite value on an instanced vertex-attribute
stream, and the scene depth texture + point sampler at set 0 bindings 1 and 2, writing a signed offset field that
the post chain's fullscreen apply pass re-samples the resolved colour through as its FIRST pass (see
`../KhaozEngine.Render3D/Rendering/DistortionRenderer.cs`). The vertex half of the fault plus the
fold-into-one fix are proven offscreen by `GpuSkinningReproGpuTests` variant 3
(`FoldMatrixIntoBoneBuffer_VertexReadsOneResource_ReadsEveryBone`). The SHIPPED GPU-skinning pass
(`Scene3D.UseGpuSkinning`, `SkinnedModelVert`/`SkinnedModelFrag`) is the full instance: one combined
per-draw UBO holding `{ Mvp; Model; P; <frame block>; bones[128] }` read by both stages (vertex =
matrices+bones, fragment = the frame block for lighting), with material maps at set 1. An earlier attempt
that kept the frame UBO fragment-only in a second binding/set rendered every skinned mesh black - the
second-UBO tell - and is the reason this note now says "per pipeline", not "per vertex stage". **That black
frame is the shape the 2026-08-11 measurement reproduces on the incumbent and the mechanism above explains
exactly**: the fragment referenced only the second buffer, so it was emitted at `buffer(0)` and written at
`buffer(1)`.

## Adding a new backend

To swap or add a backend for a seam that already has the separate-package split:

1. New project `KhaozEngine.<Area>.<Backend>` referencing the seam project and the third-party package.
2. Implement the seam interface (`IPhysicsWorld`, `INetTransport`, `IWorldStore`, ...). Keep it the **only**
   assembly that references the library.
3. Leave it out of the umbrella metapackages (`Foundation`/`Game2D`/`Game3D`/`Server`) unless it is
   non-optional, so it stays opt-in like `Physics.Bepu` / `Netcode.LiteNetLib` / `WorldStore.Sqlite`.
4. Headless test against the contract; for backends with a real device, gate device tests as the existing
   ones are.
5. Run the full doc sweep (this table, the package catalog in `../README.md` and `../AGENTS.md`) so the
   new package is listed everywhere it should be.

## Where to look in the code

| Seam | Contract | Backend |
|---|---|---|
| GPU | `../KhaozEngine.Gpu/GpuDeviceContext.cs`, `GpuInterfaces.cs`, `GpuBackendSelector.cs` | `../KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs` |
| Physics | `../KhaozEngine.Physics/IPhysicsWorld.cs` | `../KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs` |
| Netcode | `../KhaozEngine.Netcode/` (`INetTransport`, `LoopbackTransport`) | `../KhaozEngine.Netcode.LiteNetLib/` |
| Persistence | `../KhaozEngine.WorldStore/IWorldStore.cs`, `InMemoryWorldStore.cs` | `../KhaozEngine.WorldStore.Sqlite/`, `../KhaozEngine.WorldStore.SqlServer/` |
| Commerce wallet | `../KhaozEngine.Commerce/IWalletStore.cs`, `IGrantScheduleStore.cs`, `Entitlements.cs` (`IEntitlementValidator`), `InMemoryWalletStore.cs` | `../KhaozEngine.Commerce.Sqlite/SqliteWalletStore.cs`, `../KhaozEngine.Commerce.SqlServer/SqlServerWalletStore.cs` |
| Persistence enumeration | `../KhaozEngine.WorldStore/IEnumerableWorldStore.cs` | `InMemoryWorldStore.cs`, `SqliteWorldStore.cs`, `SqlServerWorldStore.cs` |
| Server ban list | `../KhaozEngine.NetWorld/IBanStore.cs`, `InMemoryBanStore.cs` | `WorldStoreBanStore.cs` |
| Admin HTTP endpoint | `../KhaozEngine.NetWorld/IAdminControllable.cs` (seam) | `../KhaozEngine.Server.Admin/` (Kestrel, ASP.NET Core) |
| Audio | `../KhaozEngine.Audio/IMusicBackend.cs`, `ISfxBackend.cs`, `Null*Backend.cs` | `../KhaozEngine.Audio/OpenAl*Backend.cs` |
| Server-status fetch | `../KhaozEngine.ServerStatus/IServerStatusSource.cs`, `ServerStatusReport.cs`, `ServerStatusClient.cs`, `ServerStatusEvaluator.cs` | `../KhaozEngine.ServerStatus/HttpServerStatusSource.cs` (contains `HttpClient`) |
| Server heartbeat | `../KhaozEngine.ServerStatus/ServerHeartbeat.cs` (`IServerHeartbeatSink`, `Null`/`InMemory` sinks), `ServerHeartbeatService.cs` | game-side SQL upsert (not in the engine) |
| Social / presence | `../KhaozEngine.Social/ISocialProvider.cs`, `NullSocialProvider.cs`, `SocialPresenceController.cs` | `../KhaozEngine.Social.Discord/DiscordSocialProvider.cs` (+ `Internal/DiscordIpcClient.cs`, `NamedPipeDiscordTransport.cs`) |
| Player identity | `../KhaozEngine.Identity/IIdentityProvider.cs`, `IIdentityValidator.cs`, `ITokenCache.cs`, `IBrowserLauncher.cs`, `ILoopbackListener.cs`, `IdentitySession.cs`, `SessionToken.cs`, `FileTokenCache.cs` | `../KhaozEngine.Identity.Oidc/OidcClientProvider.cs`, `OidcTokenValidator.cs`, `SystemBrowserLauncher.cs`, `HttpLoopbackListener.cs`; `../KhaozEngine.Identity.Discord/DiscordClientProvider.cs`, `DiscordTokenValidator.cs` |
| Windowing/input | `../KhaozEngine.Windowing/AppWindow.cs` (sole toucher; the pure event-to-snapshot half is `InputAccumulator.cs`) | Silk.NET/GLFW, contained |
| glTF load | `../KhaozEngine.Render3D/Models/GltfLoader.cs` (contains SharpGLTF) | (containment) |
| Image decode | `../KhaozEngine.Render2D/ImageRgba.cs` (contains StbImageSharp) | (containment) |
| Font rasterization | `../KhaozEngine.Render2D/SpriteFont.cs` (contains StbTrueTypeSharp) | (containment) |
| Content validation | `../KhaozEngine.Content/JsonSchemaValidator.cs` (contains JsonSchema.Net) | (containment) |
| MCP server protocol | `../KhaozEngine.MapEdit.Tool/Program.cs`, `../KhaozEngine.TileEdit.Tool/Program.cs` (dev tools, the only referencers) | (containment) |
