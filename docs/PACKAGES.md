# Packages

Every KhaozEngine package, one line each, grouped by area. Each name links the package's own README, which is
its detailed page and ships inside its nupkg. `scripts/check-doc-versions.sh` fails when a packable package has
no row here, or when a row names a package that is gone or no longer packable.

**Depends on** is curated. It names the engine packages that explain what a row gives you, with notable
third-party packages in parentheses. The csproj `ProjectReference` set is the exact graph, and
[DEPENDENCY-SEAMS.md](DEPENDENCY-SEAMS.md) explains how each third-party library sits behind an engine seam.

**Carried by** says which umbrella brings the package in, directly or through another umbrella. Every umbrella
means `Foundation` carries it, and the other three umbrellas carry `Foundation`. Opt-in means no umbrella
carries it, for the reasons [below](#why-some-packages-are-opt-in).

Every package shares one version, set by `<KhaozEngineVersion>` in `Directory.Build.props`. Packages target
`net10.0`, the two Roslyn analyzers target `netstandard2.0`, and `ServerStatus`, `Http`, `Diagnostics` and
`Primitives` also target `net8.0` so a status endpoint can run on Azure Functions.

## Umbrellas

Most projects need one reference. An umbrella has no code of its own. Its README lists everything it pulls in,
and the guard keeps that list matching the umbrella's csproj.

| Package | For | Carries |
|---|---|---|
| [KhaozEngine.Game2D](../KhaozEngine.Game2D/README.md) | A desktop 2D game | Windowing, the three GPU backends, Render2D, Gui, Audio, Particles, Telegraphs, Game, Foundation and the analyzers |
| [KhaozEngine.Game3D](../KhaozEngine.Game3D/README.md) | A desktop 3D game | Game2D plus Render3D and its 3D arms: Render3D.Ecs, Game.Render3D, Attachments.Render3D, Particles.Render3D, Telegraphs.Render3D, Terrain.Render3D and TileWorld.Render3D |
| [KhaozEngine.Server](../KhaozEngine.Server/README.md) | A headless authoritative server or server tool | Foundation plus Netcode, Netcode.LiteNetLib, Simulation, Replication, Sharding, WorldStore, NetWorld, TileWorld.Netcode, ItemInstances.Journal, Catalog.Authoring and Catalog.Netcode, with no graphics |
| [KhaozEngine.Foundation](../KhaozEngine.Foundation/README.md) | A gameplay logic library or headless tool | The GPU-free core, world, gameplay and content packages, with no renderer, windowing or audio |

A granular package can be referenced on its own or beside an umbrella, for example `Game2D` with
`Netcode.LiteNetLib` for a 2D multiplayer game.

A common split is a game head that references `Game2D` or `Game3D` and a gameplay logic library that references
only `Foundation`. The logic then tests headless and a server can share it. Hardpoint is built that way, and
SpaceGame keeps its deterministic lockstep simulation in a separate project on the foundation packages.

## Core

GPU-free building blocks almost every other package sits on.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Primitives](../KhaozEngine.Primitives/README.md) | Shared colors, math, random sources, pooling, viewports and geometry helpers. | None | Every umbrella |
| [KhaozEngine.Diagnostics](../KhaozEngine.Diagnostics/README.md) | Captures logs, crash reports, runtime telemetry, and connection health, with bounded log history and report redaction. | None | Every umbrella |
| [KhaozEngine.App](../KhaozEngine.App/README.md) | Provides application metadata, data paths, service registration, localization, clean restarts, and single-instance control. | Diagnostics, Platform | Every umbrella |
| [KhaozEngine.Platform](../KhaozEngine.Platform/README.md) | Connects games to native clipboards, application icons, Windows taskbar identity, developer consoles, and process control. | None | Every umbrella |
| [KhaozEngine.Serialization](../KhaozEngine.Serialization/README.md) | Shares tolerant JSON readers and consistent serializer defaults for configuration, persistence, and entity data. | None | Every umbrella |
| [KhaozEngine.Persistence](../KhaozEngine.Persistence/README.md) | Stores settings and saves with atomic writes, tamper detection, backups, migration, recovery, and queued batch logging. | Diagnostics, App, Serialization | Every umbrella |
| [KhaozEngine.Content](../KhaozEngine.Content/README.md) | Loads JSON configuration, validates schemas, and enforces content schemas during consumer builds. | Diagnostics, Serialization, Primitives (+ JsonSchema.Net) | Every umbrella |
| [KhaozEngine.Ecs](../KhaozEngine.Ecs/README.md) | Archetype entity-component system with by-reference components, safe iteration, deferred commands, parallel queries, and world snapshots. | Serialization, Simulation, Primitives | Every umbrella |
| [KhaozEngine.Determinism](../KhaozEngine.Determinism/README.md) | Controls and restores per-thread floating-point settings for deterministic fixed-tick simulations. | Diagnostics | Every umbrella |
| [KhaozEngine.Imaging](../KhaozEngine.Imaging/README.md) | PNG reading and writing, JPEG encoding, thumbnail decoding and image regression comparisons. | None | Every umbrella |
| [KhaozEngine.Http](../KhaozEngine.Http/README.md) | Sends HTTP requests with bounded attempts, per-attempt timeouts, configurable backoff, and cancellation-aware retries. | None | Every umbrella |

