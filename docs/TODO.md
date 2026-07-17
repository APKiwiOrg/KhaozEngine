# KhaozEngine TODO / follow-ups

Discovered follow-ups, known gaps, and consumer pulls. Not a tracked sprint and not the roadmap.

**TODO vs ROADMAP.** This file is the chip pile: things noticed in passing, gaps a release knowingly
left, pulls a game has asked for. [`ROADMAP.md`](ROADMAP.md) is the program list: anything that earns
its own design spec and its own release. If it needs a spec, it is a roadmap item. Otherwise it is a
TODO. Shipped detail lives in [`../CHANGELOG.md`](../CHANGELOG.md), so a resolved item is deleted here
rather than ticked and kept.

Anything discovered and not done belongs here **at the moment it is discovered**, before the finding
chat moves on. Open items are actioned at a checkpoint (current unit of work finished, before the
release ritual), never mid-task. Resolved entries are deleted by the release sweep. See the
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
  **Handed off:** branch `fix/vulkan-lavapipe-full-suite` (2026-07-17)
- [ ] **HDR chroma preservation is partial.** A saturated channel that clips at the display ceiling
  before the rescale still desaturates, even at `ChromaPreservation = 1`. Evidence: `CHANGELOG.md`
  11.7.0.
- [ ] **SunCycle has no visible moon disc and no secondary night key light.** Called out as deferred
  follow-ups. Evidence: `CHANGELOG.md` 10.124.0.
- [ ] **Map editor invalidation still falls back to full rebuild for several edit kinds.** Scatter-layer,
  companion, and terrain-scalar edits take the full-rebuild path rather than narrowed partial
  invalidation (exclusion and scatter-override edits were narrowed to partial rebuild in 11.4.0).
  Polygon override shapes stay MCP-authored and inspector-read-only, and the biome-band "Affects" row
  admits ground tinting is not yet wired. Evidence: `CHANGELOG.md` 10.119.0 "Scope cuts (by design this
  round)", 10.125.0 "Deferred out of this round", 11.4.0 "Biome-band editing honesty".
- [ ] **Cascaded shadow map gaps.** Terrain is receive-only and cannot cast, there are no alpha-tested
  cutout casters, and GPU-skinned casters stay opt-in and off by default pending a windowed A/B.
  Evidence: `CHANGELOG.md` 10.122.0 "Out of scope (unchanged)".
