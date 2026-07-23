# LOD/HLOD: far-field terrain tiers, prop fades and LOD meshes, baked cluster impostors

Design approved 2026-07-23 (direction set by the user: the far world stays VISIBLE, no fog and no
distance hiding, and the arc was pulled forward from data-gated to active). Roadmap issue #276.
Interim bridge while this ships: Ruinborne brute-forces full-island residency, affordable at 450 m
world scale and explicitly not at continent scale.

**Status: Complete.** All three phases shipped: L1 (14.16.0, far-field terrain tiers + decor ring +
collision decouple), L2 (14.17.0, prop fade band + manifest LOD meshes), L3 (14.18.0, merged-coarse-mesh
HLOD). The spike (decision 5) chose merged coarse mesh over billboard impostor. See the L3 implementation
round below for the runtime-vs-offline bake decision. Shipped API + usage live in `CHANGELOG.md`,
`docs/USING-KHAOZENGINE.md`, and `KhaozEngine.Terrain.Render3D/README.md`, not here.

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

## L3 implementation round (14.18.0)

The spike (issue #276 comment) rendered both candidate shapes for one real forest cluster and chose the
**merged coarse mesh**: same draw collapse as the impostor (1 draw / 1 instance), adequate fidelity at
range, robust to a free/orbiting camera (no atlas, no flat-card skew, no popping beyond the crossfade),
and it drops into the existing mesh upload + `Scene3D.Draw` path with zero new shader work, where the
impostor would have needed an atlas packer, a per-instance billboard fade, and multi-direction bake +
crossfade (three subsystems that do not exist). Impostors stay on the table as a later opt-in tier for the
extreme far ring if profiling ever shows the merged triangle count actually hurts (it does not at these
scales: even 1000 clusters at 16k tris is 16M tris, nothing for a modern GPU).

**Bake shape: RUNTIME bake at chunk load, cached per cluster. No offline artifact, no `ke-hlodbake` tool
for v1.** Decision 5 sketched an author-time `ke-hlodbake` following the `ke-propbake` manifest-stamping
pattern, but that was written before the runtime picture was clear. The judgment call, made here with the
code in hand:

- The merge inputs (prop placements) are 100% deterministic from `(TerrainField, ScatterConfig, chunk
  area)` via `PropScatter.Generate` - the *exact same call* `Scene3DChunkSink` already makes for every
  gameplay chunk, already on a worker thread in `BuildCpu`. Adding the merge+weld there is a pure-CPU
  extension of an existing off-thread step, not a new pipeline.
- The source meshes are small kit meshes already loaded in memory. No new asset, no new file format or
  loader, no manifest field, no content-validation surface.
- An offline `ke-hlodbake` artifact would need ALL of: a manifest field, a baked-mesh file format + loader,
  content validation, a bake CLI, and manifest stamping - a lot of pipeline to precompute something that is
  cheap and deterministic to compute at boot (the spike merged+welded 41 props in microseconds of CPU).
- The streamer lifecycle already owns per-chunk load/unload with an off-thread CPU build and a frame-thread
  GPU apply. The HLOD mesh fits it exactly: merge+weld in `BuildCpu`, upload in `Apply`, free in `Unload`.
  The "cache" is the chunk handle itself - one merged mesh per loaded chunk per layer, rebuilt only on
  load / re-LOD / Invalidate, exactly like the terrain mesh, and stable across a pure tier/ring re-LOD
  (the placements are field-determined, so the coarse geometry does not change with the render tier).
- The door to offline is not closed: the library API `PropHlod.BuildMergedMesh(placements, sourceMeshes,
  weldCellSize)` is a pure function of its inputs, so it is the ready-made core of a future `ke-hlodbake`
  with zero rework if a continent-scale profile ever shows a per-boot merge cost that matters. Runtime-first
  defers that pipeline until it is needed, rather than building it speculatively.

**Cluster granularity: one chunk = one cluster (v1).** The library API is cluster-agnostic (it merges any
placement list), but the runtime applies it at chunk granularity because that aligns with the streamer's
load/unload unit and the per-chunk crossfade distance. Multi-chunk clustering (one merged mesh spanning an
NxN block) is a future option the same `PropHlod` API supports without change.

**Vertex-colour texturing.** The spike showed flat vertex-colour merged meshes are adequate at range, so
the merge source is `PropLoader.LoadProp`'s flat form, whose per-vertex colour already folds in each
material's alpha-weighted average albedo. The merged mesh therefore renders through the existing untextured
`Scene3D.Draw` vertex-colour path with zero extra texture memory, no atlas packer, and no new shader - the
one atlas-packer dependency the impostor would have forced is avoided on this side too. A baked albedo atlas
on the merged mesh is only worth reaching for if its texture fidelity ever proves necessary.

**Crossfade.** One merged mesh per chunk, so the swap is decided per chunk (chunk-centre distance), not per
placement. Across the crossfade band the props draw with a uniform `dissolveFloor` = t (added to
`PropRenderer`, combined with the L2 fade band by max) and the merged mesh draws at dissolve = 1 - t, so the
two complementary halves hand off through the 14.5.0 rigid-dissolve primitive with no new shader.

**Golden.** No committed per-backend reference grid was baked. Coverage is a non-golden `[GpuFact]`
(pixel-presence + `RenderFrameStats`, backend-agnostic, mirroring L2's `PropFadeBandGpuTests`): a 100-prop
cluster collapses from ~100 instances to one merged instance past the HLOD distance while screen coverage
holds. A `Golden`-named far-cluster test was not added because the cross-platform bake+verify loop
(decision 6) could not be closed in this session, and an assertion-only test needs no bake.