## GPU and windowing

The graphics seam, the three engine-owned backends and the window that drives them.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Gpu](../KhaozEngine.Gpu/README.md) | Graphics backend selection, device interfaces, resource retirement, compute dispatch, shader validation and diagnostics for renderers. | Diagnostics, Primitives (+ Silk.NET.Shaderc and Silk.NET.SPIRV.Cross, with native packages) | Game2D, Game3D |
| [KhaozEngine.Gpu.Metal](../KhaozEngine.Gpu.Metal/README.md) | Metal graphics and compute rendering on macOS, with device selection, shader caching and frame capture. | Gpu, Diagnostics | Game2D, Game3D |
| [KhaozEngine.Gpu.D3D11](../KhaozEngine.Gpu.D3D11/README.md) | Direct3D 11 graphics and compute rendering on Windows, with adapter selection, driver diagnostics and completion fences. | Gpu, Diagnostics (+ Vortice) | Game2D, Game3D |
| [KhaozEngine.Gpu.Vulkan](../KhaozEngine.Gpu.Vulkan/README.md) | Vulkan graphics and compute rendering on Linux and Windows, with device selection, validation controls and pipeline caching. | Gpu, Diagnostics (+ Silk.NET.Vulkan) | Game2D, Game3D |
| [KhaozEngine.Windowing](../KhaozEngine.Windowing/README.md) | Desktop windows, frame pacing, DPI-aware viewports, input, gestures and rebindable actions. | Gpu, Platform, Primitives | Game2D, Game3D |

## Rendering, audio and GUI

