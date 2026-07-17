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

- [ ] **Void/plane fallback for ground decals.** Ground decals reconstruct their surface from the scene
  depth buffer, so they only paint where geometry exists. A large tower range ring truncates where it
  overhangs the void past the mesa edge. Needs an opt-in per-decal fallback projecting onto the virtual
  plane at the decal's own Y where there is no geometry. Design already exists as "release 2" of
  `docs/BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md` on branch `feature/background-pass`,
  design-only, zero implementation. Requested by Hardpoint, which tracks the reciprocal entry in its
  `docs/TODO.md` "Engine candidates" section (Hardpoint commit `b419a443`).
  **Handed off:** Hardpoint `docs/TODO.md` "Void/plane fallback for ground decals" (2026-07-17)
- [ ] **BakeOverworld overload that sweeps an IPhysicsWorld directly.** Ruinborne hand-rolls a
  physics-probe surface provider (`RuinborneNavSurfaceProvider`) because its props are physics-only Bepu
  statics. An overload sweeping `IPhysicsWorld` directly is useful to any physics-obstacle game.
  Recorded and deliberately not centralized at the time. Evidence: Ruinborne
  `docs/ENGINE-INTEGRATION.md`, "Not centralized (recorded)" note in the NPC follower-integration
  section.
## Known gaps

- [ ] **Vulkan/lavapipe full-suite crash, mode 2 (use-after-free).** `UnloadTexture` disposes GPU
  resources while an unfenced staging upload is still queued. Metal and WARP absorb it silently,
  lavapipe segfaults. A prototype fix (`IGpuDevice.DisposeWhenIdle` across 11 disposal sites in
  Render2D/Render3D, including render-target resize and SpriteBatch set eviction) was validated only in
  a throwaway container and does not exist in the repo. Mode 1 (concurrent device create racing the
  Vulkan loader) is fixed on branch `fix/vulkan-lavapipe-full-suite` (commit `0b7831af`). That branch
  also carries commit `4f056153` widening the Vulkan CI leg to the full-suite tier, which would
  reintroduce the flake if merged before mode 2 is fixed.
  **Blocked on:** branch `fix/vulkan-lavapipe-full-suite` (2026-07-17)
- [ ] **Ground decals wrap down a sharp edge's vertical face (legacy, unflagged decals).** The Y-band gate is
  `[Center.Y - YTolerance, Center.Y + MaxStep]` and `GroundTelegraphs` hardcodes `YTolerance = 0.3`, so the TOP
  0.3 of any vertical face is inside the band and the decal conforms ONTO it, evaluated at that face's XZ
  (pinned at the edge) instead of the decal's own. A range ring overhanging a mesa visibly drips down the cliff.
  The gate's stated purpose is "conform to terrain, not walls" (`DecalFrag`), which it fails at the top of a
  wall: at one pixel, with only depth, a terrain dip 0.3 below the plane and the top 0.3 of a cliff face are
  arithmetically identical. The geometric normal is the only signal that separates them. 11.10.0 fixed this for
  `VoidFallback` decals ONLY (normal-gated, so the release stayed zero-neutral for everyone else) and left the
  legacy path alone: `GroundDecalRenderer` now binds `NormalTex`, so making it universal is a shader-side
  `if` plus a golden rebake sweep, but it CHANGES existing decal rendering for every consumer and so wants its
  own release and its own windowed A/B. Reproduced with the fallback off during 11.10.0; the `GroundNormalMinY`
  0.5 threshold (60-degree slopes still count as ground) is the constant to reuse. Evidence: `CHANGELOG.md`
  11.10.0, `KhaozEngine.Tests/Gpu/GroundDecalVoidGoldenTests.Golden_void_fallback_keeps_the_disc_flat_across_a_cliff_face`
  (its legacy-delta control measures exactly this artifact).
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
