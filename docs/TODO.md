# KhaozEngine TODO / follow-ups

Discovered follow-ups, known gaps, and consumer pulls. Not a tracked sprint and not the roadmap.

**TODO vs ROADMAP.** This file is the chip pile: things noticed in passing, gaps a release knowingly
left, pulls a game has asked for. [`ROADMAP.md`](ROADMAP.md) is the program list: anything that earns
its own design spec and its own release. If it needs a spec, it is a roadmap item. Otherwise it is a
TODO. Shipped detail lives in [`../CHANGELOG.md`](../CHANGELOG.md), so a resolved item is deleted here
rather than ticked and kept.

Anything discovered and not done belongs here **at the moment it is discovered**, before the finding
chat moves on. Open items are actioned at a checkpoint (the moment you are about to end your turn and
report back), never mid-task. Resolved entries are deleted by the release sweep. See the
"Discovered work" section in [`../AGENTS.md`](../AGENTS.md) for the full lifecycle and the entry format.

## Consumer pulls (game-requested)

- [ ] **BakeOverworld overload that sweeps an IPhysicsWorld directly.** Ruinborne hand-rolls a
  physics-probe surface provider (`RuinborneNavSurfaceProvider`) because its props are physics-only Bepu
  statics. An overload sweeping `IPhysicsWorld` directly is useful to any physics-obstacle game.
  Recorded and deliberately not centralized at the time. Evidence: Ruinborne
  `docs/ENGINE-INTEGRATION.md`, "Not centralized (recorded)" note in the NPC follower-integration
  section.
## Known gaps

- [ ] **Showcase 3D overworld room is not a shadow/day-night testbed.** The 12.0.0 frustum-slice
  shadow rework had to be visually verified without a representative showcase scene: the 3D overworld
  demo room enables no shadows (`ShadowMode` stays `Off`), has no staircase or multi-level geometry
  (the exact stair-climb case that surfaced the old cascade square-seam artifact in Ruinborne), and
  runs no day/night cycle. Extend the room to enable `ShadowMode.ShadowMap` with the engine defaults,
  add a walkable staircase plus a tree line near a cascade hand-off distance, and drive the sun with
  the same `SunCycle` mapping Ruinborne uses so a moving key light exercises per-frame cascade refits
  and the dirty-skip path. Makes the showcase the one-click windowed verification surface for every
  future shadow/lighting change. Recorded 2026-07-17 from the 12.0.0 release retro.

- [ ] **Ground decals wrap down a sharp edge's vertical face (legacy, unflagged decals).** The Y-band gate is
  `[Center.Y - YTolerance, Center.Y + MaxStep]` and `GroundTelegraphs` hardcodes `YTolerance = 0.3`, so the TOP
  0.3 of any vertical face is inside the band and the decal conforms ONTO it, evaluated at that face's XZ
  (pinned at the edge) instead of the decal's own. A range ring overhanging a mesa visibly drips down the cliff.
  The gate's stated purpose is "conform to terrain, not walls" (`DecalFrag`), which it fails at the top of a
  wall: at one pixel, with only depth, a terrain dip 0.3 below the plane and the top 0.3 of a cliff face are
  arithmetically identical. The geometric normal is the only signal that separates them. 12.1.0 fixed this for
  `VoidFallback` decals ONLY (normal-gated, so the release stayed zero-neutral for everyone else) and left the
  legacy path alone: `GroundDecalRenderer` now binds `NormalTex`, so making it universal is a shader-side
  `if` plus a golden rebake sweep, but it CHANGES existing decal rendering for every consumer and so wants its
  own release and its own windowed A/B. Reproduced with the fallback off during 12.1.0. The `GroundNormalMinY`
  0.5 threshold (60-degree slopes still count as ground) is the constant to reuse. Evidence: `CHANGELOG.md`
  12.1.0, `KhaozEngine.Tests/Gpu/GroundDecalVoidGoldenTests.Golden_void_fallback_keeps_the_disc_flat_across_a_cliff_face`
  (its legacy-delta control measures exactly this artifact).
- [ ] **`GroundTelegraphs.BuildResidueCircle` drops `VoidFallback` / `VoidDim`.** It composes its `GroundDecal`
  directly instead of through `Base()`, so a residue mark whose style opted into the void fallback still truncates
  at an island's edge while every other `Ground*` shape projects. Deliberate for 12.1.0 (a scorch mark is a mark ON
  ground, so projecting it into the void is not obviously wanted) and documented on the builder, but the asymmetry
  is a trap: the flag is on the shared `TelegraphStyle` and silently does nothing here. Either route the builder
  through `Base()` or split residue onto its own style type. Evidence: `CHANGELOG.md` 12.1.0,
  `KhaozEngine.Telegraphs.Render3D/GroundTelegraphs.cs` `BuildResidueCircle`.
