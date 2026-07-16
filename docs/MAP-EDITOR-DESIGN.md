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

**Resolved (textured-kits, T3)**: the editor viewport rendered every prop kit flat regardless of a manifest
entry's `"textured": true` flag. `MapEditorOptions.TexturedProps` (default true, matching gameplay) now gates
whether `ViewportWorld.LoadKitMeshes` loads a textured entry's multi-material parts (`PropLoader.LoadPropAuto`)
or its flattened single-part form, surfaced as a "Textured props" `BoolRow` in the Layers panel's new Rendering
section and threaded to `ke-mapedit`'s `render_topdown`/`render_view` as a `textured` parameter. Both the
toggle and a scatter-layer visibility toggle rebuild the streamed world (`RebuildWorldForVisibility`), since
both are read at prop-mesh load time, not live.

**Deferred out of that round**: `ViewportWorld.WithTextured` (the internal helper that overrides one entry's
`Textured` flag for a single load, when the toggle disagrees with the manifest) copies `AssetEntry` by calling
its full positional constructor. `AssetEntry` is a plain `readonly struct`, not a record, so it has no `with`
expression support. A future field added to `AssetEntry` needs a matching update to `WithTextured` or it
silently drops out of the copy (worse if the new parameter is optional-with-default, since the call still
compiles). Not fixed this round since `AssetEntry` is stable today - flagged so a future `AssetEntry` field
addition checks this call site.

**Resolved this round (editor-round8)**: per-element hide maintenance is now event-driven from a single
source. The reorder/remove/rename commands carry an `IVisibilityEffect` (`VisibilityOp`) that the new
`EditorDocument.CommandApplied`/`CommandUndone`/`CommandRedone` events drive, so the forward remap runs on
execute/redo and its inverse on undo (`EditorVisibility.RemapIndex`/`RemoveIndex`/`InsertIndex`/`RenameKey`).
Hides now survive undo and redo of a reorder or delete, and renaming a placement, spawn, player spawn, or
region moves its hide entry with the key (the old stale-key orphan is gone). The old inline call sites and
the `EditorToolController.OnIndexRemoved` callback are removed. One residual, by design: a deleted element's
OWN hide is dropped and not restored on undo (it was never part of the reversible document). Player spawns
gained their own `VisibilityGroup.PlayerSpawns` (labeled "Player spawns" in the Layers panel, gating both
the `DrawPlayerSpawnMarkers` draw and the pick, in lockstep with the per-element hide), so mass-hiding every
player spawn works like the other whole-class toggles. `ke-mapedit` gained the `exclusions_info` and
`scatter_overrides_info` read verbs (66 to 68), closing the exclusions read-back gap. Engine-misc
correctness: `RayMath` NaN origin/direction components now miss (the NaN comparisons used to fall through
every slab test as an always-pass hit) and the zero-length-ray edge is pinned by test, `TerrainRaycast`
throws `ArgumentOutOfRangeException` on a NaN step or maxDistance (was a silent NaN-poisoned miss) and its
stall guard is covered by test, `Scene3D.UnloadTexture` is a safe no-op on a stale handle after `Dispose()`
(the same guard `UnloadSplatMaterial` already had), and the three index-keyed remove commands
(`RemoveExclusionCommand`/`RemoveScatterOverrideCommand`/`RemoveFeatureCommand`) throw a precise
`ArgumentOutOfRangeException` on a bad index instead of whatever the raw list indexing surfaced. Editor UX
smalls: the status line truncates with an ellipsis instead of overflowing the strip
(`GuiDraw.TruncateWithEllipsis`, now public), a mode swap unfocuses the hidden filter, whitespace-only
filter edits no longer rebuild, the overlay draw list is a caller-owned reused buffer instead of a per-frame
allocation, the selected-overlay brighten clamps channels (`Color.ScaleRgbClamped`), the stale exit-chord
warning-text doc claim is corrected, and the feature-selection highlight, spawn id-gap reuse, and
polygon-row `Description` gaps are covered by tests.

