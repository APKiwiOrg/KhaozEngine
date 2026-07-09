# Map Editor Design (MapDoc + MapEditor + ke-mapedit)

Date: 2026-07-09
Status: Approved design, pre-implementation
First adopter: Ruinborne

## Problem

KhaozEngine games have no map data. The world is code. In Ruinborne, terrain is a pure
analytic function (`TerrainConfig` with seed, biome bands, and parametric features, built in
`Ruinborne.Core/RuinborneWorld.cs`), trees and rocks are deterministic coord-hash scatter
(`PropScatter.Generate`), the only hand-authored placements are literal C# arrays (7 town
buildings, 2 showcase rocks), and NPC spawns are rows in `dbo.npc_spawn` with code defaults.
Editing the world means editing C# and redeploying. There is no editor, no placement file
format, and no way for an AI to manipulate the world other than writing game code.

This design introduces the map artifact itself (an engine-owned world document format) plus
two frontends over it: an in-engine GUI editor for humans and an MCP server for AI-driven
editing. The engine-first rule applies: all of this is engine machinery usable by every
current and future game, with Ruinborne as the first adopter.

## Decisions made (with rationale)

1. **Terrain scope: placement-first, sculpt-ready.** V1 edits terrain at the config level
   (seed, water, biome bands, and features such as lakes, flatten discs, and rims as
   parametric objects) plus all placements. The document format reserves a terrain-override
   (sculpt delta) section so sculpting can land later as a format version bump, not a break.
   Full sculpting was rejected for v1: it would invent a new authored terrain representation
   and threaten the stateless-function determinism both heads rely on.
2. **GUI hosting: in-engine editor runtime, per-game head.** A `KhaozEngine.MapEditor`
   package that each game wraps in a tiny editor csproj so the editor runs with that game's
   assets and manifests. Rejected: an editor mode baked into a game client (couples tooling
   into the shipping client, generalizes poorly) and an external Avalonia/web app (foreign
   viewport embedding risk, abandons Gui dogfooding).
3. **Offline documents, not live-world editing.** The editor opens, previews, and saves
   documents on disk. Client and server load them at boot. Live reload and live world editing
   are explicit later increments. Rejected for v1: mutating a running MMO server requires
   physics rebuild and client re-sync machinery that is a project of its own.
4. **AI surface: a real MCP server with world queries.** File-level JSON editing would work
   but leaves the AI blind. The MCP tool exposes semantic mutations plus ground height,
   walkability, scatter preview, and headless PNG renders so the AI can see its edits.
5. **Scatter model: layered, with a bake-region tool in v1.** Procedural scatter stays the
   base layer. The document adds authored placements on top, plus exclusion zones and
   per-region scatter overrides. A bake tool freezes a chosen rect of scatter into authored
   placements (plus a covering exclusion) for hand-tuning. Baking everything was rejected:
   it balloons the document and kills the infinite-streaming property.
6. **Approach: engine-native stack, document core first.** Scored 42/50 against a
   game-first prototype (26/50) and an MCP-first sequencing (35/50) on reuse, time to first
   edit, risk, engine capability growth, and parallel-execution fit.

## Architecture

Three engine deliverables on the shared version line, one per-game head:

### KhaozEngine.MapDoc (GPU-free, joins the Foundation umbrella)

The document model: load, save, validate, mutate, migrate.

- Depends on: Primitives, Serialization, Content, Terrain.
- Terrain does NOT depend on MapDoc. The loader builds engine objects from document data
  (`TerrainConfig`, `TerrainField`, `ScatterConfig`s, placement lists), so the runtime stack
  never references document types it does not need.
- Ships the JSON schema as a package asset. Documents carry `$schema` and are validated by
  the existing `KhaozEngine.Content` build-time check plus an explicit load-time validation.
- Format versioning uses the `MigrationChain` pattern from `KhaozEngine.Persistence`.
- A registry lets games add custom feature and marker types (tagged-union discriminators
  mapped to game factories) without engine changes.

### KhaozEngine.MapEditor (opt-in package, in no umbrella)

The editor runtime library:

- Viewport host reusing `TerrainStreamer`, `Scene3DChunkSink`, and `PropLayer` so the editor
  preview is pixel-identical to the game.
- Editor cameras: a new fly cam (WASD, right-drag look, wheel speed), the existing orbit
  controller, and a top-down orthographic mode.
- Picking via the existing `ScreenToRay` plus terrain raymarch and prop AABB tests. No
  Bepu/physics dependency: the editor must not require native physics.
- Gizmos (XZ translate constrained to terrain, yaw ring, uniform scale) drawn through the
  `Scene3D` overlay path (`CollisionShapeOverlay` precedent).
- Undo/redo as a command stack over document mutations. Save validates before writing.
- Depends on: MapDoc, Gui, Terrain.Render3D, Render3D.

New generic widgets land in **KhaozEngine.Gui** itself, not in MapEditor: TreeView,
PropertyGrid (explicit property descriptors, no per-frame reflection), and a draggable
NumberField. Every game gets them.

### ke-mapedit (KhaozEngine.MapEdit.Tool, dotnet tool)

An MCP server over stdio using the official ModelContextProtocol C# SDK, following the
`ke-propbake` dev-tool packaging convention. Hosts the same MapDoc model. GPU is required
only for render verbs (via the Snapshot harness). Everything else runs headless.

### Per-game editor head

A thin csproj (for example `Ruinborne.Editor`) that assembles MapEditor with the game's
asset manifests, archetype ids, and custom document types, and opens the game's documents.

## Document format

One JSON file per zone (for example `assets/maps/valley.map.json`), human-diffable and
git-committed in the game repo. Sections:

- **Header**: format version int, zone id, display name, zone bounds.
- **Terrain**: seed, water level, biome bands, features as a tagged union, for example
  `{"type": "lake", "center": [34, -14], "radius": 22, "depth": 6}`. Custom feature types
  resolve through the MapDoc registry.
- **Scatter**: named procedural layers (config per layer), exclusion zones (disc, rect,
  polygon), and region overrides (density or kind-mix changes within a shape).
- **Placements**: authored props and buildings. Manifest kind id, x/z with ground-snapped y
  (explicit y override allowed), yaw, scale, stable id, free-form tags. Generalizes the
  hand-coded `TownBuilding` pattern. Also the output of the bake-region tool.
- **Spawns**: NPC spawn markers (archetype id, position, enabled, tags).
- **Regions**: named shapes with tags, interpreted by the game.
- **TerrainOverrides**: reserved. Schema-defined, rejected if present in v1. Future home of
  the sculpt delta layer.

Ground snapping happens deterministically against the generated field at load time, so both
heads agree by construction (one loader, one field).

## Runtime consumption

The loader produces exactly what games already consume:

- `TerrainField` from the terrain section.
- `ScatterConfig`s carrying the new exclusion/override shapes. This is the one substantive
  Terrain engine change: `ScatterConfig` today supports a single clearing disc, so
  `PropScatter` gains generalized exclusion and override shape lists, consulted per cell so
  determinism and chunk-order independence hold.
- An authored placement list feeding the existing instanced render path, `PropColliders`,
  and `PropSurfaces`.
- Spawn and region lists queryable by archetype or tag.

Error handling is asymmetric with runtime cell blobs on purpose: a map document is
dev-authored content, so an invalid or unmigratable document fails the boot loudly with a
precise error. No quarantine machinery.

Determinism guard: an engine test loads a document and asserts placement enumeration is
identical between chunked queries and one whole-zone query.

## GUI editor

