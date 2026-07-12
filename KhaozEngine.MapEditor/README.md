# KhaozEngine.MapEditor

Opt-in in-engine map editor runtime. A document-driven viewport over `KhaozEngine.MapDoc`, tool modes,
selection with gizmos, and an undo/redo command stack, wrapped in a turn-key scene a per-game head pushes.

This package is **not** bundled in any umbrella (the `KhaozEngine.Server.Admin` precedent): add it
explicitly to a game head that wants to edit its zone documents, so a shipping game never pulls the editor.

`KhaozEngine.MapEdit.Tool` (the `ke-mapedit` MCP server) is also a consumer of this package, via
`InternalsVisibleTo` rather than a game head: it reuses `MapEditorScene.ComputeOverlayDrawList`/
`OverlayDraw` so its `render_topdown` PNGs paint the same exclusion, region, and feature overlays the
GUI viewport does. See [`docs/MAP-EDITOR-DESIGN.md`](../docs/MAP-EDITOR-DESIGN.md).

## Quickstart

Fill `MapEditorOptions` and push a `MapEditorScene` directly onto your `SceneManager`, the same way every
other scene in the game gets pushed:

```csharp
var options = new MapEditorOptions
{
    DocumentPath = Path.Combine(AppContext.BaseDirectory, "assets", "maps", "valley.map.json"),
    ManifestPaths = new List<string>
    {
        Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json"),
        Path.Combine(AppContext.BaseDirectory, "assets", "buildings", "buildings.manifest.json"),
    },
    SpawnArchetypes = new List<string> { "wolf", "boar" },   // fills the spawn-tool dropdown
};
sceneManager.Push(new MapEditorScene().Init(scene, whiteTexture, dpiFont, options));
```

- `DocumentPath` is loaded on enter (a missing file starts a blank untitled document, see
  `MapEditorScene.CreateDocument`) and saved back to on Ctrl+S.
- `ManifestPaths` are the same `AssetManifest` files the game's own prop-kit loading reads, so the
  editor's palette and picking heights match what the game actually renders.
- `Registry` defaults to `MapDocRegistry.CreateDefault()`. Pass your own to add custom terrain feature
  types (see the `KhaozEngine.MapDoc` README).
- `SpawnArchetypes` seeds the spawn tool's dropdown. The editor never interprets the strings, it only
  stamps the chosen one onto a new `MapSpawn.ArchetypeId`.
- `ShowOverlays` (default true) draws the translucent viewport overlays for exclusions, regions, and
  terrain-feature markers (see Viewport overlays below). Set false to hide them.
