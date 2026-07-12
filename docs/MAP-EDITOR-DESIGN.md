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
  loader, schema, determinism guard tests. Shipped in 10.44.0, see CHANGELOG.
- **Phase B (parallel after A)**: Gui widgets (TreeView, PropertyGrid, NumberField),
  MapEditor runtime, fly cam, gizmos. One or two releases.
  - **B1 (building blocks)**: the three Gui widgets (`NumberField`, `TreeView`,
    `PropertyGrid`), the editor fly camera (`FlyCamera3D`/`FlyCameraController`), and
    picking math (`RayMath.IntersectAabb`, `TerrainRaycast`). Shipped in 10.46.0, see CHANGELOG.
  - **B2 (editor runtime)**: the `MapEditor` runtime package itself, viewport host
    (`ViewportWorld`), gizmos, the undo/redo command stack (`EditorDocument`/`EditorHistory`
    with gesture coalescing, `EditorCommands`, `BakeRegionCommand`), the per-frame tool
    policy (`EditorToolController`), the turn-key `MapEditorScene`, and a
    `KhaozEngine.Showcase` demo room (`RoomMapEditor`) wiring it into a real game head as the
    manual verification handoff. Shipped in 10.50.0, see CHANGELOG.
  - **B2.1 (polish)**: kit-palette categories with a collapsible filterable tree (manifest `category`
    field, spawn-mode swap), one-shot draw and bake tools with an active-tool status hint, a visible
    water surface with an editable water level, and inline placement/spawn rename. Shipped in 10.52.0,
    with 10.53.0 second-playtest fixes (toolbar tracks one-shot tool returns, a host-reserved status
    footer via `StatusBottomOffset`, the Showcase outline post effect defaults off, and translucent
    exclusion/region/feature viewport overlays via `ShowOverlays`) following, 10.54.0 shape editing
    (per-parameter inspector rows and a disc/rect shape-kind selector for regions and exclusions,
    overlay picking that selects features from the viewport, translate and scale gizmos that move and
    resize shapes and features, the EditFeature placement tool, and a `ChoiceRow` dropdown inspector row
    with numpad typing) after, and 10.55.0 apply-order and visibility (feature apply-order controls that
    reorder the fold order with Ctrl+Up/Down, R that snaps a placement to the ground undoably, and a
    visibility system with a Layers panel and per-selectable Visible toggles) after that. See CHANGELOG.
- **Phase C (parallel after A, independent of B)**: ke-mapedit MCP tool, an MCP server over stdio
  exposing 39 document, query, mutation, and headless-render verbs over the same `MapDoc` model the
  GUI editor uses (`docs/USING-KHAOZENGINE.md` "ke-mapedit" section, `KhaozEngine.MapEdit.Tool`
  README). Shipped in 10.63.0, see CHANGELOG.
- **Phase D (game repo)**: Ruinborne export, both-heads loading, parity test (D1,
  Ruinborne 0.3.16), editor head and doc rewrite (D2, Ruinborne 0.6.2 era, engine 10.67.0).