- [ ] **HDR chroma preservation is partial.** A saturated channel that clips at the display ceiling
  before the rescale still desaturates, even at `ChromaPreservation = 1`. Evidence: `CHANGELOG.md`
  11.7.0.
- [ ] **Map editor invalidation still falls back to full rebuild for several edit kinds.** Scatter-layer,
  companion, and terrain-scalar edits take the full-rebuild path rather than narrowed partial
  invalidation (exclusion and scatter-override edits were narrowed to partial rebuild in 11.4.0).
  Polygon override shapes stay MCP-authored and inspector-read-only, and the biome-band "Affects" row
  admits ground tinting is not yet wired. Evidence: `CHANGELOG.md` 10.119.0 "Scope cuts (by design this
  round)", 10.125.0 "Deferred out of this round", 11.4.0 "Biome-band editing honesty".
- [ ] **Cascaded shadow map gaps.** Terrain is receive-only and cannot cast, there are no alpha-tested
  cutout casters, and GPU-skinned casters stay opt-in and off by default pending a windowed A/B.
  Evidence: `CHANGELOG.md` 10.122.0 "Out of scope (unchanged)".
- [ ] **The golden grid is blind to fine, sparse detail. It cannot see the starfield at all.**
  `GoldenCompare` downsamples each render to a 32x18 grid of AVERAGED rgb per cell and compares with a
  0.06/channel tolerance (`GoldenGrid.DefaultTolerance`). A star contributes only about 0.012 to a cell
  average, five times under tolerance. Proven during 11.9.0: with `_starfield.Draw` commented out, so the
  engine renders NO starfield whatsoever, `telegraph_ground` and `scene3d` still PASS. The grid is
  deliberately coarse (it exists to catch gross shader / UBO / blend / winding regressions while
  tolerating driver noise), so this is not a defect in itself, but it means any sparse or fine-detail
  feature has zero golden coverage and needs its own raw-pixel test. `StarfieldGpuTests` is now the only
  net for the starfield. Worth auditing which OTHER features believe they are golden-covered but are not.
  Evidence: `CHANGELOG.md` 11.9.0, `docs/BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md`.
- [ ] **`StarfieldGpuTests` box-coverage guard proves "mostly covered", not fully.** The guard added in
  11.9.0 asserts the box's centre block is meaningfully brighter than the clear colour before trusting
  the cross-mode byte-identity diff, which closes the vacuous-pass hole. A reviewer noted it still only
  establishes the block is mostly covered by geometry, not wholly. Low value, recorded for honesty.
  Evidence: `CHANGELOG.md` 11.9.0.
- [ ] **Rebake the drifted direct3d11/vulkan goldens before the tolerance margin runs out.** A
  `workflow_dispatch bake=true` run of `cross-platform-gpu.yml` (run 29567466645, 2026-07-17, on the
  frustum-slice shadow branch after merging main at 11.9.0) showed 8 scenes whose direct3d11/vulkan
  goldens have accumulated whole-frame drift versus their checked-in grids that is NOT attributable to
  the shadow rework: `scene3d`, `scene3d_fill`, `scene3d_hdr_off`, `scene3d_splat`,
  `scene3d_splat_distance`, `scene3d_textured`, `telegraph_ground`, `telegraph_modern`. 636-1641 of 1728
  cells changed, max deltas 0.046-0.058 against the 0.06 verify tolerance. Likely source is the 11.9.0
  background-pass release (or residual 11.7.0 chroma-tonemap drift) shipping without a CI-backend rebake
  because it stayed sub-tolerance. The verify legs still pass today, but the margin is nearly consumed:
  any further sub-tolerance change turns main red on those scenes. Task: confirm the drift source from
  the relevant releases' bake commits, run a fresh `cross-platform-gpu.yml bake=true` on current main,
  eyeball the bake evidence PNGs per scene (repo rule), then commit the rebaked direct3d11/vulkan
  goldens with a message attributing the drift. Goldens-only change, no version bump needed.
- [ ] **Audit whether the golden tests are actually valid, robust, and useful.** The grid-blindness
  entry and the backend-drift entry above are two symptoms of the same unexamined question: what do the
  goldens really prove? Known so far: the 32x18 averaged grid cannot see sparse detail at all (a
  fully deleted starfield still passes), and whole-frame drift accumulates silently right up to the
  0.06 tolerance so a "green" leg can be one small change away from red. Wanted: a first-principles
  review of the mechanism, not a patch. Is per-cell averaging with a fixed absolute tolerance the right
  comparison? Should tolerance be per-backend, or a structural/perceptual metric instead of a mean? How
  many scenes would still pass with their feature-under-test deleted (the starfield experiment
  generalized)? Does the suite catch anything the raw-pixel GPU tests do not? Is the per-backend rebake
  ritual masking real regressions as "driver noise"? Outcome should be a written verdict on what the
  goldens are for and what tier of test covers what, whether or not the mechanism changes.