What draws, plays and lays out a frame, plus the game loop and headless screenshots.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Render2D](../KhaozEngine.Render2D/README.md) | Sprites, fonts, text layout, cameras, primitives and offscreen capture. | Windowing, Gpu, Primitives, Imaging | Game2D, Game3D |
| [KhaozEngine.Render3D](../KhaozEngine.Render3D/README.md) | Mesh rendering, animation, materials, lighting, shadows, cameras, water and visual effects. | Windowing, Gpu, Render2D, Collision, Physics, Primitives | Game3D |
| [KhaozEngine.Render3D.Ecs](../KhaozEngine.Render3D.Ecs/README.md) | Components and a scene binder for drawing entities with 3D transforms, meshes and materials. | Ecs, Render3D | Game3D |
| [KhaozEngine.Gui](../KhaozEngine.Gui/README.md) | Immediate-mode controls, retained widgets, screen stacks, menus, notifications and editor tools. | App, Windowing, Render2D, Updates, Diagnostics, Netcode | Game2D, Game3D |
| [KhaozEngine.Audio](../KhaozEngine.Audio/README.md) | Streaming music, sound effects, positional audio, volume buses and output-device recovery. | Diagnostics, Primitives | Game2D, Game3D |
| [KhaozEngine.Particles](../KhaozEngine.Particles/README.md) | Deterministic particles, emitter scheduling, motion trails, attraction and screen shake. | Primitives | Game2D, Game3D |
| [KhaozEngine.Particles.Render3D](../KhaozEngine.Particles.Render3D/README.md) | Draws particle simulations as sprites, trails, lights, flipbooks and distortion effects, with ready-to-use presets. | Particles, Render3D | Game3D |
| [KhaozEngine.Telegraphs](../KhaozEngine.Telegraphs/README.md) | Animated attack warnings and danger zones with reusable styles and a 2D renderer. | Render2D, Primitives | Game2D, Game3D |
| [KhaozEngine.Telegraphs.Render3D](../KhaozEngine.Telegraphs.Render3D/README.md) | Paints animated danger zones and fading impact marks onto 3D ground and terrain. | Telegraphs, Render3D | Game3D |
| [KhaozEngine.Attachments](../KhaozEngine.Attachments/README.md) | Defines and validates rigid and skinned attachment sets, canonical JSON and self-contained mesh assets. | None | Game3D |
| [KhaozEngine.Attachments.Render3D](../KhaozEngine.Attachments.Render3D/README.md) | Binds attachments to animated skeletons and manages shared uploads, drawing and outlines. | Attachments, Render3D | Game3D |
| [KhaozEngine.Game](../KhaozEngine.Game/README.md) | Game loop, scenes, UI passes, diagnostics and staged startup screens. | Windowing, Render2D, Gui, Updates, ServerStatus, Simulation | Game2D, Game3D |
| [KhaozEngine.Game.Render3D](../KhaozEngine.Game.Render3D/README.md) | Connects the game loop to 3D scenes and supplies character movement, facing and animation. | Game, Render3D, Locomotion, Physics | Game3D |
| [KhaozEngine.Snapshot](../KhaozEngine.Snapshot/README.md) | Captures named 2D screenshots headlessly and handles PNG encoding, file output and logging. | Render2D, Imaging | Opt-in |
| [KhaozEngine.Snapshot.Render3D](../KhaozEngine.Snapshot.Render3D/README.md) | Adds 3D scene screenshots to the shared snapshot runner. | Snapshot, Render3D | Opt-in |

## Worlds, physics and movement

