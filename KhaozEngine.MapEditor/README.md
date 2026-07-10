# KhaozEngine.MapEditor

Opt-in in-engine map editor runtime. A document-driven viewport over `KhaozEngine.MapDoc`, tool modes,
selection with gizmos, and an undo/redo command stack, wrapped in a turn-key scene a per-game head pushes.

This package is **not** bundled in any umbrella (the `KhaozEngine.Server.Admin` precedent): add it
explicitly to a game head that wants to edit its zone documents, so a shipping game never pulls the editor.

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

Ctrl+Z undo, Ctrl+Shift+Z or Ctrl+Y redo, Ctrl+S save, Delete removes the current selection. Escape
cancels an in-flight gizmo/draw gesture and returns to Select. Shift+Escape exits the editor by popping
the scene off the `SceneManager`: with no unsaved changes it pops immediately, and with unsaved changes
the first press arms a status-strip warning ("Shift+Escape again to discard and exit") and a second
Shift+Escape discards and exits. Any Ctrl+S or any document mutation (an edit, an undo, a redo) disarms
the warning, so a stale discard confirmation can never fire after the user resumed working. The status
strip shows the chord as a standing hint.

## The headless core

GPU-free and fully unit-tested:

- `EditorDocument` holds the open `MapDocument` plus editor state (dirty tracking, selection, world-rebuild
  signalling) and is the mutation choke point: every edit routes through `Execute`.
- `EditorHistory` is the engine's first undo/redo command stack, with gesture coalescing (a drag collapses
  to one undo step).
- `EditorCommands` are the reversible edits over the document model (placements, spawns, exclusions,
  regions, terrain features). Commands are the only mutation path, so undo is total by construction.
- `BakeRegionCommand` freezes a scatter layer's procedural output over a rect region into authored
  placements (tagged `baked`, explicit Y) plus a covering exclusion, so a designer can hand-edit props that
  were procedural. It captures the generated placements on first apply and reuses them on redo, so an
  undo/redo cycle is byte-identical.
- `EditorToolController` is the GPU-free per-frame policy: it reads a plain `EditorFrameInput` (pick ray +
  pointer/keyboard edges) and emits commands. Select mode picks and drives the transform-gizmo drag
  (coalesced into one undo step, sealed on release). The place modes ground-snap a click into an Add. The
  draw modes rubber-band a disc (drag) or rect (shift-drag) into an exclusion or an auto-named region. The
  bake mode drags a rect on the ground and bakes the `BakeLayer` scatter over it.

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

`EditorCommand.AffectsWorld` (internal) classifies each command: terrain features, scatter exclusions, and
bake-region are `true` (they change terrain shape or scatter inputs). Placement/spawn/region edits are
`false` (they draw outside the streamed sink, so a drag never triggers a chunk rebuild). `EditorDocument`
sets `WorldRebuildPending` whenever an executed, undone, or redone command's `AffectsWorld` is true.

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
runs BEFORE `CheckWorldRebuild`) do not have this lag, though in practice only placement/spawn drags go
through the gizmo and those never set `AffectsWorld` anyway.

## Bake-region

`EditorToolMode.BakeRegion` drags a rect on the ground. On release, `EditorToolController` executes a
`BakeRegionCommand(region, layer, registry)` for `EditorToolController.BakeLayer` (defaults to the
document's first scatter layer, null when the document has none, in which case the drag is a no-op).
`BakeRegionCommand.Apply` runs `PropScatter.Generate` against the document-built field and scatter config
for that layer over the rect, converts each generated placement to an authored `MapPlacement` (a
document-unique `baked-<layer>-N` id, the scatter kit id, the frozen ground Y so a later re-snap cannot
drift it, and a `baked` tag), and adds a matching rect exclusion scoped to that one layer so the frozen
props are never re-scattered on top of themselves. The generated placement list and exclusion are captured
on the FIRST `Apply` and replayed verbatim on every redo (never regenerated), so an Apply/Revert/Apply
cycle is byte-identical even though `PropScatter` is otherwise deterministic only against a fixed field.

## `DocumentChanged` unsubscribe note for custom hosts

`MapEditorScene.OnEnter` subscribes to `EditorDocument.DocumentChanged` and `EditorSelection.Changed`
(rebuilding the outline / inspector), and `OnExit` unsubscribes both. If you build your own host around
`EditorDocument` directly (bypassing `MapEditorScene`, for example a headless batch tool or a custom
viewport), remember to unsubscribe any handler you attach to `DocumentChanged`/`Selection.Changed` yourself
when you tear the host down. `EditorDocument` outlives nothing on its own: a forgotten unsubscribe keeps
your handler (and whatever it closes over, such as a disposed `ViewportWorld`) reachable from the document
for as long as the document itself is referenced.