**Also resolved this round (editor-round8, inspector widgets)**: the three inspector widgets' known gaps are closed. `NumberField`'s
numeric filter now validates against the buffer `TextEntry.Apply` is accumulating THIS call (not the stale
pre-frame field), so a multi-key frame or a paste admits at most one dot. Public `Value` is now a property
whose setter always clamps to `[Min, Max]`, so an out-of-range external value (e.g. a `FloatRow` polling a
document value) displays clamped instead of raw. Disabling a field mid-edit now cancels (buffer discarded,
value unchanged) rather than silently committing, and a sub-3px drag that scrubbed a real change no longer
falls through into typing mode on release. `TreeView` no longer freezes drop geometry while the wheel scrolls
mid-drag (`RowAt`/`RowBounds` resolve against the live `ScrollOffset`, recomputed the same frame right after
the wheel updates it), and the caret-zone-at-depth, `RowBounds`, and `VisibleRows` shared-list gaps are now
covered by tests (no behavior change). `PropertyGrid` clamps a partially-visible row's editor cell to `Bounds`
before running its `Update`, so a sliver already hidden by the Draw-time scissor can no longer claim a tap or
drag beyond it. `FloatRow.GestureEnded` (wired to `EditorDocument.SealGesture` through the new
`AddFloatRow` inspector helper) seals the undo gesture when a scrub or edit commit finishes, so scrubbing one
terrain field then a different one lands as two undo steps instead of coalescing through
`EditTerrainCommand.TryMerge`'s any-two-fields union (that command-level merge is unchanged and still correct
WITHIN one gesture). `TreeView`/`PropertyGrid` wheel scrolling is also now continuous
(`ScrollDelta * WheelSpeed`, the `ScrollablePanel` idiom) instead of rounding to an integer notch count, with
both exposing a `WheelSpeed` property for the same idiom. `ChoiceRow`'s overlay-clipping host contract was
reviewed and left as documented behavior, no code change.

**Deferred out of this round (round8)**: concave polygon overlays still self-overlap through the centroid
fan (carried from editor-round6, unrelated to this round's widget/visibility work). A richer per-kind kinds
editor for scatter-layer rules and companion `HostKinds`/`Kinds` (per-kind rows, drag-to-reweight, a picker
over known prop ids) is still future work, the crude comma-separated text row is unchanged this round.
Feature/exclusion/scatter-layer/companion-layer selection still keys off list index or name rather than a
stable synthetic id, the same v1 carve-out as before. Camera bookmarks still do not persist across an editor
close/reopen (session-only by design, carried from editor-round7). `ExtremeMaxDistanceToStepRatio_StillTerminates`
takes about 6 seconds to reproduce the float32 accumulation-stall path (it inherently needs roughly 2^24 march
iterations), accepted this round: a dedicated slow-test lane is only worth adding if this pattern recurs
elsewhere. This round's index-keyed remove-command guards (`RemoveExclusionCommand`, `RemoveScatterOverrideCommand`,
`RemoveFeatureCommand`, precise `ArgumentOutOfRangeException`) do not extend to the id- or name-keyed remove
commands (`RemovePlacementCommand`, `RemoveSpawnCommand`, `RemovePlayerSpawnCommand`, `RemoveScatterLayerCommand`,
`RemoveCompanionLayerCommand`, `RemoveRegionCommand`, `RemoveBiomeBandCommand`), which stay plain existence-checked
lookups (a generic `InvalidOperationException` from the `Find*` helper on a missing id or name), deliberately left
unguarded this round.

**Resolved this round (editor-round7)**: Shift+Escape used to pop the scene with no way back (a head that
pushed the editor as its only scene was left with a blank screen once the stack emptied), now goes through
`MapEditorOptions.RequestQuit` (invoked only when the editor is the bottom scene, otherwise the scene just
pops), with a per-game head wiring it to its own `GameApp.Quit()`. Saving used to be reachable only through
Ctrl+S/Cmd+S with no visible affordance, now the toolbar carries an always-visible Save button (`Save*` while
dirty) beside the existing chord. The old silent double-press discard (a first Shift+Escape armed a
status-strip warning, a second one discarded and exited, with no dialog and no visible list of the actual
choices) is replaced by a `PopupPanel`-based modal exit dialog offering Save and Close / Save / Discard /
Cancel when dirty, Close / Cancel when clean, blocking every other input while open. The engine also gained a
turn-key `MapEditorLandingScene` (New Map / Open Recent / Quit) a head pushes beneath the editor so Close
always has somewhere to land, a Ctrl+D duplicate across all nine selectable kinds (mirrored exactly by the
new `element_duplicate` MCP verb), and session-only camera bookmarks (Shift+1..9 store, bare 1..9 recall).