Terrain, tile worlds, native maps, navigation, physics and character movement.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Terrain](../KhaozEngine.Terrain/README.md) | Deterministic terrain sampling, prop scattering and world streaming shared by clients, servers and tools. | Primitives, Collision, Diagnostics | Every umbrella |
| [KhaozEngine.Terrain.Render3D](../KhaozEngine.Terrain.Render3D/README.md) | Terrain meshes, material blending, streamed chunks, vegetation and distant prop clusters. | Terrain, Render3D, MapDoc | Game3D |
| [KhaozEngine.TileWorld](../KhaozEngine.TileWorld/README.md) | Tile-based worlds with stacked planes, terrain heights, objects, markers, collision, pathfinding, prefabs and optional foliage. | Primitives, Serialization, Content | Every umbrella |
| [KhaozEngine.TileWorld.Editing](../KhaozEngine.TileWorld.Editing/README.md) | Reversible tile-world edits with undo, redo, gesture coalescing, heightmap import and collision updates. | TileWorld, Imaging | Every umbrella |
| [KhaozEngine.TileWorld.Physics](../KhaozEngine.TileWorld.Physics/README.md) | Static tile-world colliders, ground sampling and water sampling for character movement. | TileWorld, Physics, Locomotion | Opt-in |
| [KhaozEngine.TileWorld.Render3D](../KhaozEngine.TileWorld.Render3D/README.md) | Renders tile-world ground, water, props, roofs and foliage with streaming, picking and screenshot capture. | TileWorld, Render3D, Render2D, Terrain.Render3D, Attachments.Render3D | Game3D |
| [KhaozEngine.TileWorld.WorldMap](../KhaozEngine.TileWorld.WorldMap/README.md) | Tile-world terrain, water, footprints and markers exposed as a north-up world map source. | WorldMap, TileWorld | Opt-in |
| [KhaozEngine.WorldMap](../KhaozEngine.WorldMap/README.md) | Stylized world map images, contours, symbols, feature anchors and chunk caching for authored worlds. | Primitives | Opt-in |
| [KhaozEngine.WorldMap.Gui](../KhaozEngine.WorldMap.Gui/README.md) | Pannable, zoomable world map widget with background builds, budgeted uploads, labels, icons and live markers. | WorldMap, Gui | Opt-in |
| [KhaozEngine.MapDoc](../KhaozEngine.MapDoc/README.md) | Versioned map documents with terrain, prefabs, interiors, markers, bounded water and cosmetic foliage. | Primitives, Serialization, Content, Terrain | Every umbrella |
| [KhaozEngine.MapDoc.Physics](../KhaozEngine.MapDoc.Physics/README.md) | Static colliders, physical queries and residency identities for native map documents. | MapDoc, Physics, Movement, Locomotion | Opt-in |
| [KhaozEngine.MapEditor](../KhaozEngine.MapEditor/README.md) | In-engine map authoring with viewport tools, property editing, undo, redo, settings and prefab workflows. | Dungeon, MapDoc, Gui, Persistence, Terrain.Render3D, Render3D, Game, Game.Render3D | Opt-in |
| [KhaozEngine.Navigation](../KhaozEngine.Navigation/README.md) | Clearance-aware navigation grids, layered paths and NPC steering for deterministic world movement. | Primitives, Collision, Terrain | Every umbrella |
| [KhaozEngine.Dungeon](../KhaozEngine.Dungeon/README.md) | Deterministic multi-level dungeons with rooms, corridors, stairs, ceilings, map output and navigation. | Primitives, MapDoc, Physics, Navigation | Every umbrella |
| [KhaozEngine.Collision](../KhaozEngine.Collision/README.md) | Provides deterministic collision queries, spatial broadphase, static-world push-out, and walkable prop surfaces. | None | Every umbrella |
| [KhaozEngine.Physics](../KhaozEngine.Physics/README.md) | Defines headless 3D physics, rigid bodies, joints, collision queries, baked shapes, and support-query measurements. | None | Every umbrella |
| [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu/README.md) | Runs deterministic 3D physics through Bepu with rigid bodies, joints, powered drives, and collision queries. | Physics (+ BepuPhysics) | Opt-in |
| [KhaozEngine.Locomotion](../KhaozEngine.Locomotion/README.md) | Shared character movement for local control, server simulation and prediction, including collision, jumping, slopes, wading and swimming. | Primitives, Physics | Every umbrella |
| [KhaozEngine.Movement](../KhaozEngine.Movement/README.md) | Body-aware reach, absolute ground movement, physics-backed navigation profiles and exact range steering. | Locomotion, Navigation, Physics | Opt-in |
| [KhaozEngine.SegmentRig](../KhaozEngine.SegmentRig/README.md) | Procedural poses, action strokes, skeleton transforms and held-item sockets for rigid-segment humanoids and quadrupeds. | None | Every umbrella |

## Gameplay and content

