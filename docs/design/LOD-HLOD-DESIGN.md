# LOD/HLOD: far-field terrain tiers, prop fades and LOD meshes, baked cluster impostors

Design approved 2026-07-23 (direction set by the user: the far world stays VISIBLE, no fog and no
distance hiding, and the arc was pulled forward from data-gated to active). Roadmap issue #276.
Interim bridge while this ships: Ruinborne brute-forces full-island residency, affordable at 450 m
world scale and explicitly not at continent scale.

## Problem

The engine renders exactly one residency disk of terrain around the camera and hard-culls props at
a per-layer radius. Beyond the disk there is nothing, so an always-drawn water plane reads as
floating sky-water wherever unstreamed terrain should have occluded it, and the horizon is empty.
The target is a continent-scale world whose far field renders for real.

## What already exists (recon 2026-07-23, do not rebuild)

- A working 3-tier terrain LOD: `TerrainLod` (resolutions 64/32/16, thresholds 80 m/200 m),
  distance-picked per chunk by `TerrainStreamer`, applied as an in-place atomic re-LOD through
  `Scene3DChunkSink`, with skirts hiding cracks between tiers. All hardcoded constants today.
- `StreamerConfig` with chunk-unit load/unload radii and hysteresis, budgeted nearest-first loads,
  async CPU builds.
- A per-instance rigid dissolve primitive in the instancing path (14.5.0), opaque noise discard so
  overlapping fades never sort-fight, explicitly shipped for the still-unbuilt prop fade band (#44).
- `TexturedBillboardRenderer`: depth-interleaved textured quads in the model pass, the natural
  impostor draw path, currently without a per-instance fade channel.
- Always-on `RenderFrameStats` (draw calls, instances, triangles) and the three-backend GPU golden
  harness, the regression guardrails for every phase.

## Decisions

1. **Extend the existing tier system, do not replace it.** `TerrainLod` becomes data-driven: a
   config of tier resolutions and distance thresholds (defaults preserving today's 64/32/16 at
   80/200), with coarser far tiers added (16 down to 8 and 4 segments) so a chunk at 2 km costs a
   few hundred triangles. `StreamerConfig` carries the new far radius.
2. **A decor ring beyond the gameplay ring.** Chunks past the gameplay radius are render-only: no
   scatter generation, no prop colliders, no terrain collision registration. Far terrain is
   scenery, the simulation never touches it. This caps the cost of seeing far at mesh cost only.
3. **Collision LOD decouples from render LOD.** `collideTerrain` registers the physics mesh at one
   fixed collision resolution regardless of the render tier, killing the triangle-mesh rebuild on
   every tier crossing. The prop-collider rebuild on re-LOD (recon finding: placements are
   LOD-independent yet colliders rebuild anyway) is eliminated at the same time.
4. **Props fade, then LOD.** First wire the existing dissolve primitive into `PropRenderer` as a
   distance fade band at each layer's draw radius, closing #44 with zero new shader work. Then add
   optional per-kit LOD mesh variants (a manifest `lodFile` field plus distance selection in
   `PropRenderer`), so forests keep silhouettes at range without full meshes.
5. **HLOD is an author-time bake, shape decided by a spike.** Per chunk-cluster artifact baked by a
   `ke-hlodbake` tool following the `ke-propbake` manifest-stamping pattern. Two candidate shapes:
   a merged coarse mesh per cluster (reuses the existing mesh upload path, no new shader, favored
   first) versus a billboard impostor atlas (needs an atlas packer, which does not exist, and a
   per-instance fade in the billboard shader, which does not exist). The L3 spike renders both for
   one real forest cluster and the numbers decide. Alpha-to-coverage stays out unless the spike
   proves it necessary, since it requires new `KhaozEngine.Gpu` surface.
6. **Guardrails on every phase.** `RenderFrameStats` before/after assertions in headless tests,
   `TerrainStreamer.LodOf` tier assertions, and GPU goldens (named with the Golden convention so
   the three-backend matrix sweeps them) for visual parity at tier boundaries.

## Phasing (each releasable, consumer adopts per phase)

- **L1, terrain far field.** Data-driven tiers, far tiers, far radius, decor ring, collision
  decouple and re-LOD churn fixes. This alone gives Ruinborne a real horizon and retires the
  full-island residency bridge.
- **L2, prop presentation.** Dissolve fade band (#44), then manifest LOD mesh variants and
  distance selection.
- **L3, HLOD bake.** The spike, then the chosen artifact, `ke-hlodbake`, runtime consumption with
  cross-fade, golden coverage.

## Non-goals

- Fog or any distance-based hiding as a correctness mechanism (user-rejected, visibility is the
  point). Games may still use atmospheric haze aesthetically.
- World partitioning and streamed persistence (tracked separately in #269).
- GPU timestamp queries (Veldrid 4.9.0 has none, CPU-encode timings and RenderFrameStats stand in).
- Alpha-to-coverage, unless the L3 spike demands it.
