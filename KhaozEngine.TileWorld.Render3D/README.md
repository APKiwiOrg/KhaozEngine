# KhaozEngine.TileWorld.Render3D

The render arm of [KhaozEngine.TileWorld](../KhaozEngine.TileWorld): meshes a tile world's ground into
vertex-coloured `Render3D` meshes, places its objects through the `Terrain.Render3D` prop path, and owns the
per-region scene handles, region streaming and headless snapshot capture on top. Kept separate from the
render-free document so a server or tool never drags in `Render3D`. In the `Game3D` umbrella.

Design: [docs/design/TILE-WORLD-DESIGN-2026-08-15.md](../docs/design/TILE-WORLD-DESIGN-2026-08-15.md).

The full API summary lands with the round.
