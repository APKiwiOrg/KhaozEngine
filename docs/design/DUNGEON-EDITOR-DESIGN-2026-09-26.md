# Dungeon generation in MapEditor

Approved and implemented for staged 20.6.0 on 2026-09-26. Tracks the editor integration in
[#74](https://github.com/APKiwiOrg/KhaozEngine/issues/74).

## Goal

An editor operator can generate a deterministic dungeon into the open map, see the result, and undo or redo the entire bake as one edit. A game supplies the piece-to-kit mapping and may supply a generator preset. The editor provides the command and the visible workflow. The grammar and decoration follow-ups in #74 remain conditional.

## Existing contracts

- `DungeonGenerator.Generate(config, seed)` produces a validated `DungeonLayout`. `DungeonMapDocEmitter.Emit` appends placements, spawns, regions, one flatten feature, and a bounds expansion to a `MapDocument`.
- `EditorDocument.Execute` is MapEditor's mutation choke point. `EditorHistory` calls a command's `Apply` before adding it to the undo stack, so a failed `Apply` must leave the document unchanged.
- `MapEditorScene` owns a modal settings pattern based on `PropertyGrid`. It already gates camera, tools, and shortcuts while a modal is open.
- A large tiled document may be loaded through a window. Its save path refuses a write that would overwrite an occupied tile the editor did not load.

## Package choice

`KhaozEngine.MapEditor` references `KhaozEngine.Dungeon` directly. This does not create a cycle, since Dungeon depends on MapDoc and other Foundation packages but not MapEditor. MapEditor is opt-in and remains outside the umbrellas. The command and dialog are new types. The existing scene file is at its KESIZE baseline, so any chrome wiring that would grow it moves a cohesive existing chrome method group into a partial file and lowers the baseline in the same change.

| Criterion, 1 to 10 | Direct editor dependency | Game callback | Separate adapter package |
|---|---:|---:|---:|
| Built-in operator workflow | 10 | 5 | 8 |
| Cohesive ownership | 9 | 4 | 8 |
| Package isolation | 7 | 10 | 10 |
| Delivery risk | 8 | 6 | 3 |
| Extensibility | 7 | 7 | 9 |
| **Total** | **41** | **32** | **38** |

A callback makes each game implement the bake and undo boundary. An adapter package would need a new editor extension seam for one feature. The direct dependency keeps the first complete workflow in one optional package.

## Public API

`MapEditorOptions` gains an optional `DungeonKitMap` and an optional `DungeonConfig` preset. Without a kit map, the editor hides the Generate dungeon action. A missing preset means `new DungeonConfig()`.

`GenerateDungeonCommand` is a public `EditorCommand` taking a config, `ulong` seed, kit map, plot transform, and optional spawn archetype id. It copies the config at construction, so later caller edits cannot change the pending command. Games can call `EditorDocument.Execute` with it directly, including with settings that the focused panel does not expose.

The first successful `Apply` generates the layout and stages the emitter output. Redo reuses that exact output and never reruns generation. The command reports `AffectsWorld = true` and requests a full world rebuild, because the flatten feature and map bounds change the terrain and streaming extent.

## Operator workflow

When a kit map is configured, a Generate dungeon button appears beside Save in the toolbar. Clicking it opens a modal panel. The panel follows the existing settings dialog's scrim, scrolling `PropertyGrid`, footer buttons, and input ownership. Escape or Cancel closes it without changing the map. Generate stays in the panel on validation failure and shows the error there. It closes after a successful command, leaving the map dirty and the new dungeon visible after the normal world rebuild.

The panel edits these values.

| Group | Fields |
|---|---|
| Reproduction | Seed as an invariant-culture unsigned 64-bit integer |
| Placement | Plot origin X and Z, base Y, yaw in degrees |
| Main layout | Plot width and depth in tiles, room count target, max floors, corridor minimum and maximum width, ceiling mode |

Every other `DungeonConfig` field comes from a copy of the game preset. The copy uses `DungeonJson`'s config round-trip so a later config field is carried without a second manual property list. The panel does not write its edits back to `MapEditorOptions`. A new open begins from the configured preset. The initial plot is centred around the terrain hit under the viewport centre ray. If that ray misses or there is no live viewport, it uses the document bounds centre. The plot origin is offset by half the unrotated plot width and depth so the starting plot is centred on that point. The fields remain editable before generation.

All labels and errors are developer-tool text under MapEditor's existing localization exemption.

## Command transaction and undo

The command stages `DungeonMapDocEmitter.Emit` into a scratch `MapDocument` with a copy of the target's starting bounds. It keeps the emitted placements, spawns, regions, flatten feature, and resulting bounds together as one patch. A missing kit mapping, invalid config, or generator failure occurs before the target is touched.

Before appending, it rejects an emitted placement or spawn id, or region name, that already exists in the target. This covers a repeated bake at the same layout and plot, whose deterministic salt would otherwise reuse ids. It also validates the input plot coordinates, generated bounds, and every staged placement, spawn, region, and flatten feature as finite values. A failed preflight leaves the document and history unchanged.

`Apply` appends the staged objects and sets the new bounds. If an ordinary append fails partway, it removes only this command's appended tail and restores the original bounds before rethrowing. `Revert` removes that tail and restores the exact prior bounds. It checks the expected tail before removal, so an external mutation cannot silently delete a different author's content. Redo appends the cached patch again. Undo and redo use `EditorDocument`'s existing dirty and rebuild signals.

## Tiled document boundary

For a windowed tiled document, the editor checks the rotated plot's world-space bounding rectangle against the loaded `MapTileRect` before executing the command. A plot crossing that window is rejected with a message to open the needed window or load the whole document. This prevents a successful in-memory bake that the editor cannot show or safely save. The command's public API accepts an optional loaded-window constraint. When applied to a partial document without one, it refuses the bake rather than guessing which empty coordinates were loaded. Whole-loaded and monolithic documents need no window argument.

The existing `MapTiledFile.Save` guard still protects the on-disk document. The new check is an earlier editor error, not a replacement for save validation.

## Verification

Headless tests in `KhaozEngine.MapEditor.Tests` cover one generated bake through `EditorDocument.Execute`, dirty state, rebuild signal, undo and redo with exact document restoration, deterministic redo after config or kit mutation, duplicate-id refusal, invalid config and missing-kit atomicity, and a windowed plot that crosses the loaded range. Scene tests check action visibility, cancel without mutation, invalid seed feedback, and modal input gating without a GPU. The test project takes only the engine references it uses.

The package and API change gets the full Markdown name and behavior sweep, including the root package catalog, MapEditor README, consumer guide, dependency edges, and the staged changelog entry. Release build and tests, local-feed pack, and the whole-tree guards run before integration. A windowed visual pass checks panel layout, field editing, error display, Generate, undo, and redo after the headless suite.

## Scope boundary

This work does not add a mission grammar, decoration pass, or a persistent dungeon-editor settings file. The game continues to own its kit ids and any advanced preset. The CLI and runtime stamp remain separate existing entry points.