B and C share only the MapDoc contract from A, so they run concurrently in separate
worktrees. Doc sweep obligations per phase follow `AGENTS.md` (README catalog, per-package
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
- PropertyGrid descriptor API shape (explicit descriptors confirmed, exact fluent surface
  to be designed in Phase B).

## Deferred follow-ups

Items surfaced by code review across the map-editor program's phases, kept here because
the per-worktree SDD ledger gets deleted at merge. A few overlap the non-goals and open
questions above, which predate implementation. This section is what later review
dispatches keep appending to. Same convention as the rest of the roadmap: delete an item
once it ships, the detail moves to `CHANGELOG.md`.

- **Format and schema (MapDoc)**: `MapTerrain` duplicates `TerrainConfig` defaults, a drift risk that
  is only partially parity-tested.
- **Ruinborne adoption**: document-to-SQL spawn seed automation. `PostDeploy.sql` still
  hand-carries the wolf values, currently in agreement with the document. Retained
  constants (lake, town, rim, play area) are still read directly by game code. Migrate
  those consumers to document reads in the editor era. Clearing-disc boundary semantics
  differ at exactly the radius, strict vs inclusive, a measure-zero case.
- **Gui widgets**:
  - `NumberField`: the numeric filter is frame-granular, so a multi-key frame or a paste
    can admit a second dot. Public `Value` bypasses the clamp, the same gap as `Slider`.
    Disable-mid-edit is untested. A sub-3px scrub also opens typing.
  - `TreeView`: the caret zone at depth above 0 is untested. `VisibleRows` returns the
    shared rebuilt list rather than a copy. `RowBounds` is never directly asserted.
    Wheel-scrolling the tree while a drag-and-drop reorder is armed is not supported: the
    drop geometry freezes at the scroll position the drag started at, so a long list needs
    the target row already on screen before the drag begins.
  - `PropertyGrid`: a partial row's `BlockRegion` slivers past the clip. Out-of-range
    external values display unclamped. Wheel feel diverges from the rest of the
    `ScrollablePanel` family. `FloatRow` needs a scrub-end gesture seal:
    cross-parameter scrubs currently coalesce into one undo step. `ChoiceRow`'s open
    option list now draws in the grid's late overlay pass (above the rows below the
    selector, no longer overpainted), but still inside the grid's own scissor, so a long
    list clips at the grid bounds. A host needing the list to spill past the grid has to
    call `Dropdown.DrawOverlay` itself after the grid's `Draw`.
  - `RenameRegionCommand` needs a `TryMerge`. Typed renames today land one undo step per
    keystroke.
- **Editor UX**: sibling-focus drop after a rename, defer the pending re-select while any
  inspector row is focused. Stale exit-chord warning text lingers after disarm. Status-line
  length can overflow the strip, no truncation yet. Default `PlaceKind` pre-arm changed to
  sorted-first. Stale inactive filter focus survives across a mode swap. Whitespace-only
  filter edits trigger a redundant rebuild. Concave polygon overlays self-overlap, the
  centroid fan. The overlay draw list allocates per frame. Selected-overlay brighten clamps
  channels, a slight hue shift. Feature-selection highlight lacks direct unit tests.
  Custom `MapEditorScene` hosts must unsubscribe `DocumentChanged` themselves (documented). `BakeRegion`'s two-arg overload has a doc
  nicety around its shadowed-discriminator caveat. Index-keyed hides (the feature and
  exclusion `Visible` rows key on list index) do not remap on a feature or exclusion
  reorder, whether via Ctrl+Up/Ctrl+Down or an outline drag-and-drop, or on a Delete, so
  the hidden flag can end up stuck on the wrong element once the list shifts under it.
  Renaming a placement, spawn, or region orphans its hide entry instead of following it:
  the `Visible` row polls the live post-rename key, so the renamed element shows again by
  default while the old-key entry lingers unreachable in `EditorVisibility`, a stale-key
  leak rather than a correctness bug. Scatter overrides (`MapScatterOverrideDoc`) have no
  editor surface at all: no palette entry to place one, no inspector rows to edit its
  shape/density/kind-mix, and no reorder command, unlike exclusions and terrain features.
  This one matters more than a typical missing-surface gap: override order is
  first-match-wins (document order), not a set union like exclusions, so once editing
  ships, reordering is gameplay-significant and not merely cosmetic.
- **Engine misc**: `RayMath`'s zero-length-ray edge is untested, and a NaN direction acts
  as an always-pass slab (garbage in, garbage out). `TerrainRaycast`'s NaN step is a silent
  miss, and its stall guard jumps to the endpoint at absurd ranges. Gizmo overlay builders
  compute normals the unlit pass never uses. `RemoveExclusionCommand`'s raw indexing throws
  the wrong exception type for a bad index. No partial chunk invalidation, wholesale
  viewport rebuild is the perf ceiling for large zones. Inspector-driven terrain edits lag
  the streamed world by one frame. `Scene3D.UnloadSplatMaterial` needs a guard against a cleared
  `_splatMaterials` list, so a `ViewportWorld` disposed after its owning `Scene3D` no longer throws
  `ArgumentOutOfRangeException`. Found by ke-mapedit's `RenderService`, which documents the workaround
  today: let the `Capture`-scoped scene own teardown instead of disposing the world explicitly.
- **Program phases**: Sculpting via the reserved `terrainOverrides` delta layer. Live
  server editing and hot reload. Multi-user editing. Polygon click-path authoring gesture
  for exclusions and regions.