- `StatusBottomOffset` (default 0) reserves that many points of clearance at the window bottom for a host
  that draws its own bottom chrome (the Showcase's F7-F10 display readout line), shifting the status strip
  and editor body up so the editor never stacks on the host's pixels.

**Push it directly, do not wrap it.** `MapEditorScene` already IS a complete `GameScene` + `IGameScene3D`
(its own `Init` chain mirrors any other room's Init-injection pattern). `GameScene.Manager` is set only by
`SceneManager.Push` (an internal setter inside `KhaozEngine.Game`), so a wrapper `GameScene` that builds a
`MapEditorScene` with `new` and forwards lifecycle calls to it by hand, instead of pushing it, leaves the
inner scene's `Manager` permanently null and its first `Manager!.Input` read throws. The scene also pops
itself off the stack on the Shift+Escape exit chord (see Keys below), so a host needs no wrapper to leave
the editor either. If your game needs extra room-level behaviour beyond the built-in keys, add a thin
**factory** function that builds the options and returns the pushed-ready `MapEditorScene`, and put the
extra key handling in the outer code that owns the `Push`/`Pop` call, not in a scene wrapping this one.
See `KhaozEngine.Showcase/RoomMapEditor.cs` for a worked example (`RoomMapEditor.Create` plus one
`Rooms.Add(("Map editor", () => RoomMapEditor.Create(...)))` line).

## Keys

Ctrl+Z undo, Ctrl+Shift+Z or Ctrl+Y redo, Ctrl+S save, Delete removes the current selection. R snaps the
selected placement to the ground (an undoable re-move with a null Y, a no-op when nothing placement-shaped
is selected or the placement is already grounded), suppressed while the rename field has focus so typing
an "R" into a name does not fire it. Ctrl+Up and Ctrl+Down reorder the selected terrain feature one step
earlier or later in the fold order (see Feature apply order below), clamped at the list ends so a boundary
press lands no command. Escape cancels an in-flight gizmo/draw gesture and returns to Select. Shift+Escape
exits the editor by popping the scene off the `SceneManager`: with no unsaved changes it pops immediately,
and with unsaved changes the first press arms a status-strip warning ("Shift+Escape again to discard and
exit") and a second Shift+Escape discards and exits. Any Ctrl+S or any document mutation (an edit, an undo,
a redo) disarms the warning, so a stale discard confirmation can never fire after the user resumed working.
The status strip leads with the active tool name and its `ModeHint` one-liner, then the undo/redo labels
and the exit chord as a standing hint. The toolbar tab bar is mirrored back onto the live controller mode
every frame, so a one-shot draw / bake tool returning to Select on completion (or an Escape cancel)
re-highlights the Select tab on its own, without a tap.

## Kit palette

The bottom-left panel is a filter box over a collapsible, category-grouped `TreeView`. A kit id's category is
`AssetEntry.Category` when the manifest declares one, else the declaring manifest's own file-name stem with a
trailing `.manifest` suffix stripped (`props.manifest.json` falls back to `props`), first-manifest-wins on a
duplicate id across manifests (`ViewportWorld.KindCategories`). `MapEditorScene.BuildPaletteSource` groups the
map once, on enter, into a twice-sorted source (categories ordinal, kit ids ordinal within each), and the live
tree rebuilds only when the filter box text changes, never every frame. Typing in the filter narrows leaves
case-insensitively, hides a category left with no matches, and forces every surviving category open. Clearing
the filter restores each category's remembered expand/collapse state instead of resetting it.

The panel is tool-scoped, hosting at most one of three pickers: the `PlacePlacement` tool shows the kit
palette, the `PlaceSpawn` tool swaps the same region to a flat, filtered spawn-archetype list instead (no
categories, since the archetypes are a flat game-supplied list from `MapEditorOptions.SpawnArchetypes`), and
the `EditFeature` tool swaps it again to a flat, unfiltered list of the registry's feature types
(`MapDocRegistry.FeatureTypes`, in registration order - no filter box, since the registered set is small and
static). Every other tool shows no panel at all and the outline reflows over the freed space, taking the
whole left column. Selecting a palette leaf sets `EditorToolController.PlaceKind`, selecting a spawn-list
leaf sets `SpawnArchetype`, selecting a feature-list leaf sets `PlaceFeatureType`, and tapping a category row
itself changes neither.

## Viewport overlays

Exclusions, regions, and terrain-feature markers are otherwise-invisible authoring shapes, so with
`MapEditorOptions.ShowOverlays` (default true) the viewport draws them as translucent ground fills:
exclusions red, regions blue, and a small amber marker disc at each terrain feature's center, with the
selected element brightened. They render through the `Scene3D` debug-fill pass, which runs depth-disabled
after post, so the overlays composite always-on-top of the terrain for authoring visibility rather than
depth-testing against it. `MapEditorScene.ComputeOverlayDrawList` is the pure, headless-tested doc-to-draw
step, and only the per-entry GPU submission lives in `DrawOverlays`. Set `ShowOverlays` false to hide the lot.

## Shape and feature editing

The exclusion and region inspectors show the shape as live-editable rows, not a read-only summary:
`MapEditorScene.AddShapeRows` adds a `ChoiceRow` (kind: `disc` / `rect`) plus one `FloatRow` per parameter
(disc gets CenterX/CenterZ/Radius, rect gets MinX/MinZ/MaxX/MaxZ), each writing a clone of the live
`MapShapeDoc` with one field changed through `EditExclusionShapeCommand`/`EditRegionShapeCommand`, whose
same-index/name `TryMerge` coalesces a scrub into one undo step. Switching the kind ChoiceRow converts the
shape center-preservingly (`MapEditorScene.ConvertShape`): a disc becomes the square of side `2r` around its
center, a rect becomes the disc centered on the rect with half its longer extent as the radius. A polygon
shape is read-only v1 (kind + point count, no conversion in or out). A kind conversion (or an undo/redo of
one) is caught by `MapEditorScene.SyncShapeInspector`, which rebuilds the inspector's row set the next chrome
step so disc rows swap to rect rows and back.

Exclusions, regions, and terrain features are also draggable through the transform gizmo once selected
(`EditorToolController.TryGizmoTarget`/`RestrictHandle`, shared geometry helpers `ShapeGeometry`/
`FeatureGeometry`): translate XZ moves the shape/feature center and the scale handle resizes its primary
radius (a lake or flatten's `Radius`, a rim's `InnerRadius`/`OuterRadius` scaled together, a ridge's
`Width`). There's no yaw ring, since none of these carry a yaw concept - only the placement gizmo draws and
honours it. The drag snapshots the pre-drag shape/feature on grab and rewrites it from that fixed start each
frame, routed through the same `Edit*ShapeCommand`/`EditFeatureCommand` merge as an inspector scrub, so a
whole drag coalesces into one undo step.

**Overlay picking.** In `Select` mode, a ray that hits only the terrain (no placement or spawn) falls back
to `OverlayPicking.Pick`, a containment test over the otherwise-invisible authoring shapes: a feature marker
(a disc of `OverlayPicking.FeatureMarkerRadius` at its center, matching the drawn overlay marker) beats an
exclusion beats a region, even when the point also lies inside a lower-priority shape, with a
nearest-shape-center tiebreak within one category for overlapping same-category shapes. This is what makes
exclusions, regions, and feature markers selectable with the mouse instead of only through the outline tree.

**Feature placement.** `EditorToolMode.EditFeature` click-places a default-parameterized feature of
`EditorToolController.PlaceFeatureType` (list-selected from the bottom-left panel, see Kit palette above) at
the terrain hit (`FeatureGeometry.CreateDefault`: a lake r10 d3, a flatten r10 target-height-at-click, a
ridge through the point, or a rim centered there), executes `AddFeatureCommand`, selects the new feature, and
one-shots back to `Select` so the next click picks it rather than placing another. A `PlaceFeatureType`
outside the registry's four built-ins has no editor default, so a click with such a type selected consumes
the click but places nothing. `Delete` on a selected feature routes through `RemoveFeatureCommand`.

## Feature apply order

Terrain features fold in list order: `MapRuntime.BuildField` runs each feature's height modifier on the
height the prior feature produced, so where two features cover the same ground the LAST one in the list
wins the overlap (a lake and a flatten over the same clearing, say). Reordering is how the author picks the
winner between overlapping features. Ctrl+Up and Ctrl+Down (`MapEditorScene.ReorderSelectedFeature`) move
the selected feature one step earlier or later through `ReorderFeatureCommand`, clamped at the list ends (a
press at a boundary lands no command). The move is its own inverse (`Revert` is `Apply` with the endpoints
swapped) and it never coalesces, so each reorder is its own undo step. The feature inspector's read-only
"Apply order N of M (last wins overlap)" row (`MapEditorScene.FeatureOrderText`) polls the feature's live
1-based fold position and the total count, so it tracks reorders and undo/redo live. Reordering is
`AffectsWorld` (it changes the folded terrain shape), triggering the same streamed-world rebuild as any
other terrain-feature edit (see Rebuild semantics below).

Undo/redo of a reorder does not re-follow the feature (v1): the selection is a bare index string, so an
undo leaves the selection on the same index, which may then address a different feature. Ctrl+Up/Ctrl+Down
itself re-selects the moved feature's new index, which is what keeps the selection glued to it during
ordinary use.

Dragging a feature row in the outline tree is the mouse-driven equivalent of Ctrl+Up/Ctrl+Down: the
`TreeView` drag-and-drop gesture (`OnReordered`, same-parent only) fires the same `ReorderFeatureCommand`
through `MapEditorScene.OnOutlineReordered` and re-selects the dropped index, exactly like the keyboard
path. Exclusion rows are also drag-reorderable in the outline, through `ReorderExclusionCommand`, but
exclusions combine as a pure set union (see `KhaozEngine.MapDoc`), so their list order never changes what
scatter is masked. It is `AffectsWorld` false, unlike the feature reorder, so dragging an exclusion row
never triggers a streamed-world rebuild. Every other outline category (Placements, Spawns, Regions,
Terrain) has no list-order semantics, so a drag attempted there is rejected as a no-op. Wheel-scrolling the
outline while a drag is armed is not yet supported: the drop geometry freezes at the scroll position the
drag started at.

## Visibility

`EditorVisibility` is editor-session view state, not part of the document: it gates whole
`VisibilityGroup`s (`Placements`, `Spawns`, `Water`, `Exclusions`, `Regions`, `FeatureMarkers`), named
scatter layers, and individual elements, and none of it is saved or undoable. A group or element toggle
writes straight to `EditorVisibility`, never through `EditorDocument.Execute`, so hiding something never
dirties the document (no leading `*` in the status strip) and never lands an undo step.

**Layers panel.** The empty-selection inspector (`MapEditorScene.BuildLayersInspector`) is the Layers
panel: one `BoolRow` per `VisibilityGroup` (raw dev-tool labels, `FeatureMarkers` reads "Feature markers"),
then one `BoolRow` per named scatter layer in the open document. A group toggle only gates draws and picks,
no rebuild. A scatter-layer toggle also calls `ViewportWorld.Rebuild` (`RebuildWorldForVisibility`), so a
hidden layer's props actually drop out of the streamed world, taking its companion layers with it (their
host is gone). The panel rebuilds on every selection change, so it always tracks the document's live
scatter-layer set. `Water` has no matching selection kind or per-element hide: its toggle turns the single
water-plane draw on or off outright.

**Per-element hide.** Every placement, spawn, feature, exclusion, and region inspector ends with a
"Visible" `BoolRow` (`MapEditorScene.AddVisibleRow`) bound to that one element's hidden flag, independent
of its group. A renamable element's row is polled through the same live-key closure the Name row uses
(`AddNameRow`'s returned getter), so it keeps toggling the right key across a rename.

**Hidden but still selectable in the outline.** A hidden element is neither drawn nor pickable from the
viewport: `EditorPicking.Pick` and `OverlayPicking.Pick` both take an optional visibility filter, wired to
`EditorToolController.IsVisible` (which the scene points at `EditorVisibility.IsElementVisible`), that skips
it. But it stays exactly where it was in the outline tree, since the outline is rebuilt straight from the
document and visibility never touches the document. Selecting it from the outline still opens its
inspector, Visible row included, so hiding something never blocks un-hiding it.

## The headless core

GPU-free and fully unit-tested:

- `EditorDocument` holds the open `MapDocument` plus editor state (dirty tracking, selection, world-rebuild
  signalling) and is the mutation choke point: every edit routes through `Execute`.
- `EditorHistory` is the engine's first undo/redo command stack, with gesture coalescing (a drag collapses
  to one undo step).
- `EditorCommands` are the reversible edits over the document model (placements, spawns, exclusions,
  regions, terrain features, terrain globals). Commands are the only mutation path, so undo is total by
  construction. `EditTerrainCommand` carries the terrain-wide globals (v1: the water level, scrub-coalesced).
  `ReorderFeatureCommand` moves a terrain feature within the list (see Feature apply order above). It never
  coalesces, so each reorder is its own undo step.
- `EditorVisibility` is the GPU-free, headless-tested editor-session view state described in Visibility
  above: visibility groups, scatter-layer toggles, and per-element hides. It never touches the document, so
  visibility carries no dirty flag and no undo/redo of its own.
- `BakeRegionCommand` freezes a scatter layer's procedural output over a rect region into authored
  placements (tagged `baked`, explicit Y) plus a covering exclusion, so a designer can hand-edit props that
  were procedural. It captures the generated placements on first apply and reuses them on redo, so an
  undo/redo cycle is byte-identical.
- `EditorToolController` is the GPU-free per-frame policy: it reads a plain `EditorFrameInput` (pick ray +
  pointer/keyboard edges) and emits commands. Select mode picks and drives the transform-gizmo drag
  (coalesced into one undo step, sealed on release). The place modes ground-snap a click into an Add. The
  draw modes rubber-band a disc (drag) or rect (shift-drag) into an exclusion or an auto-named region. The
  bake mode drags a rect on the ground and bakes the `BakeLayer` scatter over it. The three draw tools
  (draw-exclusion, draw-region, bake-region) are one shot: a completed gesture (the release that emits the
  command) falls back to Select automatically, so the next click picks the shape rather than starting another.
  An abandoned gesture (Escape, a mode switch, or a degenerate sub-threshold click that emits nothing) keeps
  the tool armed. `EditorToolController.ModeHint` returns a one-line description of the active tool, folding in
  `PlaceKind` / `SpawnArchetype`, which the scene renders in the status strip.

`MapEditorScene` is the turn-key scene a per-game head pushes: it wires the streamed `ViewportWorld`, a fly
camera, the `EditorToolController`, and the Gui chrome (toolbar tab bar, tree outline, property-grid
inspector, kit palette, status strip) with the undo/redo/save hotkeys. The GPU work sits behind build /
teardown / rebuild seams, so the lifecycle, update ordering, and save-failure handling stay
headless-testable. Developer-only tooling, so the editor UI is `LocalizationExempt`.

## The command stack and gesture sealing

Every mutation goes through `EditorDocument.Execute(IEditorCommand)`, which applies the command, pushes it
on `EditorHistory`, and raises `DocumentChanged`. A command that returns true from `TryMerge` on a
same-target follow-up (drag, scrub, successive rotate/scale) collapses into the command already on top of
the undo stack instead of pushing a second step, so a whole drag or a slider scrub is one undo, not one per
frame.

`EditorDocument.SealGesture()` (delegating to `EditorHistory.SealGesture()`) raises a merge barrier: the
NEXT `Execute` always starts a fresh undo step, even if it targets the same object as the command before
the seal. `MapEditorScene`/`EditorToolController` call it at every natural gesture boundary: pointer
release after a gizmo drag or a place/draw click, a tool-mode switch (`EditorToolController.Mode`'s setter
seals before changing tools), Escape (cancels the in-flight gesture and seals), and `MarkSaved` (a save is
always a gesture boundary, so a later same-gesture edit can never merge into the just-saved command and
hide itself from `IsDirty`). Undo and Redo also raise the barrier themselves, so an edit right after an
undo never re-merges into the command that was just reverted. Call `SealGesture()` yourself after any
custom multi-frame gesture you drive through `EditorDocument.Execute` directly, or a drag-like edit will
keep coalescing into later, unrelated edits of the same object.

## Rebuild semantics

`EditorCommand.AffectsWorld` (internal) classifies each command: terrain features (add, edit, remove, and
reorder via `ReorderFeatureCommand`), terrain globals (the water level, `EditTerrainCommand`), scatter
exclusions, and bake-region are `true` (they change terrain shape or scatter inputs). Placement/spawn/region
edits are `false` (they draw outside the streamed sink, so a drag never triggers a chunk rebuild).
`EditorDocument` sets `WorldRebuildPending` whenever an executed, undone, or redone command's `AffectsWorld`
is true.

A scatter-layer visibility toggle also rebuilds the streamed world, but through a separate path
(`MapEditorScene.RebuildWorldForVisibility` calling `ViewportWorld.Rebuild` directly), never through
`WorldRebuildPending`: visibility is view-only session state, not a document change, so it never touches the
command/dirty machinery above. See Visibility above.

The water level is `AffectsWorld` because scatter honours it (underwater candidates skip), so a change must
rebuild the streamed world. The water SURFACE itself is separate: `ViewportWorld.Draw` submits one
`Scene3D.DrawWater` plane per frame, derived live from the document bounds and `Terrain.WaterLevel`
(`ViewportWorld.BuildWaterPlane`), covering the whole zone. It is always submitted (no "skip when dry" guard):
the water pass is depth-tested against the terrain and its shore-fade drives the alpha to zero at the
waterline, so a level below all terrain renders nothing at negligible cost. Deriving the plane live means the
surface tracks a water-level edit immediately, ahead of the scatter rebuild. The terrain root in the outline
(a `SelectionKind.Terrain` node) selects into an inspector with the editable water level plus read-only seed
and biome count.

`MapEditorScene.OnUpdate` runs, in order, `UpdateCamera` -> `UpdateTools` -> `CheckWorldRebuild` ->
`UpdateChrome` -> `UpdateStreaming`. `CheckWorldRebuild` is what actually calls `ViewportWorld.Rebuild`
(tear down the sink + streamer + kit meshes, then rebuild wholesale from the document, since the engine has
no partial chunk invalidation) and `EditorDocument.AcknowledgeWorldRebuild()`.

**The one-frame `EditFeature` lag.** A terrain-feature parameter scrub lands through the `PropertyGrid`
inspector, which is polled inside `UpdateChrome` (after `CheckWorldRebuild` in the same frame's order). So
when the inspector's `FloatRow` setter calls `EditorDocument.Execute(new EditFeatureCommand(...))`,
`WorldRebuildPending` flips to true too late for that frame's `CheckWorldRebuild` call. The viewport
rebuild only happens on the FOLLOWING frame's `CheckWorldRebuild`. This is a one-frame visual lag, not a
correctness bug (the document itself is updated immediately, and a scrub coalesces every intermediate value
into one undo step regardless), but it means an automated test asserting "the streamed world reflects the
just-scrubbed radius" needs to step the scene one extra frame. Gizmo-driven edits (`UpdateTools`, which
runs BEFORE `CheckWorldRebuild`) do not have this lag: a feature or exclusion drag rewrites the document in
the same frame `CheckWorldRebuild` reads `AffectsWorld` from, so dragging a lake's radius (or an exclusion's)
rebuilds the streamed world that same frame, with no one-frame lag. Placement/spawn drags never trigger a
rebuild either way (`AffectsWorld` is always false, since they draw outside the streamed sink), and neither
do region drags (game-interpreted, also `AffectsWorld` false) - only the inspector-driven scrub path above
has the lag, and only for the `AffectsWorld`-true kinds (terrain features, exclusions, terrain globals).

