# KhaozEngine roadmap

Future work only: what's planned or missing, highest-priority first. This file does NOT record shipped
history. See [CHANGELOG.md](../CHANGELOG.md) and `git tag` for what landed and when. When an item ships,
delete it from here (the detail moves to the changelog) rather than marking it "done".

Current released version: **10.112.0** (the shared `<KhaozEngineVersion>` line in `Directory.Build.props`).

Each near-term item gets its own design spec + plan when it is scheduled.

## Near-term (next up)

### 1. NPC navigation / pathfinding

Engine-owned pathfinding that respects terrain walkability (the walkable slice shipped in 10.x) plus prop and
static collision, so game NPC brains consume a path instead of hardcoded pursuit. Current behavior: NPCs chase
in straight lines and get stuck on every prop (playtest finding, 2026-07-14: Ruinborne wolves pinned behind
rocks). Scope question to record: grid A* over the walkable slice vs a pre-baked navmesh, decide at design time.
First consumer: Ruinborne wolves.

### 2. Physics: ragdolls and vehicles (pull-gated)

The joint foundation shipped in 10.30.0 (ball socket, hinge and slider with limits, distance, weld, plus
hinge/slider motors and servos and the distance winch on the `IPhysicsWorld` seam), on top of 10.29.0's
dynamic bodies, terrain geometry, and replication. What remains is deliberately pull-gated: ragdolls and
wheeled vehicles wait for a concrete game need, as do character-carrying moving platforms (a character does
not inherit platform velocity today). Recorded smaller follow-ups: velocity-based extrapolation and
quaternion bit-packing for replicated bodies, a per-frame re-clamp option for slider servo targets, and a
BufferPool-health churn assertion if Bepu ever exposes cheap introspection.

Height-gate the WalkableFloorUnderFeet support fan to accept support only near the paced feet height, so the
step-mount cap-vs-seat discriminator stops relying on approach floors being analytic. This prevents edge cases
where deep risers on physics floor slabs stall slow-walk mounts. No shipped geometry has this combination yet,
but the stair tests cannot catch it since all approach floors are analytic.

Open, still-unfixed: a WALK on a ONE-SIDED triangle-mesh staircase (thin riser/tread quads, no solid body)
sticks at the base - the capsule mounts one riser then never advances, identically before and after the
bottom-stair tread-find fix (10.71.1). It is a separate mechanism: a horizontal-advance blockage on the thin
one-sided riser, orthogonal to the vertical-support tread-find fix (which corrects the support HEIGHT, not the
forward push), so that fix neither covers nor regresses it. No shipped consumer geometry hits this - the
consumers' stairs are convex boxes, which the tread-find fix fully covers - so it is recorded here rather than
patched. Documented in the `ConsumerStairBaseMountTests` header.

SurfaceGradeAhead complex-geometry robustness (stair-glide signal, deferred). The continuous-run detection that gates
the climb signal reads the local grade from a small forward ray fan (`SurfaceGradeAhead`). On a clean box/convex
staircase it is stable, but on complex geometry near the run - railings, corner posts, a balustrade, an adjacent wall
lip - a probe ray can graze a non-tread surface and flip the detection off for a tick, which flickers the signal and
can make the render glide disengage/re-engage (a brief hitch). No shipped consumer stair has this (the consumer
`TestStaircase` and dungeon stairs are convex boxes with clear approaches), so it is recorded here rather than
hardened now. When it matters, options are a more robust surface classifier (reject rays whose hit normal or height is
inconsistent with the run) or a short hysteresis on the run-detected flag so a one-tick probe miss does not drop it.

Gate the up-tilted-lip step-up on elevation above the current SUPPORT floor (props included), not analytic
terrain alone. The lip step-up widened in the short-riser/tread-lip fix keys its near-floor gate off the
analytic terrain height only (`StepUpEligible`), so a short lip sitting on TOP of a prop platform more than a
StepHeight above terrain still fails the gate and dead-stalls. Pre-existing behaviour, narrowed by the widening
rather than regressed (the near-vertical band is unaffected), but the proper fix is to track the capsule's
current support height including props and gate against that. Documented in the `StepUpEligible` doc comment.

