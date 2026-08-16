# KhaozEngine.TileWorld.Editing

The editing kernel for a `KhaozEngine.TileWorld` document: every mutation is a reversible command, so
undo and redo work the same way whoever issued the edit. GPU-free and render-free, in the `Foundation`
umbrella, referencing `KhaozEngine.TileWorld` and nothing else.

## Why it is its own package

A tile world is authored from two frontends: the `ke-tileedit` MCP tool (an AI client over stdio) and,
in a later round, an in-engine GUI editor. Both mutate the same document, and both need the same undo
stack. Putting the command layer here rather than in either frontend means the two cannot drift apart
on what a single edit is, and it keeps the layer free of any Gui or Render3D dependency, which is what
lets a headless test cover the whole thing.

The `MapDoc` side of the engine learned this the other way round: its command stack lives inside
`KhaozEngine.MapEditor`, which drags in Gui, Render3D and Terrain.Render3D, so the `ke-mapedit` tool
carries a renderer it only needs for two verbs. This package is that shape corrected.

## Shape

A command captures what it needs to revert itself before it applies, reports the tiles it touched as
dirty rects, and may merge with the next command of its own kind so a drag lands as one undo step. The
editing document owns the document, the history, the saved marker and the derived collision map, and
rebakes collision over the accumulated dirty rects after each command, so a query right after an edit
sees consistent collision without a full world rebake.

Design rationale: `docs/design/TILE-WORLD-DESIGN-2026-08-15.md` in the engine repo.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
