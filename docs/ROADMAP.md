# KhaozEngine roadmap

Future work only: what's planned or missing, highest-priority first. This file does NOT record shipped
history. See [CHANGELOG.md](../CHANGELOG.md) and `git tag` for what landed and when. When an item ships,
delete it from here (the detail moves to the changelog) rather than marking it "done".

Current released version: **10.57.0** (the shared `<KhaozEngineVersion>` line in `Directory.Build.props`).

Each near-term item gets its own design spec + plan when it is scheduled.

## Near-term (next up)

### 1. Physics: ragdolls and vehicles (pull-gated)

The joint foundation shipped in 10.30.0 (ball socket, hinge and slider with limits, distance, weld, plus
hinge/slider motors and servos and the distance winch on the `IPhysicsWorld` seam), on top of 10.29.0's
dynamic bodies, terrain geometry, and replication. What remains is deliberately pull-gated: ragdolls and
wheeled vehicles wait for a concrete game need, as do character-carrying moving platforms (a character does
not inherit platform velocity today). Recorded smaller follow-ups: velocity-based extrapolation and
quaternion bit-packing for replicated bodies, a per-frame re-clamp option for slider servo targets, and a
BufferPool-health churn assertion if Bepu ever exposes cheap introspection.

### 2. Visual fidelity (textures + materials)

Terrain PBR splat and per-prop albedo/normal/roughness materials (`PropLoader.LoadPropWithMaterial`) have
landed. Goal: make props, trees, and buildings actually look good, not just read as shapes.

- Real Quaternius kit re-ingest with textures on (today's samples use the `PropMaterialPresets.Procedural`
  preset or opt in per-asset via the `textured` manifest flag), plus multi-texture-per-primitive support.
- CC0-asset-friendly throughout (ambientCG terrain textures, the kit textures), no new heavy dependencies.

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
- Visual fidelity (real Quaternius kit re-ingest with textures on): see Near-term item #2 above.

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
   skinned meshes and the shadow pass deliberately exempt): light-volume culling for the shadow depth pass,
   and culling for skinned characters if crowd profiling ever shows a win (needs animated-pose-safe bounds).
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
8. GPU skinning: skinning is CPU-side (deliberate: a Metal per-instance-attribute bug killed the old GPU path,
   and the dynamic-offset UBO workaround is known). Only worth reopening at MMO crowd scale - measure CPU skinning
   cost with real crowds first, do not build speculatively.
9. Reflections / environment probes: not planned for the current bar. Revisit only if water or a specific scene
   demands more than the sky provides.

Also here, unchanged:

- Material / custom-shader seam (deliberately deferred, decision 2026-07-07): the Render3D shader set stays
  closed (`ShaderSources` internal, renderers internal sealed) until a game concretely needs a custom surface
  shader. Trigger: the first game requirement that cannot be met by material params on the existing shaders.
  Design space to weigh then: fragment-snippet injection vs whole-pipeline registration vs a material graph.
  Groundwork already shipped: the composed `LightingCommonGlsl` block (10.17.1) and the public
  `KhaozEngine.Gpu.ShaderValidation` (10.17.0) mean a future seam gets single-sourced lighting and device-free
  validation for consumer-authored shaders for free.

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

## Possible future factoring (unscheduled)

- Shared 2D/3D particle sim core: `KhaozEngine.Particles` (3D) and `Render2D.Vfx.Particle2DSystem` (2D) share
  the emit/integrate/lerp-over-life model; a pass could factor the common sim core. No single target today.
- Nullwake camera convergence: Nullwake's `OreField.RefToScreen` is a non-uniform scale into a screen sub-rect,
  not `Camera2D`. Converging would need sub-viewport + non-uniform-scale support in the engine camera, else it
  stays game-specific.
