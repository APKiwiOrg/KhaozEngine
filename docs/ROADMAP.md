# KhaozEngine roadmap

Future work only: what's planned or missing, highest-priority first. This file does NOT record shipped
history. See [CHANGELOG.md](../CHANGELOG.md) and `git tag` for what landed and when. When an item ships,
delete it from here (the detail moves to the changelog) rather than marking it "done".

Current released version: **10.18.3** (the shared `<KhaozEngineVersion>` line in `Directory.Build.props`).

Each near-term item gets its own design spec + plan when it is scheduled.

## Near-term (next up)

### 1. Physics engine: dynamic bodies + constraints

The static-body physics seam (`IPhysicsWorld` + the opt-in `KhaozEngine.Physics.Bepu` backend) is in place,
and character movement collide-and-slides capsule-vs-mesh against it. What remains:

**Dynamic rigid bodies + their replication.** A body that falls / bounces / rests needs a replication component
(like `MovementState` for players); `Scene3DChunkSink` would drive `AddDynamic`/step per loaded chunk, and
`WorldClient` would interpolate dynamic-body positions from replicated snapshots. Enables physics-driven crates,
barrels, falling debris. Terrain-as-physics-geometry (the whole terrain mesh fed into a `TriangleMesh` body) also
lands here: a static terrain body replaces the `TerrainCollision` delegate in the `Step` call so all surfaces
(terrain + props + buildings) share one query path.

**Constraints, joints, and vehicles.** Hinges, sliders, ragdolls, wheeled vehicles.

### 2. Visual fidelity (textures + materials)

The terrain now renders PBR splat textures. Props can now carry albedo/normal/roughness surface detail too
(`PropLoader.LoadPropWithMaterial` reads a prop glTF's textures, opt-in via the `textured` manifest flag, and a
prop with no textures still degrades to the flat render). Goal: make props, trees, and buildings actually look
good, not just read as shapes.