## Bake-region

`EditorToolMode.BakeRegion` drags a rect on the ground. On release, `EditorToolController` executes a
`BakeRegionCommand(region, layer, registry)` for `EditorToolController.BakeLayer` (defaults to the
document's first scatter layer, null when the document has none, in which case the drag is a no-op). Like the
draw tools it is one shot: a committed bake returns the tool to Select.
`BakeRegionCommand.Apply` runs `PropScatter.Generate` against the document-built field and scatter config
for that layer over the rect, converts each generated placement to an authored `MapPlacement` (a
document-unique `baked-<layer>-N` id, the scatter kit id, the frozen ground Y so a later re-snap cannot
drift it, and a `baked` tag), and adds a matching rect exclusion scoped to that one layer so the frozen
props are never re-scattered on top of themselves. The generated placement list and exclusion are captured
on the FIRST `Apply` and replayed verbatim on every redo (never regenerated), so an Apply/Revert/Apply
cycle is byte-identical even though `PropScatter` is otherwise deterministic only against a fixed field.

## Renaming

The placement, spawn, and region inspectors lead with an inline-editable Name row (`MapEditorScene.AddNameRow`,
shared by all three). Typing a new id or name and moving focus away routes the edit through
`RenamePlacementCommand`, `RenameSpawnCommand`, or `RenameRegionCommand`, rejecting a blank, unchanged, or
colliding target before it touches the document, so a rejected rename lands no undo step. Placements and
spawns are keyed by id and regions by name, so a rename must move the selection to the new key. An immediate
`Selection.Set` mid-keystroke would rebuild the inspector and drop the row's focus, so the re-select is
deferred until the Name row itself loses focus. A different selection made first, an outline click or a
viewport pick while the row is still focused, wins over the stale pending re-select and drops it.

## `DocumentChanged` unsubscribe note for custom hosts

`MapEditorScene.OnEnter` subscribes to `EditorDocument.DocumentChanged` and `EditorSelection.Changed`
(rebuilding the outline / inspector), and `OnExit` unsubscribes both. If you build your own host around
`EditorDocument` directly (bypassing `MapEditorScene`, for example a headless batch tool or a custom
viewport), remember to unsubscribe any handler you attach to `DocumentChanged`/`Selection.Changed` yourself
when you tear the host down. `EditorDocument` outlives nothing on its own: a forgotten unsubscribe keeps
your handler (and whatever it closes over, such as a disposed `ViewportWorld`) reachable from the document
for as long as the document itself is referenced.