Content catalogs and the gameplay systems that read them.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Catalog](../KhaozEngine.Catalog/README.md) | Loads and validates versioned content packs, localized strings and loot tables for clients and servers. | Primitives | Every umbrella |
| [KhaozEngine.Catalog.GameTypes](../KhaozEngine.Catalog.GameTypes/README.md) | Provides content schemas and validators for food, equipment, shops, drops, gathering, crafting, skills and tuning. | Catalog | Every umbrella |
| [KhaozEngine.Catalog.Authoring](../KhaozEngine.Catalog.Authoring/README.md) | Authors, audits, publishes, compares, upgrades and exchanges versioned content catalogs. | Catalog | Server |
| [KhaozEngine.Catalog.Netcode](../KhaozEngine.Catalog.Netcode/README.md) | Checks connecting clients against the server's content version and returns structured update refusals. | Catalog, Netcode | Server |
| [KhaozEngine.Catalog.Sqlite](../KhaozEngine.Catalog.Sqlite/README.md) | Persists content drafts, published versions, audit records and localized text in SQLite. | Catalog.Authoring, Sqlite (+ Microsoft.Data.Sqlite) | Opt-in |
| [KhaozEngine.Catalog.SqlServer](../KhaozEngine.Catalog.SqlServer/README.md) | Persists shared content catalogs in SQL Server or Azure SQL with transactional publishing. | Catalog.Authoring (+ Microsoft.Data.SqlClient) | Opt-in |
| [KhaozEngine.Catalog.AzureBlob](../KhaozEngine.Catalog.AzureBlob/README.md) | Stores immutable content pack objects in Azure Blob Storage for public client downloads. | Catalog (+ Azure.Storage.Blobs) | Opt-in |
| [KhaozEngine.Items](../KhaozEngine.Items/README.md) | Stores item stacks in fixed slots with stacking, overflow handling, slot operations and save codecs. | None | Every umbrella |
| [KhaozEngine.ItemInstances](../KhaozEngine.ItemInstances/README.md) | Provides per-item properties, identity, paged storage, replication, generation, crafting and integer stat evaluation. | Items, Catalog, Primitives, Diagnostics | Every umbrella |
| [KhaozEngine.ItemInstances.Journal](../KhaozEngine.ItemInstances.Journal/README.md) | Loads paged item containers and commits ordered operations with replayable events and per-page projection writes. | ItemInstances, WorldStore | Server |
| [KhaozEngine.Skills](../KhaozEngine.Skills/README.md) | Provides skill experience curves, parent awards, progression readouts, curve migration and save codecs. | None | Every umbrella |
| [KhaozEngine.Stats](../KhaozEngine.Stats/README.md) | Computes stats from base values and removable equipment, skill or buff modifiers. | None | Every umbrella |
| [KhaozEngine.Progression](../KhaozEngine.Progression/README.md) | Schedules periodic wall-clock rewards with non-stacking availability and clock-step safety. | None | Every umbrella |
| [KhaozEngine.Objectives](../KhaozEngine.Objectives/README.md) | Tracks achievements, quests and challenges through metrics, conditions and completion events. | App | Every umbrella |

## Networking and servers

Simulation, networking, replication and the authoritative server stack.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Simulation](../KhaozEngine.Simulation/README.md) | Fixed-rate simulation hosting, deterministic job scheduling, world clocks and shutdown countdowns. | Determinism | Every umbrella |
| [KhaozEngine.Netcode.Abstractions](../KhaozEngine.Netcode.Abstractions/README.md) | Transport-independent contracts for splitting entity updates into reliable and unreliable channels. | None | Game2D, Game3D, Server |
| [KhaozEngine.Netcode](../KhaozEngine.Netcode/README.md) | Prediction, reconciliation, sessions, authentication and in-memory networking behind a transport-independent byte interface. | Netcode.Abstractions | Game2D, Game3D, Server |
| [KhaozEngine.Netcode.LiteNetLib](../KhaozEngine.Netcode.LiteNetLib/README.md) | Reliable UDP transport and split-channel delivery through LiteNetLib. | Netcode (+ LiteNetLib) | Server |
| [KhaozEngine.Replication](../KhaozEngine.Replication/README.md) | ECS snapshots, interest-filtered deltas and acknowledged state reconstruction for multiplayer worlds. | Ecs | Server |
| [KhaozEngine.Sharding](../KhaozEngine.Sharding/README.md) | Partitions authoritative worlds into cells with border ghosts, authority handoffs, client interest and persistence snapshots. | Ecs, Simulation, Physics, Replication | Server |
| [KhaozEngine.NetWorld](../KhaozEngine.NetWorld/README.md) | Authoritative continuous-world movement with client prediction, replication, persistence, administration and game-message channels. | Simulation, Locomotion, Physics, Netcode, Replication, Ecs, Sharding, WorldStore | Server |
| [KhaozEngine.TileWorld.Netcode](../KhaozEngine.TileWorld.Netcode/README.md) | Authoritative tile-world movement with client prediction, actors, combat, ground items and persistence. | TileWorld, Netcode, Replication, Sharding, Simulation, WorldStore, Diagnostics | Server |
| [KhaozEngine.WorldStore](../KhaozEngine.WorldStore/README.md) | Checkpoint storage and durable mutation journals with recovery, projections and bounded commit execution. | None | Server |
| [KhaozEngine.WorldStore.Sqlite](../KhaozEngine.WorldStore.Sqlite/README.md) | Embedded SQLite checkpoint and mutation-journal storage for development, tests and single-node servers. | WorldStore, Sqlite (+ Microsoft.Data.Sqlite) | Opt-in |
| [KhaozEngine.WorldStore.SqlServer](../KhaozEngine.WorldStore.SqlServer/README.md) | SQL Server and Azure SQL checkpoint and mutation-journal storage for production servers. | WorldStore (+ Microsoft.Data.SqlClient) | Opt-in |
| [KhaozEngine.Server.Admin](../KhaozEngine.Server.Admin/README.md) | HTTPS server administration and content-catalog actions over a TLS listener with bearer-token authentication. | NetWorld, WorldStore, Catalog.Authoring (+ ASP.NET Core) | Opt-in |
| [KhaozEngine.ServerStatus](../KhaozEngine.ServerStatus/README.md) | Polls server health and version reports, evaluates connection readiness, and drives heartbeat and status displays. | Diagnostics, Primitives | Every umbrella |