### 3. Map editor (design approved 2026-07-09, in flight)

The world-document program: `KhaozEngine.MapDoc` (zone document format: terrain config, authored
placements, scatter exclusions and overrides, spawns, regions), the `KhaozEngine.MapEditor` in-engine
GUI runtime plus per-game editor heads, and the `ke-mapedit` MCP server so AI can edit maps with
world-aware queries and renders. Ruinborne is the first adopter. Four phases, MapDoc first, GUI and
MCP in parallel after it. Full design: [MAP-EDITOR-DESIGN.md](MAP-EDITOR-DESIGN.md). Items deferred
by review across the program's phases are tracked in that doc's Deferred follow-ups section, not
duplicated here.

## Netcode / MMO refinements

- Delta bit-packing / quantization: shrink per-delta bandwidth further (on top of the AoI delta path).
- Phase 2 reliability: unreliable-sequenced delta channel with acked-baseline rebuild (opt-in, replacing the
  current reliable-ordered phase 1).
- SpaceGame as the first netcode adopter / testbed: validate the authoritative stack on a real game.
- Instanced world spaces (recorded 2026-07-10): per-group dungeon/zone instances as a first-class
  NetWorld/Sharding concept: instance lifecycle (spin up / tear down), portals or entry/exit transfer,
  replication and persistence scoped per instance. End state for dungeons: `KhaozEngine.Dungeon` ships
  today against the far-corner same-grid model, and its dungeon-local output (one placement transform,
  no world-absolute assumptions) adopts this when it lands.
## Overworld / world content

