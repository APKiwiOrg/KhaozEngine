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

- [ ] **The release-tag guard accepts a malformed doubled message.** `scripts/tag-release.sh` takes
  `<area> <summary...>` as separate args, but its own header docs describe the canonical tag as
  `area(version): summary`, so passing that literal string as ONE arg is the obvious mistake. It then
  assembles `"$area($ver): $summary"` with `summary` defaulted from the HEAD subject, producing e.g.
  `render(12.1.0): shadow cascades(12.1.0): integrate feature/...`. `tag_msg_ok` in
  `scripts/tag-standard.sh` does not catch it: its regex `^[a-z0-9][a-z0-9._-]*\(<ver>\): .+$` matches
  the `render(` prefix and lets `.+$` swallow the second embedded `(<ver>):` segment. `tag_msg_ok` is
  shared by `.githooks/pre-push` and `tag-release.sh`, so the hole is on both paths. Repro, no tags
  created: `. scripts/tag-standard.sh && tag_msg_ok "render(1.2.3): x on engine 9.9.9(1.2.3): y" "1.2.3"`
  exits 0 (accepted). Hit live cutting Ruinborne v0.8.3 on 2026-07-17 (caught by eye, nothing shipped).
  Fixed in game-template on 2026-07-17 (commit `77d14c5`, `APKiwiOrg/game-template`) and propagated to
  the 4 game repos: reject an `area` arg containing `(`, `)`, `:` or whitespace, and reject a summary
  carrying a second `(<ver>): ` segment (pinned to the actual version, so `fix the thing (again)` stays
  legal). The engine carries its OWN copy of both scripts (byte-identical to the template's pre-fix
  version apart from the correctly-absent "Template-managed" header) and is outside the game-repo
  verbatim manifest, so the template propagation does NOT reach it and the bug is still live here. Port
  the same two-layer fix, which needs its own version bump, `CHANGELOG.md` entry and tag per the release
  ritual. Recorded 2026-07-17.

- [ ] **Showcase 3D overworld room is not a shadow/day-night testbed.** The 12.0.0 frustum-slice
  shadow rework had to be visually verified without a representative showcase scene: the 3D overworld
  demo room enables no shadows (`ShadowMode` stays `Off`), has no staircase or multi-level geometry
  (the exact stair-climb case that surfaced the old cascade square-seam artifact in Ruinborne), and
  runs no day/night cycle. Extend the room to enable `ShadowMode.ShadowMap` with the engine defaults,
  add a walkable staircase plus a tree line near a cascade hand-off distance, and drive the sun with
  the same `SunCycle` mapping Ruinborne uses so a moving key light exercises per-frame cascade refits
  and the dirty-skip path. Makes the showcase the one-click windowed verification surface for every
  future shadow/lighting change. Recorded 2026-07-17 from the 12.0.0 release retro.

- [ ] **Vulkan/lavapipe full-suite crash, mode 2 (use-after-free).** `UnloadTexture` disposes GPU
  resources while an unfenced staging upload is still queued. Metal and WARP absorb it silently,
  lavapipe segfaults. A prototype fix (`IGpuDevice.DisposeWhenIdle` across 11 disposal sites in
  Render2D/Render3D, including render-target resize and SpriteBatch set eviction) was validated only in
  a throwaway container and does not exist in the repo. Mode 1 (concurrent device create racing the
  Vulkan loader) is fixed on branch `fix/vulkan-lavapipe-full-suite` (commit `0b7831af`). That branch
  also carries commit `4f056153` widening the Vulkan CI leg to the full-suite tier, which would
  reintroduce the flake if merged before mode 2 is fixed.
  **Blocked on:** branch `fix/vulkan-lavapipe-full-suite` (2026-07-17)
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
