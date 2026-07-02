# KhaozEngine roadmap

Future work only: what's planned or missing, highest-priority first. This file does NOT record shipped
history. See [CHANGELOG.md](../CHANGELOG.md) and `git tag` for what landed and when. When an item ships,
delete it from here (the detail moves to the changelog) rather than marking it "done".

Current released version: **9.2.0** (the shared `<KhaozEngineVersion>` line in `Directory.Build.props`).

Each near-term item gets its own design spec + plan under `docs/superpowers/` when it is scheduled.

## Near-term (next up)

### 1. Engine-level Discord social SDK

Discord integration (rich presence, join/invite, lobbies, OAuth identity) is currently done bespoke per game.
Centralize it: a `KhaozEngine.Social` seam (`ISocialProvider`: presence, invites/friends, identity, optionally
achievements) with a `KhaozEngine.Social.Discord` implementation over the Discord Social SDK, so every game gets
Discord from the engine. Retire the game-specific Discord implementations once the package ships.

- Design the seam for extensibility: the provider interface should leave room for Steam / other platforms later
  behind the same contract.
- Native dependency: the Discord SDK ships native libraries, so the `.Discord` package pulls them while the
  `Social` core seam stays dependency-free, mirroring the opt-in-backend pattern (`Netcode.LiteNetLib`,
  `WorldStore.*`). Per-RID bundling is the same concern as Audio/Windowing.

### 2. Physics engine: dynamic bodies + constraints

The static-body physics seam (`IPhysicsWorld` + the opt-in `KhaozEngine.Physics.Bepu` backend) is in place,
and character movement collide-and-slides capsule-vs-mesh against it. What remains:

**Dynamic rigid bodies + their replication.** A body that falls / bounces / rests needs a replication component
(like `MovementState` for players); `Scene3DChunkSink` would drive `AddDynamic`/step per loaded chunk, and
`WorldClient` would interpolate dynamic-body positions from replicated snapshots. Enables physics-driven crates,
barrels, falling debris. Terrain-as-physics-geometry (the whole terrain mesh fed into a `TriangleMesh` body) also
lands here: a static terrain body replaces the `TerrainCollision` delegate in the `Step` call so all surfaces
(terrain + props + buildings) share one query path.

**Constraints, joints, and vehicles.** Hinges, sliders, ragdolls, wheeled vehicles.

### 3. Visual fidelity (textures + materials)

The terrain now renders PBR splat textures. Props still come in flat base-colour (the prop
loader flattens each material's texture to a single factor during ingest, so trees/rocks/buildings carry no
surface detail). Goal: make props, trees, and buildings actually look good, not just read as shapes.

- Textured props: per-material albedo / normal on meshes (trees, rocks, buildings) - needs the glTF loader /
  prop renderer to stop flattening textures to a base-colour factor (today's limitation; same area as the rigid
  node-transform fix).
- Water: a real water shader for the lake / sea (currently a flat plane at the water level).
- Lighting polish: pairs with shadows (see Rendering) + an HDRI/sky direction for a cohesive look.
- CC0-asset-friendly throughout (ambientCG terrain textures, the kit textures), no new heavy dependencies.

## Netcode / MMO refinements

- Delta + AoI unification: fold the interest-grid filtering into the delta encoder (one pass instead of two).
- Delta bit-packing / quantization: shrink per-snapshot bandwidth.
- SpaceGame as the first netcode adopter / testbed: validate the authoritative stack on a real game.

## Overworld / world content

- Procedural dungeon generator.
- Per-cell world-state snapshot persistence: persist cell/world state, not just player records (pairs with
  sharding).
- Animated-creature adoption (game-side, not engine work): the engine animation stack shipped (glTF
  animation-clip playback, `AnimatedCharacter` + locomotion blend, `ReplicatedCharacterAnimators`). SpaceGame's
  2.5D rigged-creature direction can adopt it directly. Only reopen an engine item here if a concrete new gain
  surfaces from that adoption (e.g. blend trees, additive layers, or IK the current player can't express).
- Visual fidelity (textured props, water): see Near-term item #3 above.

## Rendering

- Shadows: 3D shadow rendering (shadow maps). A named gap from the 5.x engine audit; the MRT / depth
  infrastructure already exists.
- Depth-sorted transparency (3D): transparent meshes and billboards currently render unsorted, so overlapping
  alpha is draw-order dependent. Sort back-to-front (or add order-independent transparency) for correct blending.

## Cross-platform reach

- Mobile: iOS / Android platform layers (lifecycle, touch, packaging, store submission). Silk.NET is the
  windowing/input foundation it builds on.
- GL backend verification: the `gl` Veldrid backend override parses but is unverified; waits on real GPU CI /
  hardware. See [CROSS-PLATFORM.md](CROSS-PLATFORM.md).
- Live gamepad smoke: polling is best-effort + compile-verified; needs an on-device pass with a physical
  controller.
- Richer text entry: IME / locale / dead-keys (the current `TextEntry` is US-layout key-mapping).

## Tooling & developer experience

- On-screen profiling / diagnostics overlay: a live frame-time / draw-call / memory overlay (logging only
  today). The Gui makes it cheap to build.
- Asset hot-reload: reload meshes, textures, and shaders at runtime during development. The prop asset pipeline
  shipped, but hot-reload did not.

## Possible future factoring (unscheduled)

- Shared 2D/3D particle sim core: `KhaozEngine.Particles` (3D) and `Render2D.Vfx.Particle2DSystem` (2D) share
  the emit/integrate/lerp-over-life model; a pass could factor the common sim core. No single target today.
- Nullwake camera convergence: Nullwake's `OreField.RefToScreen` is a non-uniform scale into a screen sub-rect,
  not `Camera2D`. Converging would need sub-viewport + non-uniform-scale support in the engine camera, else it
  stays game-specific (see [CONSUMERS.md](CONSUMERS.md)).