## Accounts and services

Sign-in, accounts, wallets, social presence, bug reports and updates.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Identity](../KhaozEngine.Identity/README.md) | Coordinates player sign-in, cached sessions, credential refresh, verified identities, session tokens, and authentication exchange contracts. | Diagnostics, Platform, Serialization | Every umbrella |
| [KhaozEngine.Identity.Oidc](../KhaozEngine.Identity.Oidc/README.md) | Signs players in through OpenID Connect and verifies identity tokens against issuer discovery and signing keys. | Identity (+ Microsoft.IdentityModel.Protocols.OpenIdConnect, Microsoft.IdentityModel.JsonWebTokens) | Opt-in |
| [KhaozEngine.Identity.Discord](../KhaozEngine.Identity.Discord/README.md) | Signs players in through Discord OAuth2 and verifies access tokens against the game's Discord application. | Identity, Platform | Opt-in |
| [KhaozEngine.Identity.Exchange](../KhaozEngine.Identity.Exchange/README.md) | Exchanges verified provider credentials for game-server tokens after account admission checks. | Identity, Accounts, Netcode | Opt-in |
| [KhaozEngine.Identity.Exchange.AspNetCore](../KhaozEngine.Identity.Exchange.AspNetCore/README.md) | Hosts the authentication exchange as a bounded ASP.NET Core endpoint with trusted-proxy handling and private responses. | Identity.Exchange (+ Microsoft.AspNetCore.App) | Opt-in |
| [KhaozEngine.Accounts](../KhaozEngine.Accounts/README.md) | Maintains account records, whitelist decisions and bans behind verified sign-ins. | Netcode | Opt-in |
| [KhaozEngine.Accounts.Sqlite](../KhaozEngine.Accounts.Sqlite/README.md) | Persists account records, whitelist decisions and bans in SQLite. | Accounts, Sqlite (+ Microsoft.Data.Sqlite) | Opt-in |
| [KhaozEngine.Accounts.SqlServer](../KhaozEngine.Accounts.SqlServer/README.md) | Persists account records, whitelist decisions and bans in SQL Server or Azure SQL. | Accounts (+ Microsoft.Data.SqlClient) | Opt-in |
| [KhaozEngine.Commerce](../KhaozEngine.Commerce/README.md) | Provides server-authoritative currency wallets, idempotent transactions, entitlement redemption and periodic grants. | Progression | Opt-in |
| [KhaozEngine.Commerce.Sqlite](../KhaozEngine.Commerce.Sqlite/README.md) | Persists currency wallets, transaction ledgers and periodic grant schedules in SQLite. | Commerce, Sqlite (+ Microsoft.Data.Sqlite) | Opt-in |
| [KhaozEngine.Commerce.SqlServer](../KhaozEngine.Commerce.SqlServer/README.md) | Persists currency wallets, transaction ledgers and periodic grant schedules in SQL Server or Azure SQL. | Commerce (+ Microsoft.Data.SqlClient) | Opt-in |
| [KhaozEngine.Sqlite](../KhaozEngine.Sqlite/README.md) | Manages SQLite connection lifetimes, serialized operations and additive schema initialization for database providers. | (+ Microsoft.Data.Sqlite) | Opt-in |
| [KhaozEngine.Social](../KhaozEngine.Social/README.md) | Coordinates rich presence, local social identity, invitations, and reconnecting providers through a shared interface. | Diagnostics | Every umbrella |
| [KhaozEngine.Social.Discord](../KhaozEngine.Social.Discord/README.md) | Publishes Discord Rich Presence and handles local identity, invitations, and join requests through desktop IPC. | Social | Opt-in |
| [KhaozEngine.BugReports](../KhaozEngine.BugReports/README.md) | Builds redacted bug-report bundles with offline delivery, local viewing, and optional server snapshots. | BugReports.Contracts, Diagnostics, Http, Platform | Opt-in |
| [KhaozEngine.BugReports.Client](../KhaozEngine.BugReports.Client/README.md) | Adds a rebindable report trigger, rolling video history, screenshots, report sections, confirmation, and submission feedback to games. | App, BugReports, BugReports.Contracts, Diagnostics, Game, Gpu, Gui, Imaging, Windowing | Opt-in |
| [KhaozEngine.BugReports.Contracts](../KhaozEngine.BugReports.Contracts/README.md) | Defines bug-report bundles, validation rules, media indexes, reporter identity, and shared relay messages. | None | Opt-in |
| [KhaozEngine.BugReports.Relay](../KhaozEngine.BugReports.Relay/README.md) | Turns authenticated bug reports and server snapshots into GitHub issues through durable queued processing. | BugReports, BugReports.Contracts, Diagnostics, Http | Opt-in |
| [KhaozEngine.BugReports.Relay.Azure](../KhaozEngine.BugReports.Relay.Azure/README.md) | Stores report media, queued work, and quotas in Azure Storage, with poison-message recovery. | BugReports.Relay (+ Azure.Storage.Blobs, Azure.Storage.Queues, Azure.Data.Tables) | Opt-in |
| [KhaozEngine.Updates](../KhaozEngine.Updates/README.md) | Checks signed updates, downloads changed files, applies them safely, rolls back failures, and relaunches the application. | Diagnostics, Platform, Primitives | Every umbrella |