Layout: full-window 3D viewport, left tool palette (select, place, terrain feature,
exclusion, spawn, region, bake modes, prop palette from the game's manifests), right
inspector (PropertyGrid on the selection), layers panel (toggle scatter layers, ground
cover, authored placements, spawns, regions, exclusions), status strip (cursor world
position, ground height, walkability).

Interactions:

- Click-pick with selection highlight. Place mode drops the selected kind ground-snapped,
  gizmo adjusts afterward.
- Terrain features edited as parametric objects: select the lake, drag its center, scroll
  its radius, field and streamed chunks regenerate live.
- Exclusions and regions: drag disc/rect, click-path for polygons.
- Spawn markers: place, pick archetype from a game-fed dropdown.
- Bake-region: drag a rect, scatter in it becomes authored placements plus a covering
  exclusion, selected for hand-tuning.
- Undo/redo on everything. Save validates first.

Testing per the engine's hard rule: document mutations, command stack, picking math, and
gizmo drag logic get headless tests driven by constructed `InputState` frames. Viewport
rendering is covered by GpuFact/snapshot infrastructure.

Localization: the editor is developer-only tooling and uses the documented raw-text escape
hatch, kept greppable.

## MCP tool surface

- **Document**: `map_open`, `map_create`, `map_save`, `map_validate`, `map_summary`.
- **Queries**: `ground_height(x, z)`, `is_walkable(x, z)`, `placements_in_rect`,
  `scatter_preview_in_rect`, `find_flat_area` (slope/size constrained search).
- **Mutations**: place/move/remove placements, add/edit terrain features, exclusions,
  region overrides, spawns, regions, `bake_region`. Every mutation ground-snaps, validates,
  and returns what changed.
- **Renders** (GPU): `render_topdown` (orthographic with placement/spawn/region overlay
  markers), `render_view` (perspective from position toward target). PNG results.

Documents are git-committed JSON, so the human review loop for AI edits is a git diff, and
the GUI picks up the same file. The two frontends share nothing but the document.

## Ruinborne adoption

1. One-time export of `RuinborneWorld.cs` (terrain config, scatter configs, buildings,
   showcase rocks, wolf spawn) into `assets/maps/valley.map.json`. A parity test asserts the
   document-loaded field and placements match the code-built ones exactly before the code
   path is deleted.
2. Both heads load the document through the MapDoc loader. `RuinborneWorld.cs` shrinks to
   game constants. Physics, streaming, and replication are untouched.
3. Spawns: the document becomes the authoring source. Mechanism: the deploy step seeds
   `dbo.npc_spawn` from the document (insert-if-absent, as `PostDeploy.sql` does today), the
   server keeps reading SQL when `RUINBORNE_SQL_CONNECTION` is set, and the no-SQL fallback
   switches from code defaults to document spawns. The enabled flag keeps working for
   live-ops. Full retirement of the table is a later decision.
4. `Ruinborne.Editor` head joins the solution. `docs/ADDING-PROPS.md` and
   `docs/architecture/WORLD.md` are rewritten around the document workflow.

## Phasing

Each phase is its own worktree and engine release per the release ritual. Subagent
execution uses Sonnet for mechanical and exploratory work and Opus for the format and
editor-runtime cores. Never Fable-tier subagents.

- **Phase A (blocking foundation)**: MapDoc package, Terrain exclusion/override support,
  loader, schema, determinism guard tests. One release.
- **Phase B (parallel after A)**: Gui widgets (TreeView, PropertyGrid, NumberField),
  MapEditor runtime, fly cam, gizmos. One or two releases.
- **Phase C (parallel after A, independent of B)**: ke-mapedit MCP tool. One release.
- **Phase D (game repo)**: Ruinborne export, both-heads loading, parity test (after A),
  editor head and doc rewrite (after B).

B and C share only the MapDoc contract from A, so they run concurrently in separate
worktrees. Doc sweep obligations per phase follow `CLAUDE.md` (README catalog, per-package
READMEs, USING-KHAOZENGINE, DEPENDENCY-SEAMS, CHANGELOG).

## Explicit non-goals (v1)

- Terrain sculpting and heightmap painting (format-reserved, not implemented).
- Live editing of a running server, and live document reload.
- Multi-user editing, editor collaboration.
- Asset import/bake tooling changes (ke-propbake and the Blender scripts are unchanged).
- Retiring the SQL spawn table.

## Open questions deferred to implementation planning

- Exact shape vocabulary for exclusions/overrides (disc and rect are certain, polygon cost
  is to be sized in Phase A).
- Whether `find_flat_area` ships in the first MCP release or follows.
- PropertyGrid descriptor API shape (explicit descriptors confirmed, exact fluent surface
  to be designed in Phase B).
