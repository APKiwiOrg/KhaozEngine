# KhaozEngine.MapEditor

Opt-in in-engine map editor runtime. A document-driven viewport over `KhaozEngine.MapDoc`, tool modes,
selection with gizmos, and an undo/redo command stack, wrapped in a turn-key scene a per-game head pushes.

This package is **not** bundled in any umbrella (the `KhaozEngine.Server.Admin` precedent): add it
explicitly to a game head that wants to edit its zone documents, so a shipping game never pulls the editor.

The headless core is GPU-free and fully unit-tested:

- `EditorDocument` holds the open `MapDocument` plus editor state (dirty tracking, selection, world-rebuild
  signalling) and is the mutation choke point: every edit routes through `Execute`.
- `EditorHistory` is the engine's first undo/redo command stack, with gesture coalescing (a drag collapses
  to one undo step).
- `EditorCommands` are the reversible edits over the document model (placements, spawns, exclusions,
  regions, terrain features). Commands are the only mutation path, so undo is total by construction.

The turn-key `MapEditorScene`, viewport streaming, picking, and gizmos land alongside. Developer-only tooling,
so the editor UI is `LocalizationExempt`.