- ~~Textured props~~ landed: `PropLoader.LoadPropWithMaterial` reads a prop's baseColor/normal/roughness
  textures instead of flattening them to a base-colour factor. `MeshOps.WithTangents` gives a UV-mapped
  primitive mesh a tangent basis so normal maps take effect, and `PropMaterialPresets.Procedural` generates an
  asset-free mossy-stone albedo+normal for samples and tests. Remaining: real Quaternius kit re-ingest with
  textures on (today's samples use the procedural preset or opt in per-asset), and multi-texture-per-primitive.
- Water: a real water shader for the lake / sea (currently a flat plane at the water level).
- Lighting polish: pairs with shadows (see Rendering) + an HDRI/sky direction for a cohesive look.
- CC0-asset-friendly throughout (ambientCG terrain textures, the kit textures), no new heavy dependencies.

## Netcode / MMO refinements

- Delta bit-packing / quantization: shrink per-delta bandwidth further (on top of the AoI delta path).
- Phase 2 reliability: unreliable-sequenced delta channel with acked-baseline rebuild (opt-in, replacing the
  current reliable-ordered phase 1).
- SpaceGame as the first netcode adopter / testbed: validate the authoritative stack on a real game.

## Overworld / world content

- Procedural dungeon generator.
- Animated-creature adoption (game-side, not engine work): the engine animation stack shipped (glTF
  animation-clip playback, `AnimatedCharacter` + locomotion blend, `ReplicatedCharacterAnimators`). SpaceGame's
  2.5D rigged-creature direction can adopt it directly. Only reopen an engine item here if a concrete new gain
  surfaces from that adoption (e.g. blend trees, additive layers, or IK the current player can't express).
- Visual fidelity (textured props, water): see Near-term item #2 above.

## Rendering

**Graphics target (recorded 2026-07-07):** KhaozEngine is a 3D engine first. Ruinborne is the flagship 3D
consumer and sets the bar: "A"-tier semi-realistic fidelity with a light stylization accent, deliberately not
AAA. The other games consume the same stack for 2D/2.5D. Consequences: the cel/palette/retro post path stays a
per-game option, not the engine identity, and the items below are ordered by what closes the gap to that
"A" semi-realistic bar. Explicitly out of scope for that bar (do not build without a concrete game pull): TAA,
global illumination, a deferred renderer, full PBR+IBL, occlusion culling, GPU-driven rendering, GPU particles.

Ordered gap list (2026-07-07 feature audit):

1. Shadows: the single biggest visual gap for the semi-realistic bar (characters and props float with no
   grounding). Plan: a `ShadowMode` quality tier - blob shadows (cheap grounding, low-end fallback) and a
   key-light shadow map with PCF filtering. The MRT / depth infrastructure already exists, and the single-sourced
   `LightingCommonGlsl` block (10.17.1) means the shadow term lands in one place for model + terrain.
2. Cross-pass transparent ordering (follow-up to the shipped within-batch sort, 10.18.2): alpha-blended draws
   now sort back-to-front within each batch, but the inter-pass order of the transparent renderers is still
   fixed by Scene3D. A unified cross-renderer transparent queue is the eventual answer if a game hits a
   cross-pass ordering artifact. Also unpinned: a golden for mixed additive+alpha textured overlap.
3. Frustum culling: only distance culling exists today (prop radius, chunk ring, LOD tiers, N-nearest lights).
   `TerrainChunkBounds` already builds AABBs labeled for frustum culling but nothing extracts frustum planes or
   tests them. Plane extraction + AABB tests for chunks, props, and instanced meshes is a cheap, large perf win
   for the MMO overworld.
4. Sky: no skybox, cubemap, or environment support. The only sky is the procedural screen-space starfield in the
   blit pass (right for the space games, wrong for the Ruinborne overworld). A sky gradient/skybox with a sun
   direction that agrees with the key light pairs with shadows for the cohesive-look pass.
5. Water: a real water surface shader for the lake / sea (currently a flat plane at water level, and the splat
   weights already know the waterline). After shadows + sky so it has something to reflect the look of.
6. Bloom (LDR): beams, glow, and emissive read flat without it. The internal target is RGBA8 (no HDR pipeline),
   and a threshold+blur LDR bloom is enough for the target fidelity. A full HDR/tonemap pipeline is not planned.
7. Layered / masked animation blending: the animation stack does clip playback + single crossfade + a locomotion
   state machine. Upper-body actions over locomotion (attack while running) will be needed by Ruinborne combat,
   and additive layers / bone masks are the next step. Skeletal IK (foot placement) sits behind it.
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

- Input action maps + rebinding: bindings are hardcoded in `InputManager` today. An action-map layer (named
  actions, per-player bindings, runtime rebinding UI support, serialization via the existing settings storage)
  is a baseline modern-player expectation for every game on the engine.
- Gamepad rumble / haptics: analog sticks and triggers are in, vibration is not exposed at all.
- Music crossfade: music playback is single-track with a hard cut on track change. A short crossfade between
  tracks (and into/out of combat layers later) is cheap and immediately audible.
- Audio buses: the volume model is Master x Music/Sfx only. A small bus/category graph (UI, ambience, combat)
  gives games mix control without per-voice bookkeeping.
- Rich text markup: `TextLayout` does wrap + alignment only. Inline color/style markup (localized strings
  need it for emphasis and key names) for chat, tooltips, and dialogue.

## Tooling & developer experience

- `.resx` -> `StringId` source generator: compile-time localization enforcement shipped (`StringId` /
  `LocalizedText` + the `Localization.Analyzers` analyzer), but consumers still hand-author the `StringId`
  constants that mirror their `.resx` keys. A Roslyn source generator could emit those constants from the
  `.resx` at build time, removing the hand-maintained constants class and the key-drift risk.
- GPU timers: the diagnostics overlay shipped (F1 panel, `FrameStats`, telemetry JSONL) but all timings are CPU
  frame deltas. Backend timestamp queries would attribute frame cost to passes (shadow, main, post) and make
  perf work on the "A"-graphics items measurable.
- Asset hot-reload: reload meshes, textures, and shaders at runtime during development. The prop asset pipeline
  shipped, but hot-reload did not.

## Possible future factoring (unscheduled)

- Shared 2D/3D particle sim core: `KhaozEngine.Particles` (3D) and `Render2D.Vfx.Particle2DSystem` (2D) share
  the emit/integrate/lerp-over-life model; a pass could factor the common sim core. No single target today.
- Nullwake camera convergence: Nullwake's `OreField.RefToScreen` is a non-uniform scale into a screen sub-rect,
  not `Camera2D`. Converging would need sub-viewport + non-uniform-scale support in the engine camera, else it
  stays game-specific.
