# KhaozEngine.Foundation

Umbrella metapackage for the MonoGame-free, GPU-free foundation. Contains no code, it is a
curated dependency group: one `<PackageReference>` gives a gameplay-logic library, a tool, or
any non-rendering project the engine's shared building blocks without pulling Windowing,
Render2D/3D, Gui, Audio, or any GPU.

Pulls in:

- `KhaozEngine.Primitives` - the zero-dependency leaf: `Color`, RNGs, `MathUtil`, `Easing`, `Rect`, pooling helpers.
- `KhaozEngine.App` - `BuildMetadata`, `AppDataPaths`, `ServiceLocator`, `LocalizationManager`.
- `KhaozEngine.Content` - JSON config/content loading with schema validation.
- `KhaozEngine.Serialization` - shared `System.Text.Json` defaults (`JsonDefaults`).
- `KhaozEngine.Catalog` - the tunable-content catalog's read half: the frozen content type registry with its
  field schemas and the seven engine content types, the content-addressed pack formats, the `IPackStore` seam,
  the `IContentSnapshot` read side and the pure validator. No third-party dependency.
- `KhaozEngine.Persistence` - tamper-deterrent saves, atomic writes, `SettingsManager<T>`, `GameStorage`.
- `KhaozEngine.ItemInstances` - the per-item instance record over `Items` and `Catalog`: the canonical tagged
  property payload an owned item carries beyond its definition id, the property registry that gives every kind a
  band, a visibility, a fixed identification mask bit and a field shape, the `KECQ` quarantine wrapper, container
  codec version 2 (`ItemContainerPageCodec`), the `IInstanceIdStore`-backed instance id allocator and the
  thirteen-check instance validator. On top of the record: `PagedItemContainer` and `ItemContainerPage`, the
  paged container and its separate capacity gate, `InstanceStacking`'s byte-equality merge rule,
  `InstanceRemapPass` and `VettedRemapRules`, the registry-derived pass that brings one stored page forward,
  `ItemInstanceVisibility`, the one function answering whether a viewer may see a field, and the wire pair
  built on it, `ContainerPageDelta` and `ContainerPageSyncRequest`. Also references `Diagnostics`, for the
  validator's one injected log line.
- `KhaozEngine.Diagnostics` - logging (sinks, categories, crash hooks) + `FrameStats` telemetry.
- `KhaozEngine.Ecs` - struct-based archetype `World`/`Entity`/`ISystem` ECS with `ParallelForEach`.
- `KhaozEngine.Identity` - pluggable player-identity seam: provider sign-in + server-side verified-subject
  validation + HMAC session tokens, via the exchange model. OIDC and Discord providers are opt-in siblings.
- `KhaozEngine.Collision` - deterministic 2D collision + broadphase + walkable surfaces.
- `KhaozEngine.Physics` - the dependency-free 3D physics seam (`IPhysicsWorld`, shapes, queries).
- `KhaozEngine.Locomotion` - render-free character movement (`CharacterMovement.Step`, `MoveTuning`).
- `KhaozEngine.Terrain` - render-free analytic terrain field (height/normal/biome from `(x, z, seed)`).
- `KhaozEngine.MapDoc` - the zone/map document format: load/save/validate/migrate a versioned JSON model
  (terrain with parametric features, scatter and companion layers, exclusion/override shapes, authored
  placements, spawns, regions) plus `MapRuntime` builders that produce the exact `TerrainField`/
  `ScatterConfig`/`PropPlacement` objects games consume.
- `KhaozEngine.TileWorld` - the OSRS-style tile world document: 64x64 tile regions with stacked planes, a
  global tile-corner height lattice, ground/overlay materials and settings flags, tile-anchored objects and
  named markers, a hash-checked directory file form, catalogs, a validator, the derived per-edge collision
  map and its baker, a deterministic BFS pathfinder, a lattice raycast and prefabs.
- `KhaozEngine.Determinism` - `DeterministicFpScope` FP-environment pinning for lockstep sims.
- `KhaozEngine.Platform` - cross-platform `Clipboard` facade.
- `KhaozEngine.Updates` - delta auto-update pipeline.

The old Localization and Pooling packages were absorbed into `App` and `Primitives` in 9.0.0,
so they no longer appear as separate references.

Also ships buildTransitive build defaults that flow to every consumer, whether Foundation is
referenced directly or via `Game2D`/`Game3D`/`Server`:

- `CETCompat=false` (unless the head pins it): .NET 9+ marks the x64 apphost CET-compatible by
  default, which hard-aborts at boot on Windows 10 builds with partial shadow-stack support.
  A build-log message surfaces the default so it is not silent.
- `IncludeNativeLibrariesForSelfExtract=false` (unless pinned) when single-file publishing, so
  the GLFW, graphics-loader and OpenAL natives stay loose next to the apphost where the loader can find them
  (bundling them self-extracting breaks boot with "GlfwPlatform - not applicable").
- Linux only: the host rid's native assets (GLFW, OpenAL Soft, `libshaderc_shared`, `libspirv-cross`) are
  copied FLAT into the output directory on `build` and on a rid-agnostic `publish`. Silk.NET probes
  `runtimes/<distro rid>/native`, which nothing writes, so without this a Linux app cannot load any of them
  ([#722](https://github.com/APKiwiOrg/KhaozEngine/issues/722)). No-op on Windows and macOS, on
  `publish -r <rid>`, and in a project that references no native package. Opt out with
  `KhaozEngineFlattenHostNatives=false`. `KhaozEngine.Gpu`, `KhaozEngine.Windowing` and `KhaozEngine.Audio`
  pack the same rule file under their own names, so a project that references one of them without an umbrella
  gets it too ([#723](https://github.com/APKiwiOrg/KhaozEngine/issues/723)).

All three are overridable by setting the property in your game head.

Deliberately NOT included: networking (`KhaozEngine.Netcode*`), the render/windowing/audio
stack, and the opt-in `KhaozEngine.Physics.Bepu` backend (add it explicitly if you want a real
physics implementation behind the seam). For a game runtime use `KhaozEngine.Game2D`/`Game3D`,
for a headless server use `KhaozEngine.Server`.
