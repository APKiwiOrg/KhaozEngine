# KhaozEngine.MapEditor

Opt-in in-engine map editor runtime. A document-driven viewport over `KhaozEngine.MapDoc`, tool modes,
selection with gizmos, and an undo/redo command stack, wrapped in a turn-key scene a per-game head pushes.

This package is **not** bundled in any umbrella (the `KhaozEngine.Server.Admin` precedent): add it
explicitly to a game head that wants to edit its zone documents, so a shipping game never pulls the editor.

`KhaozEngine.MapEdit.Tool` (the `ke-mapedit` MCP server) is also a consumer of this package, via
`InternalsVisibleTo` rather than a game head: it reuses `MapEditorScene.ComputeOverlayDrawList`/
`OverlayDraw` so its `render_topdown` PNGs paint the same exclusion, region, and feature overlays the
GUI viewport does. See [`docs/design/MAP-EDITOR-DESIGN.md`](../docs/design/MAP-EDITOR-DESIGN.md).

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
- `TexturedProps` (default true, matching gameplay) gates whether a manifest entry with `"textured": true`
  loads its multi-material textured parts (`PropLoader.LoadPropAuto`) or the flattened single-part form, in
  the viewport `ViewportWorld` renders. Read at prop-mesh load time, so flipping it (in code, or via the
  Layers panel's "Textured props" toggle below) triggers a viewport world rebuild rather than taking effect
  live. Session-level only, not persisted to the document.
- `StatusBottomOffset` (default 0) reserves that many points of clearance at the window bottom for a host
  that draws its own bottom chrome (the Showcase's F7-F10 display readout line), shifting the status strip
  and editor body up so the editor never stacks on the host's pixels.
- `GestureRebuildInterval` (default 0.25 seconds) throttles the expensive FULL viewport rebuild while a drag
  or draw gesture is live, so a fast mid-gesture edit stream does not re-mesh the whole streamed world every
  frame. 0 disables the throttle (rebuilds every frame). See Rebuild semantics below.
- `RequestQuit` (default null) is how the editor leaves when it is the bottom scene on the stack (nothing to
  pop back to): the head wires it to its own quit path (a `GameApp3D` subclass calling the protected
  `GameApp.Quit()`), since a scene never touches window APIs directly. Leave it null when the head always
  pushes the editor above a landing scene (see Landing scene and recent files below). A head that pushes the
  editor as its only scene should set it, or the exit dialog's Close leaves an empty stack (a blank screen).

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

Ctrl+Z undo, Ctrl+Shift+Z or Ctrl+Y redo, Ctrl+S save, Ctrl+D duplicates the current selection (see
Duplicate below), Ctrl+Shift+F freezes the whole zone's procedural scatter into placements (see Freeze
zone below), Delete removes the current selection. R snaps the selected placement to the ground (an
undoable re-move with a null Y, a no-op when nothing placement-shaped is selected or the placement is
already grounded). Ctrl+Up and Ctrl+Down reorder the selected terrain feature or scatter override one step
earlier or later in its list (see Feature apply order below), clamped at the list ends so a boundary press
lands no command. Bare 1..9 recalls a camera bookmark and Shift+1..9 stores one (see Camera bookmarks below). Escape
cancels an in-flight gizmo/draw gesture and returns to Select. Every Ctrl chord above also fires on Cmd
(Super): `InputState.IsCommandDown` treats the two as the same modifier, so the Windows/Linux chords work
unmodified on a Mac (Cmd+S and Cmd+D also suppress the fly camera for that one frame, since both chords
carry a WASD letter, see Camera bookmarks below for the Command-modifier suppression). All of them, plus the
bare R hotkey and the bookmark digits, are suppressed while an inspector field, the kit-palette filter, or
the spawn filter holds keyboard focus (`PropertyGrid.HasActiveEditor` ORed with the two filters' own
`IsFocused`, see `MapEditorScene.AnyEditorFocused`), so typing an "R" or a Ctrl-chord letter into a name or a
filter box types instead of firing the shortcut. A `NumberField` mid-edit is its own case: it cancels only
its own typed value on Escape, and the tool's Escape cancel is suppressed for that same press so the two
never double-fire, picking back up on the next press once the field releases focus. A focused `TextRow`,
`ChoiceRow`, or filter has no Escape handling of its own at all, so Escape is simply inert while one of those
holds focus, until a pointer action elsewhere moves focus away. Shift+Escape is the one chord never gated by
focus: it opens the exit dialog (see Exit dialog below) even from inside a focused field, since it is the
one chord a user needs reachable from there. While the dialog is open, every other editor chord, tool pick,
and camera step is suppressed until the dialog is dismissed.
The status strip leads with the active tool name and its `ModeHint` one-liner, then the undo/redo labels
and the exit chord as a standing hint. The toolbar tab bar is mirrored back onto the live controller mode
every frame, so a one-shot draw / bake tool returning to Select on completion (or an Escape cancel)
re-highlights the Select tab on its own, without a tap. A Save button sits at the right end of the toolbar
(after the tab bar), its label showing `Save*` while the document is dirty and plain `Save` once clean, as
an always-visible alternative to Ctrl+S/Cmd+S.

## Exit dialog

Shift+Escape opens a modal exit dialog (a scene-owned `PopupPanel` using its `FooterButtons`, see the
`KhaozEngine.Gui` README): a dirty document offers **Save and Close** (the default action, fires on Enter) /
**Save** / **Discard** / **Cancel**, a clean document just **Close** / **Cancel** (fires on Enter). Esc or
Cancel dismisses the dialog and returns to the editor with nothing changed. **Save** saves in place and
dismisses the dialog, staying in the editor, only when the save succeeds: a failure leaves the dialog open
with the error in the status strip and the unsaved work intact. **Save and Close** does the same save, then
leaves the editor (see below) only if that save succeeded, so a save failure never quits and never dismisses
the dialog. **Discard** / **Close** leave the editor with no save at all. While the dialog is open it blocks
every other editor chord, tool pick, and camera step (the `PopupPanel` scrim + pointer block), and its own
`HandleKeys` call routes Esc to Cancel and Enter to the default action.

Leaving the editor (`Save and Close` / `Discard` / `Close`) goes through `MapEditorOptions.RequestQuit`: when
the editor is the bottom scene on the stack (nothing beneath it to pop back to) and a quit action is wired,
that runs, otherwise the scene just pops (returning to whatever sits beneath it, for example the landing
scene, see below). A head that pushes `MapEditorScene` as its only scene must set `RequestQuit`, or Close
leaves an empty stack (a blank screen, since a scene never touches window APIs directly).

## Landing scene and recent files

`MapEditorLandingScene` is a turn-key entry menu a per-game head pushes as the BOTTOM scene on its
`SceneManager`, so an editor pushed on top of it later pops back to the menu instead of leaving the app with
nothing to show. It draws a title, a New Map row (a name field plus a Create button), an Open Recent list
(one button per recent path, most-recent first, a missing file's button greyed but still clickable so a tap
prunes it instead of erroring), and a Quit button. It is 2D-only chrome (no `IGameScene3D`) and runs
headless: every action (`TryCreateMap`/`CreateMapNamed`, `ActivateRecent`, `RequestQuitLanding`) is reachable
directly, with no live viewport required.

```csharp
var landingOptions = new MapEditorLandingOptions
{
    Title = LocalizedText.Raw("My Game Editor"),
    CreateMap = name => CreateMapDocument(name),         // head owns file IO, returns the new path or null
    OpenEditor = path => BuildMapEditorScene(path),       // head assembles a fully-initialized MapEditorScene
    Recent = new EditorRecentFiles("MyStudio", "MyGame"), // rides GameStorage under the publisher/app pair
    RequestQuit = () => app.Quit(),
};
sceneManager.Push(new MapEditorLandingScene().Init(whiteTexture, dpiFont, landingOptions));
```

- `CreateMap` (`Func<string, string?>`) receives the validated, trimmed name (non-empty, no path
  separators, the scene checks both before calling it) and returns the created document's path, or null on
  failure (the scene shows an inline note and pushes nothing). On success the scene touches the recent-files
  store and pushes the built editor.
- `OpenEditor` (`Func<string, GameScene>`) builds the fully-initialized `MapEditorScene` for a path (Scene3D,
  manifests, registry, all head concerns). The landing scene only `Push`es the result and stays underneath
  it, so the editor's own exit dialog pops back to this menu (see Exit dialog above).
- `Recent` (`IRecentFilesStore`, nullable) backs the Open Recent list. Null renders an empty list.
- `RequestQuit` mirrors `MapEditorOptions.RequestQuit`: null leaves the Quit button an inline no-op note
  instead of touching window APIs directly.

`IRecentFilesStore` is the seam (`Paths`, `Touch`, `Remove`, `Flush`): `Paths` is the most-recent-first list a
test or a fake can substitute, `Touch`/`Remove` mutate it (ordinal, case-sensitive dedup), and `Flush` drains
any coalesced pending write so the on-disk file is current before shutdown. `EditorRecentFiles` is the
canonical implementation, riding the engine's `ISettingsStorage` seam under its own `editor-recents.json`
file name (`EditorRecentFiles.FileName`, distinct from a game's own `settings.json`), capped at
`EditorRecentFiles.MaxPaths` (10) entries. Construct it either from an already-built `ISettingsStorage`
(the testable shape, optionally passing the `IPersistenceQueue` it writes through so `Flush` can drain it),
or with a `(publisher, appName)` pair, which builds and owns a `GameStorage` internally (following
`AppDataPaths`' `<os-base>/<publisher>/<appName>/` layout) so `Flush` always has a queue to drain. A head
that owns the `GameStorage` overload should call `Flush()` itself during its own quit/shutdown, since a scene
never touches persistence directly. `RecentFilesRecord` is the plain `Paths` list the JSON file round-trips.

The landing scene self-heals if the store changes while it is not the one driving the actions (for example a
future Save-As pushed from the editor scene on top): it re-diffs the store's live paths every driven frame
and rebuilds its button list on a mismatch, so the Open Recent list never goes stale.

## Duplicate

Ctrl+D (Cmd+D on a Mac) duplicates the current selection through `EditorToolController.DuplicateSelection()`,
mirroring the `DeleteSelection` dispatcher's shape across all ten selectable kinds (placement, spawn, player
spawn, feature, exclusion, scatter override, region, biome band, scatter layer, companion layer). Each
duplicate is a deep clone with a fresh, unique identity, added through that kind's own `Add` command and
immediately sealed as its own undo step, then selected: a placement, spawn, or player spawn gets a fresh id
off the same `placement-N` / `spawn-N` / `player-N` generated-name scheme a freshly placed element already
uses, not a name derived from the source id. A named feature, exclusion, or scatter override gets a
uniquified `<name>-copy`/`-copy-2` suffix while an unnamed one stays unnamed (index-keyed, no collision to
dodge). A region always takes a fresh generated name, the same as a freshly drawn one, and a scatter or
companion layer gets `<name>-copy` uniquified the same way. Every kind that carries a position (placement,
spawn, player spawn, feature, exclusion, scatter override, region) offsets its clone by +2/+2 world units on
X/Z so it never lands exactly on top of its source. A biome band, a scatter layer, and a companion layer
have no position: a biome band clones verbatim (it has no name either), while a scatter or companion layer
still takes the uniquified `<name>-copy` name described above.

`DuplicateSelection()` returns a `DuplicateResult?` (the duplicated kind plus its fresh id/name/index), or
null when nothing was duplicated: an empty selection, the Terrain singleton (nothing to clone), or a custom
feature type `FeatureGeometry.Translated` does not know how to offset. Both no-op cases stay silent in the
controller itself, the same as `DeleteSelection`'s own default branch. `MapEditorScene`'s Ctrl+D handler is
what surfaces a status-strip note for the two skip cases ("Nothing to duplicate: Terrain is the document
singleton." / "Cannot duplicate this feature type."), telling a real skip apart from an ordinary empty
selection. `KhaozEngine.MapEdit.Tool`'s `element_duplicate` MCP verb reuses this exact same clone and
unique-identity logic (see the `ke-mapedit` README), so a GUI-driven and an MCP-driven duplicate can never
drift apart.

## Camera bookmarks

Shift+1 through Shift+9 stores the fly camera's current pose (`Position`/`Yaw`/`Pitch`) into that numbered
slot, overwriting whatever was there. A bare 1 through 9 recalls a previously stored slot, snapping the
camera straight back to it. Both are session-only (nothing persists across an editor close/reopen this
round, see the design ledger for the deferred persistence follow-up), and the status strip confirms every
store/recall, or reports an empty slot when a bare digit hits a slot never stored this session. Both are
gated below `MapEditorScene.AnyEditorFocused` like every other chord, so typing a digit into a name or filter
field never fires a bookmark. Cmd+S and Cmd+D carry a WASD letter (S and D), so the fly camera's own
`_camController.Update` is skipped for any frame `InputState.IsCommandDown` is true, keeping those chords
from also nudging the view one frame. The camera's aspect-ratio upkeep still runs every frame regardless, so
a resize during a held modifier is never missed.

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
itself changes neither. The spawn-archetype list carries one extra, pinned leaf above every archetype: "player
spawn". Selecting it sets `EditorToolController.PlacingPlayerSpawn` instead of an archetype, so the next click
places a player start rather than an NPC spawn (see Player spawns below).

## Player spawns

A player spawn (`MapPlayerSpawn`) is a stable-id, position-plus-yaw start marker with no archetype: unlike an
NPC spawn, which game code interprets by `ArchetypeId`, which start a game actually uses at runtime is game
code's own concern, so the editor only authors the marker. `ViewportWorld.DrawPlayerSpawnMarkers` draws each
one as a green marker disc (enabled) or the same grey a disabled NPC spawn uses (disabled), the one color
distinguishing it from an NPC spawn's blue at a glance. It picks and drags exactly like an NPC spawn: the same
fixed 1.5-tall, 1.0-wide pick box (`EditorPicking`), the same `GizmoAffordance.Marker` translate-XZ gizmo
(rotate/scale are not offered, matching NPC spawns), and the same body-drag-past-threshold path any gizmo
target gets.

Placing one goes through the pinned "player spawn" palette leaf above (Kit palette): a click in `PlaceSpawn`
mode with it selected auto-names the new spawn `player-N` (the smallest unused suffix) and executes
`AddPlayerSpawnCommand`, which absorbs an immediately-following same-id `MovePlayerSpawnCommand` (`TryMerge`)
so a place-then-drag-into-position gesture is one undo step. The outline's "Player Spawns" category
(`RebuildOutline`) lists one node per spawn, labeled with the id and a trailing "(disabled)" while off,
mirroring how the NPC "Spawns" category labels its nodes with the archetype. The inspector
(`BuildPlayerSpawnInspector`) groups Identity (an inline-rename Name row, `RenamePlayerSpawnCommand`,
rejecting a blank, unchanged, or colliding id before it touches the document) and Transform (X/Z through
`MovePlayerSpawnCommand`, Yaw in raw radians through `SetPlayerSpawnYawCommand`, both same-id merges
coalescing a scrub into one undo step) and State (an Enabled `BoolRow` through
`SetPlayerSpawnEnabledCommand`, plus the standard Visible row). Delete routes through
`RemovePlayerSpawnCommand`, restoring the removed spawn at its original list index on undo. Player spawn ids
are unique only within the `playerSpawns` section (an NPC spawn and a player spawn may share the same string
id with no collision), per `KhaozEngine.MapDoc`.

## Inspector look and feel

Every inspector row now carries a real hover tooltip: each `MapEditorScene.Add*Row` helper passes a
`description` (`LocalizedText`) explaining what the field does (terrain scalars describe their visual effect,
biome band fields explain the open-edge toggle, scatter/companion fields explain the weighted-kind syntax
and the `HostKinds` empty-means-all rule, exclusion/region rows explain targeting, and so on). The scene
builds one lazy `Tooltip` (`DrawInspectorTooltip`, the `PatchNotesView` precedent: built after a `UiViewport`
exists, since `Tooltip`'s fonts are fixed at construction and must track a DPI rebake) and draws it after the
chrome pass, escaping the grid's own scissor, anchored to `PropertyGrid.RowLabelBounds` of the currently
`HoveredRow`, showing immediately with no delay.

Every inspector is broken into named `HeaderRow` groups instead of one flat row list: Terrain gets Water /
Noise / World, a biome band gets Range / Shape, a scatter layer gets Identity / Placement / Scale / Rules, a
companion layer gets Identity / Host / Output / Shape, an exclusion or region gets Identity / Shape (plus
Targeting for exclusions), a scatter override gets Identity / State / Shape / Scatter (a DensityMultiplier
scalar and a Kinds substitution list, the same "id:weight" text convention as the scatter-layer rule rows,
empty Kinds meaning null) / Layers (the same All-layers + per-layer targeting rows the exclusion uses),
and a placement / spawn / player spawn gets Identity / Transform / State. The
empty-selection Layers panel groups its own rows the same way (Groups, then Scatter Layers). The shape editor
(`AddShapeRows`, shared by exclusions, scatter overrides, regions, and terrain features) sits under a "Shape" group header but
its own disc/rect selector row is labeled "Kind", not "Shape" - the group name and the row label are
deliberately distinct so neither reads as a duplicate of the other.

The editor now runs `GuiStyle.Modern` throughout: `PropertyGrid.EditorStyle` and every `TreeView.Style`
(outline, kit palette, spawn list, feature list) are constructed with it, so every inspector row's inner
widget and every tree's selection highlight pick up its rounded corners, gradient, and glow. The toolbar,
outline, inspector, and status-strip chrome panels draw through `SpriteBatch.DrawRounded` at
`GuiStyle.Modern.CornerRadius` instead of a flat fill, against a slightly lifted dark palette (colors are not
pinned exact in tests, only that they stay dark and the rounded-fill call shape is used). The inspector
column is also noticeably wider: `MapEditorScene.ComputeLayout` splits the old single `PanelWidth` into
`OutlinePanelWidth` (260 points, left column: outline + kit palette, unchanged) and `InspectorPanelWidth`
(340 points, right column, up from 260), flush against the window's right edge independent of the outline
width, giving the wider companion/scatter-layer rows and their group headers room to breathe.

## Viewport overlays

Exclusions, scatter overrides, regions, and terrain-feature markers are otherwise-invisible authoring
shapes, so with `MapEditorOptions.ShowOverlays` (default true) the viewport draws them as translucent
ground fills: exclusions red, scatter overrides orange, regions blue, and a small amber marker disc at
each terrain feature's center, with the selected element brightened. They render through the `Scene3D`
debug-fill pass, which runs depth-disabled after post, so the overlays composite always-on-top of the
terrain for authoring visibility rather than depth-testing against it. `MapEditorScene.ComputeOverlayDrawList`
is the pure, headless-tested doc-to-draw step, and only the per-entry GPU submission lives in `DrawOverlays`.
Set `ShowOverlays` false to hide the lot.

## Shape and feature editing

The exclusion, scatter override, and region inspectors show the shape as live-editable rows, not a
read-only summary: `MapEditorScene.AddShapeRows` adds a `ChoiceRow` (kind: `disc` / `rect`) plus one
`FloatRow` per parameter (disc gets CenterX/CenterZ/Radius, rect gets MinX/MinZ/MaxX/MaxZ), each writing a
clone of the live `MapShapeDoc` with one field changed through
`EditExclusionShapeCommand`/`EditScatterOverrideShapeCommand`/`EditRegionShapeCommand`, whose
same-index/name `TryMerge` coalesces a scrub into one undo step. Switching the kind ChoiceRow converts the
shape center-preservingly (`MapEditorScene.ConvertShape`): a disc becomes the square of side `2r` around its
center, a rect becomes the disc centered on the rect with half its longer extent as the radius. A polygon
shape is read-only v1 (kind + point count, no conversion in or out). A kind conversion (or an undo/redo of
one) is caught by `MapEditorScene.SyncShapeInspector`, which rebuilds the inspector's row set the next chrome
step so disc rows swap to rect rows and back.

Exclusions, scatter overrides, regions, and terrain features are also draggable through the transform gizmo
once selected (`EditorToolController.TryGizmoTarget`/`RestrictHandle`, shared geometry helpers
`ShapeGeometry`/`FeatureGeometry`): translate XZ moves the shape/feature center and the scale handle
resizes its primary radius (a lake or flatten's `Radius`, a rim's `InnerRadius`/`OuterRadius` scaled
together, a ridge's `Width`). A ridge or a rim with at least one pass also draws a yaw ring
(`GizmoAffordance.MoveScaleRotate`): dragging it rotates the ridge's stored direction, or offsets every one
of the rim's pass angles together by the same delta, tracking the cursor around the ring the same way the
placement gizmo's ring does. A lake, a flatten, a passless rim, and the disc/rect shapes (exclusion, scatter
override, or region) carry no orientation to show, so they keep the plain translate XZ + scale handle set
with no ring at all, and none of these draw the placement gizmo's unusable +Y arrow either way. The drag
snapshots the pre-drag shape/feature on grab and rewrites it from that fixed start each frame, routed
through the same `Edit*ShapeCommand`/`EditFeatureCommand` merge as an inspector scrub, so a whole drag
coalesces into one undo step.

Any selected gizmo target also arms a body drag, not just placements and spawns: pressing anywhere on the
object itself, away from every gizmo handle, and moving the pointer past
`EditorToolController.BodyDragThreshold` (6 screen-space units, matching the outline tree's own row-drag
threshold) drags it in XZ through the same translate-XZ path the gizmo's arrows use, starting from the
press-time ground point (a placement or spawn moves via `MovePlacementCommand`/`MoveSpawnCommand`, a feature,
exclusion, scatter override, or region translates through the same command its gizmo arrows already use). A
release below the threshold is a plain tap: the selection the press already made stands, and no history
entry lands. The gizmo handles themselves are unaffected. A press that lands on a handle still grabs that
handle exactly as before.

**Overlay picking.** In `Select` mode, a ray that hits only the terrain (no placement or spawn) falls back
to `OverlayPicking.Pick`, a containment test over the otherwise-invisible authoring shapes: a feature marker
(a disc of `OverlayPicking.FeatureMarkerRadius` at its center, matching the drawn overlay marker) beats an
exclusion beats a scatter override beats a region, even when the point also lies inside a lower-priority
shape, with a nearest-shape-center tiebreak within one category for overlapping same-category shapes. The
scatter override sits between exclusion and region because it is rarer and usually larger than an
exclusion (so the more specific exclusion wins where they overlap) yet more specific than a broad gameplay
region (so the override wins over the region it sits inside). This is what makes exclusions, scatter
overrides, regions, and feature markers selectable with the mouse instead of only through the outline tree.

**Feature placement.** `EditorToolMode.EditFeature` click-places a default-parameterized feature of
`EditorToolController.PlaceFeatureType` (list-selected from the bottom-left panel, see Kit palette above) at
the terrain hit (`FeatureGeometry.CreateDefault`: a lake r10 d3, a flatten r10 target-height-at-click, a
ridge through the point, or a rim centered there), executes `AddFeatureCommand`, selects the new feature, and
one-shots back to `Select` so the next click picks it rather than placing another. A `PlaceFeatureType`
outside the registry's four built-ins has no editor default, so a click with such a type selected consumes
the click but places nothing. `Delete` on a selected feature routes through `RemoveFeatureCommand`.

## Terrain sculpt

`EditorToolMode.SculptTerrain` (the Sculpt toolbar tab) sculpts the document's authored height deltas
(`MapDocument.TerrainOverrides`, the sculpt layer added in map format v2) with a brush. A press-drag-release
stroke is one undo step: each frame's dab picks the terrain point under the cursor
(`EditorPicking.PickTerrain`, the same ground raycast the place tools use) and applies the brush, and the dabs
coalesce into a single `TerrainSculptStrokeCommand` via `TryMerge`, exactly like the transform-gizmo drag.

The inspector shows the brush parameters while the tool is active (`BuildSculptInspector`), editing the
controller directly (they are tool settings, not document edits, so they carry no undo gesture):

- **Brush** (`EditorToolController.Brush`, a `SculptBrush`): `Raise` / `Lower` add or remove height, `Smooth`
  blends each delta toward its 3x3 neighbourhood mean (over the delta field, so it softens sculpted features
  without fighting the procedural base), `Flatten` blends the surface toward the height under the first press,
  `SetHeight` blends toward the **Set height** value.
- **Radius** (`BrushRadius`, world units) is the disc footprint; the falloff (`TerrainSculptBrush.Falloff`, a
  smoothstep) is 1 at the centre and eases to 0 at the rim.
- **Strength** (`BrushStrength`) is meters per stroke-second for raise/lower and a per-second blend rate for the
  toward-a-target brushes, so a stroke builds up while held and is deterministic given its dab sequence.

The brush math (`TerrainSculptBrush.ComputeDab`) and the stroke command are GPU-free and headless-tested. The
footprint is clamped (`SculptBounds`) to the cells of tiles that lie wholly within the document bounds, so the
brush never creates a tile the validating writer refuses (a consequence is a dead strip up to one tile wide on
a non-tile-aligned edge). The stroke command reports a bounded `DirtyRegion` over its footprint, so the
viewport re-meshes only the chunks the stroke touched (`ViewportWorld.PartialRebuild`) rather than the whole
world. Undo restores every touched tile exactly, removing tiles the stroke created and dropping a layer it
created back to null. The `sculpt_*` MCP verbs are T3, not in this release.

## Feature apply order

Terrain features fold in list order: `MapRuntime.BuildField` runs each feature's height modifier on the
height the prior feature produced, so where two features cover the same ground the LAST one in the list
wins the overlap (a lake and a flatten over the same clearing, say). Reordering is how the author picks the
winner between overlapping features. Ctrl+Up and Ctrl+Down (`MapEditorScene.ReorderSelectedElement`) move
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
never triggers a streamed-world rebuild. Scatter override rows reorder on both paths as well, but their
order genuinely matters: the FIRST matching override wins a patch of ground, so `ReorderSelectedElement`
dispatches a selected override to `ReorderScatterOverrideCommand` (Ctrl+Up/Ctrl+Down), the outline drag
fires the same command through `OnOutlineReordered`, and both are `AffectsWorld` true (a reorder rebuilds
the streamed world). Exclusions are deliberately left off the Ctrl+Up/Ctrl+Down chord for that reason:
their order is meaningless, so only the drag exposes it (for a stable authored layout). Only Feature,
Exclusion, and ScatterOverride rows can arm a drag at all: `MapEditorScene.OutlineNodeIsReorderable` is
wired as the outline's `TreeView.CanReorder`, so every other row (Placements, Spawns, Regions, Terrain,
Biomes, Scatter Layers, Companion Layers, and the category headers and add-actions) is rejected at the
press-origin gate before it grabs, showing no insertion line and firing no `OnReordered` at all, rather
than arming and being rejected as a no-op after the drop. `OnOutlineReordered`'s own kind check stays as
the safety net behind the gate. Wheel-scrolling the outline while a drag is armed is supported: the drop
geometry resolves against the live scroll position each frame, so a long outline can scroll mid-drag
instead of freezing.

A per-element hide follows the moved, deleted, or renamed element automatically, driven by an
`IVisibilityEffect` the reorder, remove, and rename commands carry (`VisibilityOp` describing a
reorder / remove / rename). `EditorDocument` raises `CommandApplied` / `CommandRedone` / `CommandUndone`
around each mutation (before `DocumentChanged`), and `MapEditorScene` subscribes once: a forward op runs
`RemapIndex` / `RemoveIndex` / `RenameKey` on execute and redo, and its inverse (`RemapIndex` with swapped
endpoints, `InsertIndex`, `RenameKey` with swapped keys) runs on undo. So a hide survives undo and redo of a
reorder, delete, or rename, and a rename moves the hide with the key leaving no orphan under the old one.
This is the single source of hide maintenance: the reorder / delete call sites no longer remap inline (the
old `EditorToolController.OnIndexRemoved` callback is gone), so a single reorder remaps exactly once. The one
residual: a deleted element's own hide is dropped and not restored on undo (it was never part of the
reversible document), an accepted view-only limit.

## Visibility

`EditorVisibility` is editor-session view state, not part of the document: it gates whole
`VisibilityGroup`s (`Placements`, `Spawns`, `Water`, `Exclusions`, `ScatterOverrides`, `Regions`,
`FeatureMarkers`, `PlayerSpawns`), named scatter layers, and individual elements, and none of it is saved or undoable. A group or element toggle
writes straight to `EditorVisibility`, never through `EditorDocument.Execute`, so hiding something never
dirties the document (no leading `*` in the status strip) and never lands an undo step.

**Layers panel.** The empty-selection inspector (`MapEditorScene.BuildLayersInspector`) is the Layers
panel: one `BoolRow` per `VisibilityGroup` (raw dev-tool labels, `FeatureMarkers` reads "Feature markers",
`ScatterOverrides` reads "Scatter overrides", `PlayerSpawns` reads "Player spawns"),
a **Rendering** section holding the "Textured props" `BoolRow` bound to `MapEditorOptions.TexturedProps`,
then one `BoolRow` per named scatter layer in the open document. A group toggle only gates draws and picks,
no rebuild. The "Textured props" toggle and a scatter-layer toggle both also call `ViewportWorld.Rebuild`
(`RebuildWorldForVisibility`), since both are read at prop-mesh load time rather than live: flipping
"Textured props" reloads every manifest entry's mesh through `PropLoader.LoadPropAuto` under the new
setting, and a hidden scatter layer's props actually drop out of the streamed world, taking its companion
layers with it (their host is gone). Because `Rebuild` now retains the cached kit meshes and splat material
by default (see Rebuild semantics below), the "Textured props" toggle calls `ViewportWorld.InvalidateKitMeshes`
first (`MapEditorScene.InvalidateViewportKitMeshes`) so the follow-up rebuild reloads every mesh in its new
form instead of serving the stale cached one. The panel rebuilds on every selection change, so it always
tracks the document's live scatter-layer set. `Water` has no matching selection kind or per-element hide:
its toggle turns the single water-plane draw on or off outright.

**Per-element hide.** Every placement, spawn, feature, exclusion, scatter override, and region inspector
carries a "Visible" `BoolRow` (`MapEditorScene.AddVisibleRow`) bound to that one element's hidden flag, independent
of its group. A renamable element's row is polled through the same live-key closure the Name row uses
(`AddNameRow`'s returned getter), so it keeps toggling the right key across a rename.

**Hidden but still selectable in the outline.** A hidden element is neither drawn nor pickable from the
viewport: `EditorPicking.Pick` and `OverlayPicking.Pick` both take an optional visibility filter, wired to
`EditorToolController.IsVisible` (which the scene points at `EditorVisibility.IsElementVisible`), that skips
it. But it stays exactly where it was in the outline tree, since the outline is rebuilt straight from the
document and visibility never touches the document. Selecting it from the outline still opens its
inspector, Visible row included, so hiding something never blocks un-hiding it.

## Selection sync

Picking an object in the viewport (or anywhere else the selection changes: an outline tap, a
`RunOutlineAction` select-on-add) also highlights and scrolls to the matching row in the outline tree, so
the tree always shows what is actually selected instead of drifting out of sync with the viewport.
`MapEditorScene.SyncOutlineSelection` resolves the live `EditorSelection` to its `TreeNode` via
`TreeView.FindByTag` (matching on `OutlineRef` kind/id value equality, so a new `SelectionKind` gets this for
free the day its outline nodes start carrying an `OutlineRef` tag), sets `TreeView.Selected` to it, and calls
`TreeView.ScrollTo` to bring it into view. It runs from `OnSelectionChanged` (a viewport pick or an outline
tap) and again at the end of every `RebuildOutline`, since a rebuild replaces every `TreeNode` wholesale and
would otherwise orphan the previous highlight against a node no longer reachable from `Roots` - this is what
fixes the highlight dropping on every document edit. An outline-originated selection resolves back to the
same node it already set, so the re-set and `ScrollTo` are harmless no-ops, not a feedback loop.

Mid-rename, the highlight stays glued to the row being renamed: each keystroke executes a rename command
that rebuilds the outline before the actual re-select onto the new key fires (that re-select is deferred
until the Name row loses focus, see Renaming above), so `SyncOutlineSelection` resolves against the pending
new key instead of the stale `EditorSelection.Id` for the rest of that frame, keeping the highlight on the
row live as the operator types.

## The headless core

GPU-free and fully unit-tested:

- `EditorDocument` holds the open `MapDocument` plus editor state (dirty tracking, selection, world-rebuild
  signalling) and is the mutation choke point: every edit routes through `Execute`.
- `EditorHistory` is the engine's first undo/redo command stack, with gesture coalescing (a drag collapses
  to one undo step).
- `EditorCommands` are the reversible edits over the document model (placements, spawns, player spawns,
  exclusions, scatter overrides, regions, terrain features, terrain globals). Commands are the only mutation
  path, so undo is total by construction. `EditTerrainCommand` carries all seven terrain-wide scalars as
  nullable fields (WaterLevel, Seed, BiomeBlend, GentleFrequency, GentleAmplitude, DetailFrequency,
  DetailOctaves): a caller passes only the fields it is changing, each field merges independently on a
  same-command follow-up (a scrub coalesces into one undo step per field, not per command), and the
  `MutationService` seed-only special case collapsed into this one widened command. `ReorderFeatureCommand`
  and `ReorderScatterOverrideCommand` move a terrain feature or a scatter override within its list (see
  Feature apply order above). Neither coalesces, so each reorder is its own undo step.
  `SetSpawnArchetypeCommand` and `SetPlayerSpawnYawCommand` make an NPC spawn's archetype id and a player
  spawn's yaw both undoable and dirty-marking through the same free-typed-row, same-id-merge pattern the
  rename rows use (an earlier gap left NPC archetype retyping outside the command stack, fixed alongside the
  new player-spawn command family).
- `EditorVisibility` is the GPU-free, headless-tested editor-session view state described in Visibility
  above: visibility groups, scatter-layer toggles, and per-element hides. It never touches the document, so
  visibility carries no dirty flag and no undo/redo of its own.
- `BakeRegionCommand` freezes a scatter layer's procedural output over a rect region into authored
  placements (tagged `baked`, explicit Y) plus a covering exclusion, so a designer can hand-edit props that
  were procedural. It captures the generated placements on first apply and reuses them on redo, so an
  undo/redo cycle is byte-identical.
- `FreezeZoneCommand` is the terminal whole-zone freeze: it bakes every scatter layer AND every companion
  layer across the document bounds into authored placements (`baked-<source>-N` ids, explicit frozen Y,
  tagged `baked` plus the source layer name) and removes all scatter layers, companion layers, exclusions,
  and scatter overrides, leaving a placements-only document with no procedural generation left to exclude
  against. Where `BakeRegionCommand` freezes one layer over one rect and keeps the layer alive behind a
  covering exclusion, this is the terminal form, converting a hybrid procedural document into a fully
  authored one. One undoable command, captured once on first apply and replayed verbatim on redo, gated by
  a static `HasWork` check so a document already placements-only never lands a phantom undo entry. See
  Freeze zone below.
- `TerrainSculptStrokeCommand` is one undoable sculpt stroke: each frame's brush dab is a stroke command the
  on-stack one absorbs via `TryMerge`, keeping every touched tile's earliest pre-stroke grid and latest final
  grid, so the whole press-drag-release gesture is one undo entry. Undo restores each tile exactly, removes
  tiles the stroke created, and drops a stroke-created layer back to null (byte-identical to no sculpting). It
  reports a bounded `DirtyRegion` so only the touched chunks re-mesh. The brush math (`TerrainSculptBrush`) and
  the footprint clamp (`SculptBounds`) that feed it are pure and headless-tested. See Terrain sculpt above.
- `EditorToolController` is the GPU-free per-frame policy: it reads a plain `EditorFrameInput` (pick ray +
  pointer/keyboard edges) and emits commands. Select mode picks (a press either grabs a gizmo handle or, past
  `BodyDragThreshold` on the object's own body, arms a body drag on the same translate-XZ path) and drives
  the transform-gizmo drag (coalesced into one undo step, sealed on release). The place modes place on the
  press (a ground-snapped Add) and keep tracking the ground point while the pointer stays held, so the whole
  press-hold-release gesture coalesces into that one Add's undo step. The draw modes rubber-band a disc
  (drag) or rect (shift-drag) into an exclusion, a scatter override, or an auto-named region. The
  bake mode drags a rect on the ground and bakes the `BakeLayer` scatter over it. The four draw tools
  (draw-exclusion, draw-scatter-override, draw-region, bake-region) are one shot: a completed gesture (the
  release that emits the command) falls back to Select automatically, so the next click picks the shape
  rather than starting another.
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

Every `FloatRow` the inspector builds goes through a single `AddFloatRow` helper that wires
`FloatRow.GestureEnded` (a pass-through of `NumberField.GestureEnded`, firing once a scrub that moved the
field's value releases, or a typed edit commits) to `SealGesture`. So the SAME field's scrub still coalesces
into one undo step (unchanged, `EditTerrainCommand.TryMerge` merges ANY two terrain edits within one
gesture), but scrubbing one field then a DIFFERENT one back to back - e.g. `WaterLevel` then `BiomeBlend` -
now seals between them and lands as two separate undo steps instead of silently merging into one just
because no explicit tool-level boundary (a mode switch, a pointer release elsewhere) happened to fall
between the two drags.

## Rebuild semantics

`EditorCommand.AffectsWorld` (internal) classifies each command: terrain features (add, edit, remove, and
reorder via `ReorderFeatureCommand`), terrain globals (the water level, `EditTerrainCommand`), scatter
exclusions, scatter overrides (add, edit, remove, AND reorder via `ReorderScatterOverrideCommand`, unlike an
exclusion reorder), and bake-region are `true` (they change terrain shape or scatter inputs). A rename-only
edit (`RenameScatterOverrideCommand`) is `false`, since a name change affects neither shape nor lookup
order. An exclusion reorder (`ReorderExclusionCommand`) is also `false`, since exclusions combine as a set
union with no order dependency (see Feature apply order above), the one place a reorder command does NOT
match its sibling add/edit/remove commands' `AffectsWorld` value. Placement/spawn/region edits are `false`
(they draw outside the streamed sink, so a drag never triggers a chunk rebuild). `EditorDocument` sets
`WorldRebuildPending` whenever an executed, undone, or redone command's `AffectsWorld` is true.

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
(a `SelectionKind.Terrain` node) selects into an inspector with all seven terrain scalars editable
(`BuildTerrainInspector`: WaterLevel, Seed, BiomeBlend, GentleFrequency, GentleAmplitude, DetailFrequency,
DetailOctaves, with Seed and DetailOctaves scrubbed as whole steps and rounded to an int on write), plus a read-only Biomes
count. Biome bands themselves are edited via the Biomes outline category, not the terrain inspector, see
Procedural setup editing below.

`MapEditorScene.OnUpdate` runs, in order, `UpdateCamera` -> `UpdateTools` -> `UpdateChrome` ->
`CheckWorldRebuild` -> `UpdateStreaming`. `CheckWorldRebuild` dispatches a pending edit to either a bounded
`ViewportWorld.PartialRebuild` (re-meshes only the loaded chunks the edit's accumulated dirty region overlaps)
or, when the region is unbounded, the full `ViewportWorld.Rebuild` (tear down the sink + streamer, then
rebuild wholesale from the document, keeping the cached kit meshes and the splat material so a full rebuild
does not re-decode every prop glTF from disk), then calls `EditorDocument.AcknowledgeWorldRebuild()`. The
full path is throttled while a drag or draw gesture is live (`EditorToolController.IsDragging` / `IsDrawing`):
it runs at most once per `MapEditorOptions.GestureRebuildInterval` seconds (default 0.25, 0 disables the
throttle), with `WorldRebuildPending` left untouched on a throttled frame so the very next check after the
gesture ends always performs the final full rebuild. The partial path is never throttled (it is cheap by
construction). A bounded `DirtyRegion` comes from `FeatureGeometry.TryFootprint` for terrain features and
`ShapeGeometry.TryBounds` for exclusion / scatter-override shapes (an AABB padded by a margin captured at
apply time: a base constant plus the document's largest scatter jitter, since scatter tests shape
membership at the jittered candidate position while chunk assignment uses the cell centre), so a gizmo
drag on any of those stays on the never-throttled partial path instead of the gesture-throttled full one.

**Same-frame inspector rebuild.** A terrain-feature parameter scrub lands through the `PropertyGrid`
inspector, which is polled inside `UpdateChrome`, now BEFORE `CheckWorldRebuild` in the per-frame order (moved
there to fix a one-frame lag the editor used to have: chrome used to run after the rebuild check, so an
inspector edit's `WorldRebuildPending` flip landed one frame too late for that frame's rebuild). So when the
inspector's `FloatRow` setter calls `EditorDocument.Execute(new EditFeatureCommand(...))`, the streamed world
rebuilds (or partial-rebuilds) the SAME frame, same as a gizmo-driven drag (`UpdateTools`, which runs even
earlier still). Placement/spawn drags never trigger a rebuild either way (`AffectsWorld` is always false,
since they draw outside the streamed sink), and neither do region drags (game-interpreted, also `AffectsWorld`
false).

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

## Freeze zone

Ctrl+Shift+F (Cmd+Shift+F on a Mac) runs `EditorToolController.FreezeZone()`
(`MapEditorScene.FreezeZoneChord`, `MapEditorScene.Shortcuts.cs`). A chord rather than a tool mode: unlike
the draw and bake tools, freezing has no gesture to arm, drag, or cancel, it is a one-shot whole-document
action, so it does not belong in the tool palette. `FreezeZoneCommand.HasWork` gates it: a document with no
scatter or companion layers already has nothing to freeze, so the chord lands the status-strip note
"Nothing to freeze: the zone has no scatter or companion layers." instead of a phantom undo entry, the same
no-op idiom `DuplicateSelectionChord` uses over an empty selection.

Where `BakeRegionCommand` freezes one scatter layer over one dragged rect and leaves that layer alive
behind a covering exclusion, `FreezeZoneCommand` is the terminal form: it bakes EVERY scatter layer and
companion layer across the whole document bounds, then removes all scatter layers, companion layers,
exclusions, AND scatter overrides. No covering exclusion is added, because once no scatter layer survives
there is nothing left to re-scatter over the frozen props. Use it to convert a hybrid procedural document
into a fully authored, placements-only one, the terminal step before a zone ships with no runtime scatter
cost.

Each frozen prop becomes a `MapPlacement` with a document-unique `baked-<sourceLayer>-N` id, an explicit
frozen Y (so a later re-snap cannot drift it), and two tags: `baked` plus the source layer name, so a
reviewer can tell which layer produced a prop from its tags alone. Companion layers are baked from the same
host generation pass that produced their host scatter layer's placements, before the host layer is removed,
matching how the runtime rings companions per chunk.

The whole freeze is one undoable `FreezeZoneCommand`: it captures the baked placement list and the four
removed collections (scatter layers, companion layers, exclusions, scatter overrides) on the first `Apply`
and replays them verbatim on every redo, so an Apply/Revert/Apply cycle is byte-identical. `Revert` restores
all four collections exactly and removes the baked block. `MapEditorScene.FreezeZoneChord` reports the
outcome in the status strip: the placement count plus how many of each collection were removed.

## Renaming

The placement, spawn, player spawn, and region inspectors lead with an inline-editable Name row
(`MapEditorScene.AddNameRow`, shared by all four). Typing a new id or name and moving focus away routes the
edit through `RenamePlacementCommand`, `RenameSpawnCommand`, `RenamePlayerSpawnCommand`, or
`RenameRegionCommand`, rejecting a blank, unchanged, or colliding target before it touches the document, so a
rejected rename lands no undo step. Placements, spawns, and player spawns are keyed by id and regions by
name, so a rename must move the selection to the new key. An immediate
`Selection.Set` mid-keystroke would rebuild the inspector and drop the row's focus, so the re-select is
deferred until the Name row itself loses focus. A different selection made first, an outline click or a
viewport pick while the row is still focused, wins over the stale pending re-select and drops it.

Terrain features, exclusions, and scatter overrides carry an optional `Name` too (`MapFeature.Name` /
`MapExclusion.Name` / `MapScatterOverrideDoc.Name`, empty means unnamed), but they are selected by list
INDEX, not by name, so their Name row uses the separate `MapEditorScene.AddIndexNameRow` variant: a rename
routes through `RenameFeatureCommand` / `RenameExclusionCommand` / `RenameScatterOverrideCommand` (rejecting
an unchanged or colliding non-empty target the same way, but ALLOWING a blank target since Name is optional
there) and never touches the selection, since the index a rename targets never moves. The outline label
falls back to the index when Name is empty: `"[i] type"` for a feature, `"exclusion[i]"` for an exclusion,
`"override[i]"` for a scatter override. An exclusion's or a scatter override's label always carries a
trailing targeting hint from its `Layers` too, `" (all)"` for a null filter or `" (trees, groundcover)"`
style for an explicit one (`MapEditorScene.TargetingHint`), so the outline alone shows which scatter layers
it masks or retunes.

The exclusion and scatter override inspectors also get layer-targeting rows below their other rows
(`MapEditorScene.AddExclusionLayerRows` / `AddScatterOverrideLayerRows`, both routed through the shared
`AddLayerTargetingRows` helper): an "All layers" `BoolRow` bound to `Layers == null` (masks or retunes every
layer, including future ones), plus one `BoolRow` per document scatter layer while an explicit list is in
effect. Checking All ON collapses the list to null. Checking it OFF materializes the full explicit layer
list. The per-layer rows are hidden (not merely disabled, there is no live per-row enabled hook) while All is
on, reflowing into view the next chrome step once All goes off, through the same `SyncShapeInspector`
rebuild-on-mismatch idiom the shape-kind conversion uses. Manually re-checking every layer does NOT
auto-collapse back to null: only the All toggle itself produces null, so an explicit list stays explicit
even when it happens to name every layer. Every layer-row change routes through
`EditExclusionLayersCommand` or `EditScatterOverrideValuesCommand` (the whole-value path for a scatter
override), each `AffectsWorld` true (a targeting change affects what the streamed scatter draws).

## Procedural setup editing

The outline gains three categories alongside the existing seven roots (`RebuildOutline`): `Biomes` (a sibling
of `Terrain`, not nested under it), `Scatter Layers`, and `Companion Layers`. Each lists its elements as
selectable nodes plus a trailing synthetic `[+ add ...]` action node (`OutlineActionKind.AddBiomeBand` /
`AddScatterLayer` / `AddCompanionLayer`, `MapEditorScene.RunOutlineAction`): tapping it appends a
default-valued element, seals the gesture, and selects the new element immediately, the same
select-on-add idiom the placement/spawn/feature tools use. A new scatter or companion layer gets an
auto-generated name (`GenerateLayerName`, the smallest `"layer-N"` / `"companion-N"` not already live). A
new companion layer also defaults its `HostLayer` to the document's first scatter layer when one exists, so
it validates without an extra step (with none yet, `HostLayer` stays empty until the operator adds a layer
and picks it through the HostLayer chooser below).

**Biome bands** (`SelectionKind.BiomeBand`, index-keyed, no reorder command) select into an inspector with a
read-only "Affects" row stating what a band drives today (terrain shaping and the scatter rules keyed by
that biome, ground tinting not yet wired), then a `Biome` `ChoiceRow` over the `BiomeId` enum names, then
the nullable `Start`/`End` world-Z edges and the `BaseHeight`/`HillAmplitude` scalars (a band is a Z-axis
slice, not a height range, and its rows and descriptions say Z, not "height", now that the terminology has
been corrected). Each nullable edge is a `FloatRow` for the concrete value paired with an "<edge> open"
`BoolRow` that toggles the null state (open = null = unbounded), mirroring the exclusion "All layers"
null-gate: both rows are always present (no reflow), and editing the `FloatRow` closes an open edge to that
value. Every edit is a whole-value edit through `EditBiomeBandCommand` (clone the live band, change one
field, same-index merge coalesces a scrub). Bands carry no name and no authored shape, so there is no Name
row and no Visible row (visibility is a per-placed-shape concept, and a band is not independently
hideable). The selected band still draws its finite Start/End edges as full-width world-Z lines in the
viewport (`MapEditorScene.AddBandEdgeLine`, an always-on aid rather than a toggled overlay element): an
open edge draws nothing, and only the current selection draws since a band has no other viewport geometry
and its order is meaningless.

**Scatter layers** (`SelectionKind.ScatterLayer`, name-keyed like placements/spawns/regions) select into an
inspector with an inline-rename Name row, then the layer scalars (Seed, CellSize, Jitter, ScaleMin/ScaleMax,
a nullable MaxHeight via the same open-edge `BoolRow` idiom), then the per-rule surface: each rule in
`MapScatterLayer.Rules` gets a Biome `ChoiceRow`, a Density `FloatRow`, and a `TextRow` for Kinds as a
comma-separated `"id:weight"` list (`ParseKinds`/`FormatKinds`, the same convention `ke-mapedit` uses), plus
a `[- remove rule N]` action row, with a trailing `[+ add rule]` and a `[- remove layer]` action row. Every
scalar and rule edit is a whole-value edit through `EditScatterLayerCommand` (deep-clone the live layer so a
nested `Rules`/`Kinds` mutation never touches the captured old value), same-name merge coalesces a scrub. A
rule add or remove seals its own gesture and reflows the per-rule rows through the deferred
`_inspectorRuleCount` sync (never a rebuild mid grid-iteration, the same idiom as the staleness sync below).
Renaming a scatter layer routes through `RenameScatterLayerCommand`, which CASCADES the new name into every
companion layer's `HostLayer` and every exclusion/scatter-override explicit layer filter that names it, so a
rename never orphans a reference (a null "all layers" filter is untouched, since it names no layer). The
editor separately follows the rename with `EditorVisibility.RenameLayer` for the visibility key, since that
is view-only session state, not part of the document. `[- remove layer]` routes through
`RemoveScatterLayerCommand`, which REJECTS a removal that would orphan a companion host or an explicit layer
filter, throwing before it mutates anything (`RemoveScatterLayerFromInspector` catches the
`InvalidOperationException` and surfaces its message in the status strip, leaving the document untouched).

**Companion layers** (`SelectionKind.CompanionLayer`, name-keyed) select into an inspector with an
inline-rename Name row, a `HostLayer` `ChoiceRow` over the document's live scatter-layer names (falling back
to a `ReadOnlyRow` only when there are no scatter layers and no host set yet, so the dropdown never silently
drops an out-of-set value), the Seed/CountMin/CountMax/RadiusMin/RadiusMax/ScaleMin/ScaleMax scalars and a
nullable MaxHeight, `HostKinds` (a plain id list `TextRow`) and `Kinds` (an `"id:weight"` list `TextRow`,
same `ParseKinds` convention), then a `[- remove companion]` action row. Every edit is a whole-value edit
through `EditCompanionLayerCommand` (deep clone). Renaming a companion layer through
`RenameCompanionLayerCommand` does NOT cascade, unlike a scatter-layer rename: nothing else references a
companion layer by name, so it just renames the one layer. Removing one likewise needs no reject guard.

`HostKinds` filters which of the host layer's placements grow companions: an empty or absent list now
matches every host placement in the layer (see `KhaozEngine.Terrain`'s `PropScatter.GenerateCompanions` for
the full semantics change), so leaving it empty is no longer a silent "grow nothing" trap. **Host-swap UX**:
changing the `HostLayer` `ChoiceRow` swaps the host in one whole-value edit, and when the current `HostKinds`
is non-empty and has zero intersection with the new host layer's placeable kit ids, that same edit also
clears `HostKinds` back to empty (match-all) and leaves a "host kinds cleared to match all hosts" note in the
status strip, so a host swap that would otherwise leave every companion silently orphaned is caught and
fixed in the SAME undo step (one undo restores both the old host and the old `HostKinds`). Independent of
that swap, whenever the inspector shows a companion with a non-empty `HostKinds` that has zero intersection
with its CURRENT host layer's kit ids, a read-only, warning-styled "Warning" row appears right under
`HostKinds` reading "HostKinds match no kind in the host layer" - a live-tracked mismatch (`UpdateChrome`
compares it every chrome step and reflows the row on a change from `HostKinds` edits, a host swap, or an
undo/redo of either), not a validator error, since a transient mismatch mid-edit must never block a save.

**Staleness triggers.** The exclusion inspector's layer-targeting rows (above) and the companion inspector's
`HostLayer` chooser both enumerate the document's live scatter-layer names at inspector-build time
(`_inspectorScatterNames`), so either would go stale if the scatter-layer set changed while that inspector
stayed selected. `UpdateChrome` compares the captured snapshot against the live set every chrome step and
rebuilds the inspector on a mismatch (add, remove, or rename of a scatter layer), the same deferred
rebuild-on-mismatch idiom `SyncShapeInspector` uses for a shape-kind conversion. The scatter-layer
inspector's own rule rows use the analogous `_inspectorRuleCount` check so a rule add/remove reflows without
a mid-iteration rebuild.

The add, edit, and remove commands above are `AffectsWorld` true (they change terrain shape or scatter inputs), triggering the same
streamed-world rebuild path described in Rebuild semantics above (same-frame, gesture-throttled while a drag
or draw is live). The two rename commands (for scatter layers and companions) are deliberately `AffectsWorld` false,
since a rename is byte-identical to streamed output and requires no rebuild.

## `DocumentChanged` unsubscribe note for custom hosts

`MapEditorScene.OnEnter` subscribes to `EditorDocument.DocumentChanged` and `EditorSelection.Changed`
(rebuilding the outline / inspector), and `OnExit` unsubscribes both. If you build your own host around
`EditorDocument` directly (bypassing `MapEditorScene`, for example a headless batch tool or a custom
viewport), remember to unsubscribe any handler you attach to `DocumentChanged`/`Selection.Changed` yourself
when you tear the host down. `EditorDocument` outlives nothing on its own: a forgotten unsubscribe keeps
your handler (and whatever it closes over, such as a disposed `ViewportWorld`) reachable from the document
for as long as the document itself is referenced.
