# Tile overlay feather implementation

Approved scope: a narrow authored overlay transition for the Grimhollow pasture track, implemented in the
shared engine. Hard overlays remain the default. No consumer or release work is part of this branch.

1. Add headless expectations for straight edges, shaped cuts, joined tiles and region boundaries.
2. Add the `FeatherOverlay` tile flag and a world-space mesher width. The flag round-trips through existing
   editor settings and storage. Collision and the authoritative surface triangulation remain unchanged.
3. Resolve exposed overlay boundaries in global tile coordinates. Subdivide only opted overlay triangles
   on their existing surface and blend inward into their authored underlay. Preserve the four underlay
   material lanes and carry the optional overlay in the spare fifth vertex lane.
4. Prove the shader on a local headless GPU, including hard-mode compatibility. Document the authoring
   requirement to put the surrounding ground beneath a feathered overlay.
5. Ride or select the next free additive version, update the changelog and consumer documentation, run
   Release build and all non-LiveSocket tests, format and repository guards. Commit and push this branch.

The selected width is 0.2 metres by default, capped to half a tile. Tessellation samples the fade at least
twice across its width up to a bounded 64 subdivisions per original triangle edge. It introduces no new
graphics resources. All boundary queries use the global document so a loaded region seam has one answer.

## Rendering evidence

The automated `TileOverlayFeatherGpuTests` captures a red overlay on a green underlay through the real
tile mesher and renderer. Its assertions measure the exposed ground, mixed border and opaque center.
The [Metal capture](../../design/evidence/2026-09-27-tile-overlay-feather-metal.png) records the local proof.
It is documentary evidence, not a golden reference. Backend CI runs the same pixel assertions without
needing a new golden bake. Set `KE_TILE_FEATHER_EVIDENCE` to an output PNG path to retain a fresh capture.