- Animated-creature adoption (game-side, not engine work): the engine animation stack shipped (glTF
  animation-clip playback, `AnimatedCharacter` + locomotion blend, `ReplicatedCharacterAnimators`). SpaceGame's
  2.5D rigged-creature direction can adopt it directly. Only reopen an engine item here if a concrete new gain
  surfaces from that adoption (e.g. blend trees or IK the current player can't express: additive layers and
  bone masks shipped in 10.31.0's `LayeredAnimator`).

## Rendering

**Graphics target (recorded 2026-07-07):** KhaozEngine is a 3D engine first. Ruinborne is the flagship 3D
consumer and sets the bar: "A"-tier semi-realistic fidelity with a light stylization accent, deliberately not
AAA. The other games consume the same stack for 2D/2.5D. Consequences: the cel/palette/retro post path stays a
per-game option, not the engine identity, and the items below are ordered by what closes the gap to that
"A" semi-realistic bar. Explicitly out of scope for that bar (do not build without a concrete game pull): TAA,
global illumination, a deferred renderer, full PBR+IBL, occlusion culling, GPU-driven rendering, GPU particles.

Ordered gap list (2026-07-07 feature audit):

1. Shadow polish (the ShadowMode tier shipped in 10.19.0: blob + key-light PCF map, model and terrain both
   receive, model-only casting): remaining follow-ups when a game pulls for them - terrain self-shadowing /
   terrain-as-caster, cascaded shadow maps for larger view distances, per-game bias tuning beyond the
   small-scene defaults, and a runtime-resizable shadow map (resolution is a construction-time knob today).
2. Cross-pass transparent ordering (follow-up to the shipped within-batch sort, 10.18.2): alpha-blended draws
   now sort back-to-front within each batch, but the inter-pass order of the transparent renderers is still
   fixed by Scene3D. A unified cross-renderer transparent queue is the eventual answer if a game hits a
   cross-pass ordering artifact. Also unpinned: a golden for mixed additive+alpha textured overlap.
3. Culling follow-ups (frustum culling shipped in 10.20.0: chunks AABB-tested, instanced props sphere-tested,
   skinned meshes and the shadow pass deliberately exempt): light-volume culling for the shadow depth pass. A
   coarse spatial culling index (a chunk/region grid, broad-phase before the per-instance sphere/AABB test) once
   per-frame instance counts grow past linear-scan comfort - today every queued instance and skinned draw is
   tested against the frustum in one flat pass each frame, fine at current scene sizes but a candidate for a
   grid/quadtree broad-phase if a much larger streamed world pushes per-frame counts up.
4. Sky follow-ups (the gradient + key-light-aligned sun disc background shipped in 10.20.0, a screen-space
   formulation that works under both ortho and perspective cameras): a physical point-at-infinity sun
   projection for perspective cameras, and a full cubemap/skybox when water or a specific scene pulls for it.
5. Water follow-ups (the animated water surface shipped in 10.28.0: `Scene3D.DrawWater` + `WaterSettings`,
   shore fade, sky-derived fresnel tint, sun glint, no reflections): shore foam, per-game wave tuning once
   Ruinborne adopts it, dropping the unused `Res` UBO field, and a water-footprint-scoped golden guard.
6. Bloom follow-ups (the LDR threshold + separable-blur bloom shipped in 10.27.0, opt-in via
   `PixelPostProcessSettings.Bloom`): per-game tuning of threshold/intensity defaults once Ruinborne adopts it,
   and a second blur octave only if a game pulls for wider halos. A full HDR/tonemap pipeline is still not planned.
7. Animation follow-ups (layered blending shipped in 10.31.0: `LayeredAnimator` with bone masks, override and
   local-frame additive layers, and one-shot or held actions via `PlayAction` on `AnimatedCharacter`): skeletal IK (foot
   placement) waits for adoption feedback, action-trigger replication stays a game-message pattern, and a
   per-layer sync-group mechanism (matching walk/run phase across layers) only if combat blending pulls for it.
8. Reflections / environment probes: not planned for the current bar. Revisit only if water or a specific scene
   demands more than the sky provides.

Also here, unchanged:

- Material / custom-shader seam (deliberately deferred, decision 2026-07-07): the Render3D shader set stays
  closed (`ShaderSources` internal, renderers internal sealed) until a game concretely needs a custom surface
  shader. Trigger: the first game requirement that cannot be met by material params on the existing shaders.
  Design space to weigh then: fragment-snippet injection vs whole-pipeline registration vs a material graph.
  Groundwork already shipped: the composed `LightingCommonGlsl` block (10.17.1) and the public
  `KhaozEngine.Gpu.ShaderValidation` (10.17.0) mean a future seam gets single-sourced lighting and device-free
  validation for consumer-authored shaders for free.

- Stair-glide EWMA resets across a mid-climb shard handoff (10.75.0 follow-up): the ascent climb signal's
  smoothing average (`ClimbRateEwma`) is sim-local and deliberately absent from the `ReplicationChannels.Migrate`
  capture, so a player crossing a cell boundary WHILE climbing a stair run re-seeds it to 0 on the new shard and
  the exported `ClimbRateQ` re-converges over the EWMA time constant. That is a ~0.2 s dip in the remote's
  rendered glide right at the boundary (content-dependent - needs a stair straddling a cell edge - and cosmetic:
  the authoritative position and the wire `ClimbRateQ` itself are unaffected, only the smoothed feed-forward
  dips). Fix when a real straddling-stair layout surfaces it. Candidates: carry the EWMA on a Migrate-only
  channel (survives the handoff, still off the wire), or re-seed it from the decoded `ClimbRateQ` in
  `PlayerMovementSystem` when `Ewma == 0 && ClimbRateQ != 0` (mirror of the reconcile seed, no migrate change).

## Cross-platform reach

- Mobile: iOS / Android platform layers (lifecycle, touch, packaging, store submission). Silk.NET is the
  windowing/input foundation it builds on.
- GL backend verification: the `gl` Veldrid backend override parses but is unverified; waits on real GPU CI /
  hardware. See [CROSS-PLATFORM.md](CROSS-PLATFORM.md).
- Live gamepad smoke: polling is best-effort + compile-verified; needs an on-device pass with a physical
  controller.
- Richer text entry: IME / locale / dead-keys (the current `TextEntry` is US-layout key-mapping).
- Text shaping / RTL (localization-completeness risk, deferred): glyph layout is left-to-right advance-based
  raster atlas with no BiDi or complex-script shaping. Localization is a founding principle, so this becomes a
  hard blocker the day a target language needs it (Arabic, Hebrew, Indic scripts). No work until such a target
  exists, but the gap is recorded here so it is a known decision, not a surprise.

## Input, audio, and game feel (2026-07-07 feature audit)

- Rumble follow-up (the `IRumble` seam + pulse envelopes shipped in 10.21.0, compile-verified): the GLFW input
  backend enumerates zero vibration motors, so rumble is a graceful no-op today. Revisit when a motor-capable
  input backend (SDL) lands, plus an on-device feel pass and a dirty-flag skip for per-frame motor writes.
- Audio follow-ups (music crossfade + SFX buses shipped in 10.22.0): a true two-stream crossfade needs an
  `IMusicBackend` seam upgrade (today it is fade-out/switch/fade-in on the single stream), live re-gain of
  already-playing bus voices needs a per-voice handle on `ISfxBackend` (today bus volume applies on next play),
  and combat music layers ride on the crossfade once games pull for them.
- Rich text markup: `TextLayout` does wrap + alignment only. Inline color/style markup (localized strings
  need it for emphasis and key names) for chat, tooltips, and dialogue.

## Tooling & developer experience

- `.resx` -> `StringId` source generator: compile-time localization enforcement shipped (`StringId` /
  `LocalizedText` + the `Localization.Analyzers` analyzer), but consumers still hand-author the `StringId`
  constants that mirror their `.resx` keys. A Roslyn source generator could emit those constants from the
  `.resx` at build time, removing the hand-maintained constants class and the key-drift risk.
- True GPU timestamps (follow-up to the per-pass CPU encode timings shipped in 10.24.0): Veldrid 4.9.0 exposes
  no timestamp-query API (only fences), so per-pass numbers today measure CPU command-encoding time, not GPU
  execution. Revisit when a Veldrid upgrade (or replacement) surfaces timestamp queries.
- Asset hot-reload: reload meshes, textures, and shaders at runtime during development. The prop asset pipeline
  shipped, but hot-reload did not.
- Shader bytecode disk cache: every shader is compiled from source (SPIRV-Cross via Veldrid.SPIRV) at every boot
  today, no persisted compiled-bytecode cache. Worth adding only once boot time becomes a measured pain point
  (not profiled yet). The cache key would need the engine version + GPU backend + shader source hash so a
  version bump or backend switch can never serve stale bytecode.

## Possible future factoring (unscheduled)

- Shared 2D/3D particle sim core: `KhaozEngine.Particles` (3D) and `Render2D.Vfx.Particle2DSystem` (2D) share
  the emit/integrate/lerp-over-life model; a pass could factor the common sim core. No single target today.
- Nullwake camera convergence: Nullwake's `OreField.RefToScreen` is a non-uniform scale into a screen sub-rect,
  not `Camera2D`. Converging would need sub-viewport + non-uniform-scale support in the engine camera, else it
  stays game-specific.

## Explicitly last priority (deferred by design)

- **Close-range texture fidelity via detail maps.** Kit source textures are 512x512 and blur when magnified on camera (playtest finding, 2026-07-14). Detail maps or a triplanar micro-detail overlay for props and terrain provide the engineered alternative to sourcing higher-res art.

- **Per-foot ray-probe IK in the animation bridge.** The AAA richness layer OVER the smooth root the presentation
  smoothers already produce (the signal-driven stair glide + the UE-style discrete-step mesh offset): per-foot
  downward ray probes that place each ankle on the TRUE geometry under it, offset the pelvis toward the LOWER
  foot so the hips sit level on a slope/stair, and LOCK a planted foot through its stance phase (no sliding).
  Reusable across all KE games and needs NO per-stair clips - the ground query drives the pose. **Deferred by
  design until the current smoothing layers are validated in prod.** Foot IK refines where each foot LANDS; the
  glide + step-offset already fix how the ROOT reads on stairs and steps, and stacking foot IK before that root
  motion is proven in the field would tune two coupled layers against a moving target. Lowest priority: schedule
  it only once the root-motion smoothing is confirmed good in a shipped build, then it is a self-contained bridge
  upgrade (a new `CharacterAnimatorTuning` block + a ground-probe delegate), not a rework of anything below it.