**Deferred out of this round**: camera bookmarks do not persist across an editor close/reopen (session-only
by design this round, a future round could ride the same `EditorRecentFiles`/`GameStorage` seam). Autosave
was proposed and explicitly declined by the user for this round (the exit dialog's Save/Save-and-Close cover
the discoverability gap without it). `MapEditorLandingScene` draws with its own fixed palette rather than
picking up `GuiStyle`/a host theme. The fly camera's aspect ratio still updates every frame even while the
exit dialog is open, so a resize mid-dialog is applied to a camera the dialog is currently blocking (cosmetic
only, the dialog itself is unaffected). An end-to-end pointer-tap test that drives a real tap against the
exit dialog's footer buttons (today's coverage drives the actions directly and through `HandleKeys`) is
optional follow-up. A registry-extensibility test for `element_duplicate`/`DuplicateSelection` against a
custom `MapDocRegistry` feature type is only worth adding if `MapEditSession` ever takes a custom registry
(it does not today). `EditorToolController.CloneShapeOffset` (all shape kinds, used by Duplicate) and
`ShapeGeometry.Translated` (disc/rect only, used by the gizmo drag) are two separate shape-translate
implementations that happen to agree on disc/rect today. Unifying them is future cleanup, not a bug: the
polygon case `ShapeGeometry.Translated` does not need to handle is why they are not just one function today.

**Resolved this round (editor-round6)**: the outline highlight used to orphan against a node no longer in
`Roots` on every document edit (`RebuildOutline` news up a fresh `TreeNode` set each time), now fixed by
`MapEditorScene.SyncOutlineSelection` re-resolving the selection via `TreeView.FindByTag` and `ScrollTo`
after every rebuild and every selection change, including mid-rename. The companion `HostKinds` empty list
used to silently mean "match no host" (a companion layer left with an empty list grew nothing, discovered
live in the `valley.map.json` understory/groundcover mismatch), now means "match every host" at the root
(`PropScatter.GenerateCompanions`), backed by a host-swap that clears a now-orphaned `HostKinds` in the same
undo step and a warning row for the populated-but-zero-intersection case. The inspector panel used to share
one 260-point `PanelWidth` with the outline, cramped for the newly-grouped companion/scatter-layer rows,
now split into `OutlinePanelWidth` (260) and a wider `InspectorPanelWidth` (340).

- **Format and schema (MapDoc)**: `MapTerrain` duplicates `TerrainConfig` defaults, a drift risk that
  is only partially parity-tested.
- **Ruinborne adoption**: document-to-SQL spawn seed automation. `PostDeploy.sql` still
  hand-carries the wolf values, currently in agreement with the document. Retained
  constants (lake, town, rim, play area) are still read directly by game code. Migrate
  those consumers to document reads in the editor era. Clearing-disc boundary semantics
  differ at exactly the radius, strict vs inclusive, a measure-zero case.
- **Gui widgets**:
  - `NumberField`: `Slider.Value` still bypasses its own clamp (a bare public field), unaffected by
    this round.
  - `TreeView`: every `TreeView` (outline, kit palette, spawn list, feature list) draws its selection
    fill through `GuiDraw.FillStyled` against `GuiStyle.Modern`, so every one of them picked up a rounded
    selection border. This is a visual-only change with no pixel-exact test coverage, verify by eyeball
    in a manual playtest rather than trusting the test suite alone.
  - `PropertyGrid`: `ChoiceRow`'s open option list draws in the grid's late overlay pass (above the rows
    below the selector, no longer overpainted), but still inside the grid's own scissor, so a long list
    clips at the grid bounds. A host needing the list to spill past the grid has to call
    `Dropdown.DrawOverlay` itself after the grid's `Draw`. `PropertyGrid.EditorStyle` pushes into every
    row's inner widget on every `Update`, so a single row cannot carry a style different from the grid's
    own: this is unreachable through the grid by design, not a bug, and a future per-row style override
    would need its own opt-out flag.