## Development

Analyzers, test kits, the Debug-only automation endpoint and author-time command-line tools.

| Package | What it gives you | Depends on | Carried by |
|---|---|---|---|
| [KhaozEngine.Automation](../KhaozEngine.Automation/README.md) | Debug-only playtest endpoint for injected input, frame stepping, state inspection and game-defined commands. | Windowing | Opt-in |
| [KhaozEngine.Localization.Analyzers](../KhaozEngine.Localization.Analyzers/README.md) | Checks player-facing localization usage and generates typed string keys from neutral resource files. | None at runtime | Game2D, Game3D |
| [KhaozEngine.CodeHealth.Analyzers](../KhaozEngine.CodeHealth.Analyzers/README.md) | Enforces file-size limits and prevents files from growing beyond recorded baselines. | None at runtime | Every umbrella |
| [KhaozEngine.Localization.TestKit](../KhaozEngine.Localization.TestKit/README.md) | Checks localization completeness and format placeholders across neutral resources and shipped languages. | App | Opt-in |
| [KhaozEngine.Gpu.TestKit](../KhaozEngine.Gpu.TestKit/README.md) | GPU availability checks and golden-image comparisons for a game's tests without requiring a particular test framework. | Gpu, Gpu.D3D11, Gpu.Vulkan, Gpu.Metal, Imaging | Opt-in |
| [KhaozEngine.Updates.Tool](../KhaozEngine.Updates.Tool/README.md) | Generates, signs, and verifies update manifests from publish pipelines. | Updates | Opt-in |
| [KhaozEngine.Sfx.Tool](../KhaozEngine.Sfx.Tool/README.md) | Generates sound effects from manifests and bakes encoded assets while skipping unchanged entries. | Serialization | Opt-in |
| [KhaozEngine.PropSurface.Tool](../KhaozEngine.PropSurface.Tool/README.md) | Bakes prop collision shapes and walkable heightmaps from kit manifests. | Render3D, Physics | Opt-in |
| [KhaozEngine.MapEdit.Tool](../KhaozEngine.MapEdit.Tool/README.md) | Map document authoring over MCP, with queries, undoable edits, prefab workflows and optional GPU captures. | MapDoc, MapDoc.Physics, MapEditor, Imaging (+ ModelContextProtocol, Microsoft.Extensions.Hosting) | Opt-in |
| [KhaozEngine.TileEdit.Tool](../KhaozEngine.TileEdit.Tool/README.md) | Tile-world authoring over MCP, with undoable edits, heightmaps, foliage, clearance checks and optional GPU captures. | TileWorld.Editing, TileWorld.Render3D, Imaging (+ ModelContextProtocol, Microsoft.Extensions.Hosting) | Opt-in |

## Why some packages are opt-in

A package stays out of every umbrella when most projects taking that umbrella would not want it. Reference it
directly.

- **Backends and providers.** `Physics.Bepu`, the `.Sqlite` and `.SqlServer` stores for Catalog, WorldStore,
  Accounts and Commerce, `Catalog.AzureBlob`, `BugReports.Relay.Azure`, `Identity.Oidc`, `Identity.Discord`
  and `Social.Discord`. The host picks its backend. Most of these seams ship in an umbrella, but `Accounts`,
  `Commerce` and `BugReports.Relay` are opt-in themselves, so their providers need the seam referenced too.
  `Sqlite` comes in with the SQLite providers.
- **Physics under a world.** `TileWorld.Physics` and `MapDoc.Physics`. A tile world or native map needs no
  physics to load, render or serve, so only a game that wants colliders adds them, with the backend it picks.
- **Composition.** `Movement` composes Locomotion, Navigation and Physics without choosing a backend.
- **Server-only services.** `Accounts`, `Identity.Exchange`, `Identity.Exchange.AspNetCore` and `Server.Admin`.
  The last two need the ASP.NET Core shared framework, which a simulation server that runs no endpoint should
  not carry. A client needs only the exchange's wire types, which ride `Identity` inside `Foundation`.
- **Optional features.** `Commerce`, `WorldMap`, `TileWorld.WorldMap`, `WorldMap.Gui` and the `BugReports`
  family.
- **Authoring and testing.** `MapEditor`, `Snapshot`, `Snapshot.Render3D`, the two test kits and the
  command-line tools. Shipping games leave them out.
- **Automation.** A shipping client must contain no automation, not merely refuse to enable it. Reference
  `Automation` under `Condition="'$(Configuration)' == 'Debug'"` so the Release `deps.json` carries no reference
  to it, and never put it in an umbrella.

## In the repository but not packed

- Tests: one `KhaozEngine.<Area>.Tests` project per area, `KhaozEngine.Tests` for cross-cutting tests, and the
  `KhaozEngine.TestSupport` helpers they share.
- Samples: [KhaozEngine.Showcase](../KhaozEngine.Showcase/README.md), the feature tour and capture testbed,
  [SnapshotTool](../SnapshotTool/README.md), the headless snapshot runner, and `MmoServerSample`, a reference
  dedicated MMO server.
- `KhaozEngine.Content.Validator`, which ships inside `KhaozEngine.Content` rather than as its own package.
- `KhaozEngine.Benchmarks`, the AOT probes and the offline asset and measurement tools under `tools/`.

## Adding a package

Give it a row in the area it belongs to, ship its own `README.md` as `<PackageReadmeFile>`, and if an umbrella
carries it, add it to that umbrella's csproj and its README's Pulls in list. Then run
`bash scripts/check-doc-versions.sh`.