- **Editor UX**: sibling-focus drop after a rename, defer the pending re-select while any
  inspector row is focused. Default `PlaceKind` pre-arm changed to sorted-first. Concave polygon
  overlays self-overlap, the centroid fan.
  Custom `MapEditorScene` hosts must unsubscribe `DocumentChanged` and the three command
  events (`CommandApplied`/`CommandRedone`/`CommandUndone`) themselves (documented). `BakeRegion`'s two-arg overload has a doc
  nicety around its shadowed-discriminator caveat.
  Scatter-layer rule editing and companion `HostKinds`/`Kinds` editing are
  deliberately v1-crude (a carve-out taken at design time): a rule is a Biome choice plus a
  Density scalar plus a comma-separated `"id:weight"` text row parsed with the same
  `ParseKinds` convention `ke-mapedit` uses, rather than a per-kind row with its own weight
  editor. A richer kinds editor (per-kind rows, drag-to-reweight, a picker over the known
  prop ids) is future work once the crude text row proves cramped in practice. Feature,
  exclusion, scatter-layer, and companion-layer selection stays index- or name-keyed off the
  live document rather than a stable synthetic id: fine at v1 scale, but a bigger redesign
  (a persistent id independent of list position or display name) is deferred, the same
  design gap decision 1 already flagged for feature/exclusion naming. Scatter-layer rule
  add/remove/edit all go through the single whole-value `EditScatterLayerCommand` (clone the
  layer, mutate the clone, replace the whole value) rather than dedicated per-rule commands
  (`AddScatterRuleCommand`, and so on): simpler and consistent with the whole-value idiom
  used everywhere else in this program, and fine at the current few-rules-per-layer scale,
  but it does mean a rule add/remove undo step reverts the WHOLE layer value, not just the
  one rule, which would matter more if a layer ever grew a large rule list edited by
  multiple hands.
- **Engine misc**: Gizmo overlay builders compute normals the unlit pass never uses.

**Deferred out of this round (mapedit-perf)**: Coverage breadth deferred: an async flush-interleave
test for `TerrainStreamer.Invalidate`, a device-gated
kit-mesh/splat retention test that exercises a real `Rebuild`, a direct rim-remove null-`DirtyRegion`
test, a GPU-built `PartialRebuild` body test, and interleaved sticky-full-vs-throttle plus
interval-change-mid-gesture tests. Also: exclusion, scatter-layer, companion, and terrain-scalar edits
still take the full-rebuild path by design this round (the dirty-region seam exists so a later round
can narrow them), and ridge and rim features always fall back to full rebuild since their reach is
unbounded.

**Resolved (mapedit-playtest-fixes)**: DirtyRegion narrowing for exclusion and scatter-override shapes,
deferred by the scatter-overrides round below, now ships. `ShapeGeometry.TryBounds` (a disc/rect/polygon
AABB padded by `ShapeBoundsMargin`, 2 m) gives every exclusion and scatter-override command a bounded
dirty region: the two shape commands (`EditExclusionShapeCommand`/`EditScatterOverrideShapeCommand`)
union old and new shape bounds live (never cached, so a merged drag still covers the latest endpoint),
add/remove report the touched shape's bounds, the layers/values whole-value commands report their
(unchanged) shape's bounds, and `ReorderScatterOverrideCommand` unions every override shape across the
inclusive from..to index range (not just the two endpoints: a reorder also flips first-match-wins
precedence for every override sandwiched in between, so the whole range is the honest cheap cover).
Fixes the choppy-drag feel an exclusion or scatter-override gizmo drag used to have (every frame fell to
the throttled full-rebuild path, same as terrain scalars). Terrain scalars and biome bands are
unaffected and stay whole-doc (a biome band is bounded only in its elevation-range Z slice, a possible
future narrowing).

**Deferred out of this round (scatter-overrides)**: Exclusions were deliberately not added to the
Ctrl+Up/Down reorder chord: their masks combine
as a set union where order never changes which ground ends up excluded, so a reorder chord would be
meaningless for them (scatter overrides get the chord because their order is genuinely significant,
first-match-wins). Polygon override shapes remain MCP-authored and inspector-read-only, the same as
polygon exclusions and regions: the shape editor's kind selector has no polygon option, so a polygon
override can only be created over MCP and then shows as a read-only kind + point count row in the
inspector. There is a cosmetic JSON representation asymmetry between the two authoring paths: the GUI
always normalizes an empty kind list back to a null `Kinds` (no kinds means "all kinds"), while an MCP
call that passes an explicit empty kinds array stores `[]` instead, which is runtime-identical but not
byte-identical in the saved document. `scatter_override_edit` called with no optional arguments is a
dirty-marking no-op: it still marks the session dirty even though nothing on the override actually
changed, consistent with the other mutation verbs' behavior when called with no fields to change.

- **Program phases**: Sculpting via the reserved `terrainOverrides` delta layer. Live
  server editing and hot reload. Multi-user editing. Polygon click-path authoring gesture
  for exclusions and regions.
